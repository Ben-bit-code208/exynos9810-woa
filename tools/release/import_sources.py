#!/usr/bin/env python3
# SPDX-License-Identifier: BSD-2-Clause-Patent
"""Import curated product sources from the private research repository.

The manifest (``release/import-manifest.json``) is the only thing that decides
what leaves the research tree. Each entry copies git-tracked files under
``from`` to ``to``; ``include``/``exclude`` globs are matched against the path
relative to ``from``. Files are read from a git ref (default ``HEAD``) so the
import is reproducible; ``--ref WORKTREE`` reads the working tree instead.

Each imported file then goes through two public-side transforms, so the public
edits survive every re-import:

1. ``resolve_macros`` (optional, per entry): ``{"NAME": 0|1}``. Simple
   ``#if NAME`` / ``#if !NAME`` blocks in C sources are resolved and the
   directives dropped, like ``unifdef``; ``"resolve_literals": true`` also
   resolves ``#if 0`` / ``#if 1``. Other conditionals are kept verbatim.
2. ``.release-patches/<entry-id>.patch``: a unified diff applied with
   ``git apply``. Edit the imported files, then run ``--make-patches`` to
   regenerate the patches from the difference. The removed lines are exactly
   the private text, so this directory is git-ignored and must never be
   published; back it up alongside the research repository.

Import refuses to overwrite a file whose content differs from what the last
import produced, so unrecorded public edits are never lost silently.

Previously imported files that are no longer selected are deleted, using the
file list in ``release/provenance.json``. Run ``tools/release/scan.py``
afterwards; nothing is published until it is clean.
"""
from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path, PurePosixPath

WORKTREE = "WORKTREE"
# Patches contain the removed private text, so they stay local (git-ignored).
PATCH_DIR = ".release-patches"
C_SUFFIXES = {".c", ".h", ".cpp", ".hpp", ".inc", ".dsc", ".dec", ".inf", ".fdf"}
_DIRECTIVE = re.compile(rb"^[ \t]*#[ \t]*(if|ifdef|ifndef|elif|else|endif)\b(.*?)\r?\n?$")
_SIMPLE_IF = re.compile(rb"^\s*(!?)\s*([A-Za-z_][A-Za-z0-9_]*|[01])\s*(?:/[/*].*)?$")


def _ends_in_comment(line: bytes, in_comment: bool) -> bool:
    """Track /* */ state across a line (string literals are not special-cased)."""
    i = 0
    while i < len(line):
        pair = line[i:i + 2]
        if in_comment:
            if pair == b"*/":
                in_comment, i = False, i + 2
                continue
        elif pair == b"//":
            break
        elif pair == b"/*":
            in_comment, i = True, i + 2
            continue
        i += 1
    return in_comment


def resolve_macros(data: bytes, macros: dict[str, int], name: str = "<file>",
                   literals: bool = False) -> bytes:
    """Resolve ``#if NAME``/``#if !NAME`` blocks (and ``#if 0``/``#if 1`` if ``literals``)."""
    out: list[bytes] = []
    # Each frame: (resolved, keep_this_branch); passthrough frames have resolved=False.
    stack: list[tuple[bool, bool]] = []

    def active() -> bool:
        return all(keep for resolved, keep in stack if resolved)

    in_comment = False
    for n, line in enumerate(data.splitlines(keepends=True), 1):
        m = None if in_comment else _DIRECTIVE.match(line)
        in_comment = _ends_in_comment(line, in_comment)
        if not m:
            if active():
                out.append(line)
            continue
        kind, rest = m.group(1), m.group(2)
        if kind == b"if":
            s = _SIMPLE_IF.match(rest)
            macro = s.group(2).decode() if s else None
            if macro in ("0", "1") and literals:
                macros = {**macros, macro: int(macro)}
            if macro in macros:
                value = bool(macros[macro]) != (s.group(1) == b"!")
                stack.append((True, value))
                continue
            stack.append((False, True))
        elif kind in (b"ifdef", b"ifndef"):
            stack.append((False, True))
        elif not stack:
            raise ValueError(f"{name}:{n}: unbalanced #{kind.decode()}")
        elif kind == b"elif":
            if stack[-1][0]:
                raise ValueError(f"{name}:{n}: #elif in a resolved block is not supported")
        elif kind == b"else":
            resolved, keep = stack[-1]
            if resolved:
                stack[-1] = (True, not keep)
                continue
        else:  # endif
            resolved, _ = stack.pop()
            if resolved:
                continue
        if active():
            out.append(line)
    if stack:
        raise ValueError(f"{name}: unterminated #if")
    return b"".join(out)


def transform(entry: dict, dest: str, data: bytes) -> bytes:
    macros = entry.get("resolve_macros", {})
    literals = bool(entry.get("resolve_literals"))
    if (macros or literals) and PurePosixPath(dest).suffix.lower() in C_SUFFIXES:
        data = resolve_macros(data, macros, dest, literals)
    return data


def patch_path(repo: Path, entry_id: str) -> Path:
    return repo / PATCH_DIR / f"{entry_id}.patch"


def apply_patch(repo: Path, patch: Path) -> None:
    r = subprocess.run(["git", "-c", "core.autocrlf=false", "apply", "--whitespace=nowarn", str(patch)], cwd=repo,
                       capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit(f"{patch.relative_to(repo)} no longer applies; fix it against the new "
                         f"import and rerun --make-patches:\n{r.stderr.strip()}")


def make_patch(repo: Path, entry_id: str, pristine: dict[str, bytes]) -> bool:
    """Write .release-patches/<entry>.patch = diff(pristine -> current). Returns True if non-empty."""
    with tempfile.TemporaryDirectory() as tmp:
        a, b = Path(tmp, "a"), Path(tmp, "b")
        for dest, data in pristine.items():
            (a / dest).parent.mkdir(parents=True, exist_ok=True)
            (a / dest).write_bytes(data)
            cur = repo / dest
            if cur.exists():
                (b / dest).parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(cur, b / dest)
        b.mkdir(exist_ok=True)
        r = subprocess.run(["git", "-c", "core.autocrlf=false", "diff", "--no-index", "--no-prefix",
                            "--binary", "--no-color", "a", "b"], cwd=tmp, capture_output=True)
        if r.returncode not in (0, 1):
            raise SystemExit(r.stderr.decode(errors="replace"))
        diff = r.stdout
    target = patch_path(repo, entry_id)
    if not diff:
        target.unlink(missing_ok=True)
        return False
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(diff)
    return True


def git(src: Path, *args: str) -> bytes:
    return subprocess.run(["git", "-C", str(src), *args], check=True,
                          capture_output=True).stdout


def list_tracked(src: Path, ref: str, prefix: str) -> list[str]:
    if ref == WORKTREE:
        out = git(src, "ls-files", "-z", "--", prefix)
        return [f for f in out.decode().split("\0") if f and (src / f).is_file()]
    out = git(src, "ls-tree", "-r", "-z", "--name-only", ref, "--", prefix)
    return [f for f in out.decode().split("\0") if f]


def read_file(src: Path, ref: str, rel: str) -> bytes:
    if ref == WORKTREE:
        return (src / rel).read_bytes()
    return git(src, "show", f"{ref}:{rel}")


def match(rel: str, globs: list[str]) -> bool:
    return any(fnmatch.fnmatch(rel, g) or fnmatch.fnmatch(PurePosixPath(rel).name, g) for g in globs)


def plan(src: Path, default_ref: str, manifest: dict) -> dict[str, tuple[str, str, str]]:
    """Return {dest_rel: (source_rel, entry_id, ref)}; an entry may pin its own ``ref``."""
    result: dict[str, tuple[str, str, str]] = {}
    for entry in manifest["entries"]:
        ref = entry.get("ref", default_ref)
        base = entry["from"].rstrip("/")
        dest = entry["to"].rstrip("/")
        include = entry.get("include", ["*"])
        exclude = entry.get("exclude", []) + manifest.get("global_exclude", [])
        files = list_tracked(src, ref, base)
        if not files:
            raise SystemExit(f"manifest entry {entry['id']!r}: nothing tracked under {base!r} at {ref}")
        for f in files:
            rel = f[len(base) + 1:] if f != base else PurePosixPath(f).name
            if not match(rel, include) or match(rel, exclude):
                continue
            target = f"{dest}/{rel}" if f != base else dest
            if target in result:
                raise SystemExit(f"{target}: selected by both {result[target][1]!r} and {entry['id']!r}")
            result[target] = (f, entry["id"], ref)
    return result


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--source", required=True, type=Path, help="research repository root")
    ap.add_argument("--ref", default="HEAD", help=f"git ref to import, or {WORKTREE}")
    ap.add_argument("--repo", default=Path(__file__).resolve().parents[2], type=Path)
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--make-patches", action="store_true",
                    help="record current public edits into .release-patches instead of importing")
    ap.add_argument("--force", action="store_true", help="discard unrecorded public edits")
    args = ap.parse_args(argv)

    repo = args.repo.resolve()
    src = args.source.resolve()
    manifest = json.loads((repo / "release" / "import-manifest.json").read_text(encoding="utf-8"))
    prov_path = repo / "release" / "provenance.json"
    previous = json.loads(prov_path.read_text(encoding="utf-8")) if prov_path.exists() else {"files": {}}

    selected = plan(src, args.ref, manifest)
    commit = git(src, "rev-parse", "HEAD" if args.ref == WORKTREE else args.ref).decode().strip()
    worktree_used = any(r == WORKTREE for _, _, r in selected.values())
    dirty = worktree_used and bool(git(src, "status", "--porcelain", "--untracked-files=no").strip())

    stale = sorted(set(previous["files"]) - set(selected))
    print(f"import: {len(selected)} files from {commit[:12]}{' (dirty worktree)' if dirty else ''}, "
          f"{len(stale)} stale", file=sys.stderr)
    if args.dry_run:
        for dest, (s, eid, ref) in sorted(selected.items()):
            print(f"{eid:24} {ref[:12]:12} {s} -> {dest}")
        for dest in stale:
            print(f"{'(remove)':24} {dest}")
        return 0

    entries = {e["id"]: e for e in manifest["entries"]}
    pristine: dict[str, dict[str, bytes]] = {}
    for dest, (s, eid, ref) in sorted(selected.items()):
        pristine.setdefault(eid, {})[dest] = transform(entries[eid], dest, read_file(src, ref, s))

    def sha(data: bytes) -> str:
        return hashlib.sha256(data).hexdigest()

    def published(dest: str) -> str | None:
        rec = previous["files"].get(dest)
        return rec and rec.get("published_sha256", rec.get("sha256"))

    if args.make_patches:
        for eid in entries:
            if make_patch(repo, eid, pristine.get(eid, {})):
                print(f"wrote {patch_path(repo, eid).relative_to(repo)}", file=sys.stderr)
    else:
        edited = [d for d in sorted(set(selected) | set(stale)) if (repo / d).exists()
                  and published(d) is not None and sha((repo / d).read_bytes()) != published(d)]
        if edited and not args.force:
            raise SystemExit("public edits not recorded in .release-patches (run --make-patches "
                             "or pass --force to discard):\n  " + "\n  ".join(edited))
        for dest in stale:
            (repo / dest).unlink(missing_ok=True)
        for group in pristine.values():
            for dest, data in group.items():
                out = repo / dest
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_bytes(data)
        for eid in entries:
            if patch_path(repo, eid).exists():
                apply_patch(repo, patch_path(repo, eid))

    files = {}
    for dest, (s, eid, ref) in sorted(selected.items()):
        out = repo / dest
        files[dest] = {"entry": eid, "ref": ref, "sha256": sha(pristine[eid][dest]),
                       "published_sha256": sha(out.read_bytes()) if out.exists() else None}
    if args.make_patches:
        # Nothing was deleted, so the next import must still see the stale files.
        files.update({d: previous["files"][d] for d in stale})
    prov = {"source_commit": commit, "source_dirty": dirty, "files": files}
    prov_path.write_text(json.dumps(prov, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
