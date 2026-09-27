#!/usr/bin/env python3
# SPDX-License-Identifier: BSD-2-Clause-Patent
"""Release gate: refuse to publish files that leak private or restricted material.

Rules are generic so that the scanner itself contains nothing sensitive.
Device serials, user names and other literal terms belong in a local,
git-ignored denylist (default: ``.release-denylist.txt`` at the repo root,
one case-insensitive literal per line, ``#`` comments allowed).
"""
from __future__ import annotations

import argparse
import fnmatch
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

MAX_BYTES = 1 << 20

BINARY_EXTENSIONS = {
    ".sys", ".dll", ".exe", ".efi", ".wim", ".esd", ".cab", ".msu", ".img",
    ".bin", ".fd", ".dtb", ".ko", ".so", ".pdb", ".obj", ".lib", ".cat",
    ".pfx", ".pvk", ".p12", ".pem", ".key", ".cer", ".crt", ".vhd", ".vhdx",
    ".zip", ".7z", ".gz", ".xz", ".tar", ".tar.gz", ".dmp", ".evtx",
    ".hiv", ".iso", ".tar.md5",
}

GPL_SPDX = re.compile(r"SPDX-License-Identifier:\s*[^\n]*\b(L?GPL)", re.I)
GPL_TEXT = re.compile(r"GNU (Lesser |Library )?General Public License", re.I)


@dataclass(frozen=True)
class Rule:
    name: str
    pattern: re.Pattern[str]
    hint: str


RULES = [
    Rule("user-path", re.compile(r"\b[A-Za-z]:[\\/]+Users[\\/]+(?!Public\b)[^\\/\s\"'<>]+", re.I),
         "absolute user-profile path"),
    Rule("unix-home", re.compile(r"(?<![\w$])/home/(?!user\b|builder\b)[a-z_][\w.-]*/"),
         "absolute home path"),
    Rule("workspace-path", re.compile(r"\b[D-Zd-z]:[\\/]+(?!\$)[A-Za-z0-9_.-]+"),
         "absolute path on a non-system drive"),
    Rule("unc-path", re.compile(r"(?<![\\\w])\\\\[A-Za-z0-9][\w.-]+\\[\w$.-]+"),
         "UNC network share"),
    Rule("ms-internal", re.compile(
        r"symweb|\bntdev\b|\.corp\.microsoft|\bredmond\b|\brazzle\b|\bos\.20\d\d\b|"
        r"microsoft confidential|azurefd\.net|\bmsft-internal\b", re.I),
         "Microsoft-internal host, build or classification"),
    Rule("private-symbols", re.compile(r"private[\s_-]*(symbol|pdb)s?", re.I),
         "reference to private symbols"),
    Rule("kernel-offset", re.compile(
        r"\b(nt|ntoskrnl|dxgkrnl|dxgmms[12]|hal|storport|winload|bootmgfw|ci)"
        r"(\.exe|\.sys|\.dll|\.efi)?\s*(!\s*\w|\+\s*0x[0-9a-f]+)|\bRVA\b", re.I),
         "Microsoft binary offset/RVA or private-symbol-style reference"),
    Rule("copilot-session", re.compile(r"session-state|\.copilot[\\/]", re.I),
         "agent session artifact reference"),
    Rule("email", re.compile(
        r"[A-Za-z0-9._%+-]+@(?!(?:users\.noreply\.github\.com|example\.(?:com|org))\b)"
        r"[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}"),
         "e-mail address"),
    Rule("private-key", re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
         "private key material"),
    Rule("secret-assign", re.compile(
        r"(password|passwd|secret|api[_-]?key|token)\s*[:=]\s*['\"][^'\"\s]{8,}['\"]", re.I),
         "hard-coded secret"),
]


@dataclass(frozen=True)
class Finding:
    path: str
    line: int
    rule: str
    detail: str

    def __str__(self) -> str:
        return f"{self.path}:{self.line}: [{self.rule}] {self.detail}"


def load_list(path: Path) -> list[str]:
    if not path.is_file():
        return []
    out = []
    for raw in path.read_text(encoding="utf-8").splitlines():
        s = raw.strip()
        if s and not s.startswith("#"):
            out.append(s)
    return out


def load_allow(path: Path) -> list[tuple[str, str]]:
    """Lines of ``<glob> <rule>``; exempts a rule for matching files."""
    pairs = []
    for s in load_list(path):
        parts = s.split()
        if len(parts) >= 2:
            pairs.append((parts[0], parts[1]))
    return pairs


def is_allowed(rel: str, rule: str, allow: list[tuple[str, str]]) -> bool:
    return any(fnmatch.fnmatch(rel, g) and (r == rule or r == "*") for g, r in allow)


def gpl_roots(root: Path, files: list[str]) -> set[str]:
    """Directories that declare a GPL licence with their own LICENSE file."""
    roots = set()
    for rel in files:
        p = Path(rel)
        if p.name.upper() in {"LICENSE", "LICENSE.TXT", "COPYING"} and len(p.parts) > 1:
            try:
                text = (root / rel).read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            if GPL_TEXT.search(text) or GPL_SPDX.search(text):
                roots.add(p.parent.as_posix() + "/")
    return roots


def list_files(root: Path) -> list[str]:
    try:
        out = subprocess.run(
            ["git", "-C", str(root), "ls-files", "-co", "--exclude-standard", "-z"],
            check=True, capture_output=True).stdout.decode("utf-8")
        return sorted(f for f in out.split("\0") if f and (root / f).is_file())
    except (OSError, subprocess.CalledProcessError):
        return sorted(p.relative_to(root).as_posix() for p in root.rglob("*")
                      if p.is_file() and ".git" not in p.parts)


def scan(root: Path, files: list[str], deny: list[str],
         allow: list[tuple[str, str]]) -> list[Finding]:
    findings: list[Finding] = []
    deny_re = [re.compile(re.escape(d), re.I) for d in deny]
    gpl_dirs = gpl_roots(root, files)
    for rel in files:
        path = root / rel
        lower = rel.lower()
        if any(lower.endswith(ext) for ext in BINARY_EXTENSIONS) and not is_allowed(rel, "binary", allow):
            findings.append(Finding(rel, 0, "binary", "binary/archive/key extension"))
            continue
        size = path.stat().st_size
        if size > MAX_BYTES and not is_allowed(rel, "size", allow):
            findings.append(Finding(rel, 0, "size", f"{size} bytes exceeds {MAX_BYTES}"))
            continue
        data = path.read_bytes()
        if b"\0" in data[:8192] and not is_allowed(rel, "binary", allow):
            findings.append(Finding(rel, 0, "binary", "file contains NUL bytes"))
            continue
        text = data.decode("utf-8", errors="replace")
        in_gpl = any(rel.startswith(d) for d in gpl_dirs)
        for no, line in enumerate(text.splitlines(), 1):
            for d in deny_re:
                if d.search(line):
                    findings.append(Finding(rel, no, "denylist", "matches a local denylist term"))
            for rule in RULES:
                m = rule.pattern.search(line)
                if m and not is_allowed(rel, rule.name, allow):
                    findings.append(Finding(rel, no, rule.name, f"{rule.hint}: {m.group(0)[:60]!r}"))
            if not in_gpl and (GPL_SPDX.search(line) or GPL_TEXT.search(line)) \
                    and not is_allowed(rel, "gpl", allow):
                findings.append(Finding(rel, no, "gpl",
                                        "GPL marker outside a directory with its own GPL LICENSE"))
    return findings


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("root", nargs="?", default=".", type=Path)
    ap.add_argument("--denylist", type=Path, default=None,
                    help="literal terms file (default: <root>/.release-denylist.txt, "
                         "or $RELEASE_DENYLIST)")
    ap.add_argument("--allow", type=Path, default=None,
                    help="exemptions file (default: <root>/release/scan-allow.txt)")
    ap.add_argument("paths", nargs="*", help="limit to these repo-relative files")
    args = ap.parse_args(argv)

    root = args.root.resolve()
    deny_path = args.denylist or Path(os.environ.get("RELEASE_DENYLIST", root / ".release-denylist.txt"))
    allow_path = args.allow or root / "release" / "scan-allow.txt"
    files = list_files(root)
    if args.paths:
        wanted = {Path(p).as_posix() for p in args.paths}
        files = [f for f in files if f in wanted]
    files = [f for f in files if f not in {".release-denylist.txt"}]
    deny = load_list(deny_path)
    findings = scan(root, files, deny, load_allow(allow_path))
    for f in findings:
        print(f)
    note = "" if deny else " (no local denylist loaded)"
    print(f"release-scan: {len(files)} files, {len(findings)} findings{note}", file=sys.stderr)
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
