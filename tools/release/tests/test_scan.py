# SPDX-License-Identifier: BSD-2-Clause-Patent
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import scan  # noqa: E402


def run(tmp_path, files, deny=(), allow=()):
    for rel, content in files.items():
        p = tmp_path / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(content, bytes):
            p.write_bytes(content)
        else:
            p.write_text(content, encoding="utf-8")
    return scan.scan(tmp_path, sorted(files), list(deny), list(allow))


def rules(findings):
    return sorted({f.rule for f in findings})


def test_clean_file_passes(tmp_path):
    assert run(tmp_path, {"a.c": "// SPDX-License-Identifier: BSD-2-Clause-Patent\nint x;\n"}) == []


def test_user_and_workspace_paths(tmp_path):
    f = run(tmp_path, {"a.md": "see C:\\Users\\alice\\x and E:\\Work\\y\n"})
    assert rules(f) == ["user-path", "workspace-path"]


def test_system_paths_allowed(tmp_path):
    assert run(tmp_path, {"a.ps1": "C:\\Windows\\System32 C:\\Users\\Public %LOCALAPPDATA%\\x\n"}) == []


def test_internal_and_private_symbol_terms(tmp_path):
    f = run(tmp_path, {"a.md": "fetched from symweb\nusing private symbols\ndxgkrnl+0x1234 and nt!KiFoo\n"})
    assert rules(f) == ["kernel-offset", "ms-internal", "private-symbols"]


def test_denylist_is_literal_and_case_insensitive(tmp_path):
    f = run(tmp_path, {"a.txt": "serial ABCDEF0123\n"}, deny=["abcdef0123"])
    assert rules(f) == ["denylist"]


def test_email_allows_noreply(tmp_path):
    ok = run(tmp_path, {"a.txt": "Co-authored-by: X <1+x@users.noreply.github.com>\n"})
    bad = run(tmp_path, {"b.txt": "mail me: someone@contoso.com\n"})
    assert ok == [] and rules(bad) == ["email"]


def test_binary_by_extension_and_content(tmp_path):
    f = run(tmp_path, {"x.sys": b"MZ", "y.dat": b"ab\0cd"})
    assert [x.rule for x in f] == ["binary", "binary"]


def test_gpl_only_inside_gpl_subtree(tmp_path):
    lic = "GNU General Public License version 2\n"
    hdr = "// SPDX-License-Identifier: GPL-2.0-only\n"
    f = run(tmp_path, {"gpl/LICENSE": lic, "gpl/t.c": hdr, "bsd/t.c": hdr})
    assert [(x.path, x.rule) for x in f] == [("bsd/t.c", "gpl")]


def test_allowlist_exempts_rule(tmp_path):
    f = run(tmp_path, {"docs/a.md": "mount W:\\Windows\n"}, allow=[("docs/*", "workspace-path")])
    assert f == []


def test_private_key(tmp_path):
    f = run(tmp_path, {"k.txt": "-----BEGIN RSA PRIVATE KEY-----\n"})
    assert rules(f) == ["private-key"]
