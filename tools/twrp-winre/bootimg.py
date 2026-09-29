# SPDX-License-Identifier: BSD-2-Clause-Patent
"""Android boot image + newc cpio + LZMA round-trip helpers for the WinRE build.

Everything is done in memory. The ramdisk is never written to the host
filesystem: Windows cannot represent its symlinks, uid/gid or permission bits,
so extracting and re-archiving would silently corrupt the recovery. Parsing to a
list of entries and re-serialising keeps every byte we do not deliberately touch
identical, which is what lets build.py refuse to ship an image that does not
round-trip.

This is the reference implementation. The installer ships a byte-compatible C#
port (S9Woa.Installer.Core.Twrp.AndroidBootImage / CpioArchive) so end users
build the recovery on their own PC without Python; the two are kept in step by
the tests on both sides.
"""

from __future__ import annotations

import hashlib
import lzma
import struct
from dataclasses import dataclass, field

BOOT_MAGIC = b"ANDROID!"
CPIO_MAGIC = b"070701"
CPIO_TRAILER = "TRAILER!!!"

S_IFMT = 0o170000
S_IFREG = 0o100000
S_IFDIR = 0o040000
S_IFLNK = 0o120000


def _pad(n: int, page: int) -> int:
    return (n + page - 1) // page * page


@dataclass
class BootImage:
    """A Samsung-style (header v0 + trailing DTBH) Android boot image."""

    header: bytearray
    kernel: bytes
    ramdisk: bytes
    second: bytes
    dt: bytes
    tail: bytes = b""

    @property
    def page_size(self) -> int:
        return struct.unpack_from("<I", self.header, 0x24)[0]

    @property
    def dt_size(self) -> int:
        return struct.unpack_from("<I", self.header, 0x28)[0]

    @classmethod
    def parse(cls, blob: bytes) -> "BootImage":
        if blob[:8] != BOOT_MAGIC:
            raise ValueError(f"not an Android boot image: {blob[:8]!r}")
        (
            kernel_size,
            _kernel_addr,
            ramdisk_size,
            _ramdisk_addr,
            second_size,
            _second_addr,
            _tags_addr,
            page_size,
            dt_size,
        ) = struct.unpack_from("<9I", blob, 8)

        off = page_size
        kernel = blob[off:off + kernel_size]
        off += _pad(kernel_size, page_size)
        ramdisk = blob[off:off + ramdisk_size]
        off += _pad(ramdisk_size, page_size)
        second = blob[off:off + second_size]
        off += _pad(second_size, page_size)
        # Header v0's 0x28 word is repurposed by Samsung as dt_size; the blob
        # that follows starts with 'DTBH'. Treating it as header_version would
        # silently drop the device trees.
        dt = blob[off:off + dt_size]
        off += _pad(dt_size, page_size)

        return cls(
            header=bytearray(blob[:page_size]),
            kernel=kernel,
            ramdisk=ramdisk,
            second=second,
            dt=dt,
            tail=blob[off:],
        )

    def compute_id(self) -> bytes:
        sha = hashlib.sha1()
        for part in (self.kernel, self.ramdisk, self.second):
            sha.update(part)
            sha.update(struct.pack("<I", len(part)))
        if self.dt:
            sha.update(self.dt)
            sha.update(struct.pack("<I", len(self.dt)))
        return sha.digest()

    def serialize(self, refresh_id: bool = True, keep_tail: bool = True) -> bytes:
        page = self.page_size
        struct.pack_into("<I", self.header, 0x08, len(self.kernel))
        struct.pack_into("<I", self.header, 0x10, len(self.ramdisk))
        struct.pack_into("<I", self.header, 0x18, len(self.second))
        struct.pack_into("<I", self.header, 0x28, len(self.dt))
        if refresh_id:
            self.header[0x240:0x254] = self.compute_id()
            self.header[0x254:0x260] = b"\0" * 12

        out = bytearray(self.header)
        out += b"\0" * (page - len(self.header))
        for part in (self.kernel, self.ramdisk, self.second, self.dt):
            out += part
            out += b"\0" * (_pad(len(part), page) - len(part))
        if keep_tail:
            out += self.tail
        return bytes(out)


@dataclass
class CpioEntry:
    name: str
    mode: int
    ino: int = 0
    uid: int = 0
    gid: int = 0
    nlink: int = 1
    mtime: int = 0
    devmajor: int = 0
    devminor: int = 0
    rdevmajor: int = 0
    rdevminor: int = 0
    data: bytes = b""

    @property
    def is_dir(self) -> bool:
        return self.mode & S_IFMT == S_IFDIR

    @property
    def is_symlink(self) -> bool:
        return self.mode & S_IFMT == S_IFLNK


@dataclass
class Cpio:
    entries: list[CpioEntry] = field(default_factory=list)
    trailer: CpioEntry | None = None

    def index(self, name: str) -> int:
        for i, e in enumerate(self.entries):
            if e.name == name:
                return i
        return -1

    def get(self, name: str) -> CpioEntry | None:
        i = self.index(name)
        return self.entries[i] if i >= 0 else None

    def put_file(self, name: str, data: bytes, mode: int = 0o644) -> None:
        """Replace an existing file's payload, or append a new regular file.

        A replacement inherits the original mode/uid/gid so injecting a theme
        file cannot accidentally change its permissions.
        """
        i = self.index(name)
        if i >= 0:
            self.entries[i].data = data
            return
        template = self.entries[0]
        self.entries.append(
            CpioEntry(
                name=name,
                mode=S_IFREG | mode,
                ino=max((e.ino for e in self.entries), default=0) + 1,
                uid=template.uid,
                gid=template.gid,
                mtime=template.mtime,
                data=data,
            )
        )

    def remove(self, name: str) -> bool:
        i = self.index(name)
        if i >= 0:
            del self.entries[i]
            return True
        return False

    @classmethod
    def parse(cls, blob: bytes) -> "Cpio":
        entries: list[CpioEntry] = []
        trailer: CpioEntry | None = None
        off = 0
        while off < len(blob):
            if blob[off:off + 6] != CPIO_MAGIC:
                # Some builders pad the archive tail with NULs to a block
                # boundary; anything else is a real parse failure.
                if not blob[off:].strip(b"\0"):
                    break
                raise ValueError(f"bad cpio magic at 0x{off:x}: {blob[off:off + 6]!r}")
            f = [int(blob[off + 6 + i * 8: off + 14 + i * 8], 16) for i in range(13)]
            (
                ino, mode, uid, gid, nlink, mtime, filesize,
                devmajor, devminor, rdevmajor, rdevminor, namesize, _check,
            ) = f
            name_off = off + 110
            name = blob[name_off:name_off + namesize - 1].decode("utf-8", "surrogateescape")
            data_off = _pad(name_off + namesize, 4)
            data = blob[data_off:data_off + filesize]
            off = _pad(data_off + filesize, 4)
            entry = CpioEntry(
                name=name, mode=mode, ino=ino, uid=uid, gid=gid, nlink=nlink, mtime=mtime,
                devmajor=devmajor, devminor=devminor,
                rdevmajor=rdevmajor, rdevminor=rdevminor, data=data,
            )
            if name == CPIO_TRAILER:
                # Kept verbatim: this builder writes a non-zero ino and mode 0755
                # in the trailer, and synthesising it would break byte-exactness.
                trailer = entry
                break
            entries.append(entry)
        return cls(entries, trailer)

    def serialize(self) -> bytes:
        out = bytearray()
        for e in self.entries:
            out += self._pack(e, e.ino)
        trailer = self.trailer or CpioEntry(name=CPIO_TRAILER, mode=0o755, nlink=1)
        out += self._pack(trailer, trailer.ino)
        # The kernel's initramfs unpacker wants the archive to end on a 512-byte
        # boundary; every stock Android ramdisk is padded this way.
        out += b"\0" * (_pad(len(out), 512) - len(out))
        return bytes(out)

    @staticmethod
    def _pack(e: CpioEntry, ino: int) -> bytes:
        name = e.name.encode("utf-8", "surrogateescape") + b"\0"
        fields = [
            ino, e.mode, e.uid, e.gid, e.nlink, e.mtime, len(e.data),
            e.devmajor, e.devminor, e.rdevmajor, e.rdevminor, len(name), 0,
        ]
        # Lowercase hex: that is what this ramdisk's builder emitted, and using
        # uppercase would change thousands of bytes for no reason and destroy the
        # byte-exactness check that proves we only touched what we meant to.
        out = bytearray(CPIO_MAGIC)
        for v in fields:
            out += b"%08x" % (v & 0xFFFFFFFF)
        out += name
        out += b"\0" * (_pad(len(out), 4) - len(out))
        out += e.data
        out += b"\0" * (_pad(len(out), 4) - len(out))
        return bytes(out)


def lzma_decompress(blob: bytes) -> bytes:
    """Decompress a raw 'lzma alone' stream (0x5D header, unknown size)."""
    dec = lzma.LZMADecompressor(format=lzma.FORMAT_ALONE)
    return dec.decompress(blob)


def lzma_compress(blob: bytes, dict_size: int = 8 << 20) -> bytes:
    """Match the stock ramdisk's 'lzma alone' framing: lc=3 lp=0 pb=2, 8 MiB dict."""
    filters = [{
        "id": lzma.FILTER_LZMA1,
        "preset": 9 | lzma.PRESET_EXTREME,
        "dict_size": dict_size,
        "lc": 3, "lp": 0, "pb": 2,
    }]
    return lzma.compress(blob, format=lzma.FORMAT_ALONE, filters=filters)
