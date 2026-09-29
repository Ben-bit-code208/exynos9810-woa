# Vendored LZMA SDK

`LzmaAlone.cs` in this folder wraps the classic **7-Zip LZMA SDK** (managed C#)
so the installer can decompress and recompress the TWRP recovery ramdisk, which
is stored as an "LZMA alone" stream. .NET has no built-in LZMA codec, and the
kernel's initramfs unpacker expects exactly this format, so the codec is
vendored rather than reimplemented or pulled from a package.

* **Source:** the 7-Zip LZMA SDK by Igor Pavlov, https://www.7-zip.org/sdk.html
* **Licence:** public domain (the SDK is explicitly placed in the public domain
  by its author). The files carry `LicenseRef-PublicDomain` and are used
  verbatim except for two mechanical changes:
  1. the namespace root is changed to
     `S9Woa.Installer.Core.Twrp.Lzma.SevenZip`;
  2. each file starts with `#nullable disable` and `#pragma warning disable`
     so the decades-old source compiles cleanly under this project's
     `TreatWarningsAsErrors` and nullable settings.

`LzmaAlone` configures the encoder to reproduce the stock ramdisk header exactly
(`lc=3 lp=0 pb=2`, 8 MiB dictionary, property byte `0x5D`), so the output begins
`5D 00 00 80 00` like the original, and Python's `lzma.FORMAT_ALONE` (and the
device kernel) read it without change. Round-trip and Python-compatibility are
covered by the Core tests.
