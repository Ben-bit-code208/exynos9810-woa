# SPDX-License-Identifier: BSD-2-Clause-Patent
import json
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import import_sources  # noqa: E402


def sh(cwd, *a):
    subprocess.run(["git", "-C", str(cwd), *a], check=True, capture_output=True)


def make_source(tmp_path):
    src = tmp_path / "src"
    (src / "fw" / "sub").mkdir(parents=True)
    (src / "fw" / "a.c").write_text("a")
    (src / "fw" / "sub" / "b.h").write_text("b")
    (src / "fw" / "junk.log").write_text("x")
    (src / "one.ps1").write_text("p")
    sh(src, "init", "-q")
    sh(src, "add", ".")
    sh(src, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-qm", "i")
    return src


def make_repo(tmp_path, entries):
    repo = tmp_path / "pub"
    (repo / "release").mkdir(parents=True)
    (repo / "release" / "import-manifest.json").write_text(
        json.dumps({"global_exclude": ["*.log"], "entries": entries}))
    return repo


def test_import_prunes_and_records(tmp_path):
    src = make_source(tmp_path)
    repo = make_repo(tmp_path, [
        {"id": "fw", "from": "fw", "to": "firmware"},
        {"id": "one", "from": "one.ps1", "to": "tools/one.ps1"},
    ])
    args = ["--source", str(src), "--repo", str(repo)]
    assert import_sources.main(args) == 0
    assert (repo / "firmware/sub/b.h").read_text() == "b"
    assert (repo / "tools/one.ps1").read_text() == "p"
    assert not (repo / "firmware/junk.log").exists()
    prov = json.loads((repo / "release/provenance.json").read_text())
    assert set(prov["files"]) == {"firmware/a.c", "firmware/sub/b.h", "tools/one.ps1"}

    manifest = json.loads((repo / "release/import-manifest.json").read_text())
    manifest["entries"][0]["exclude"] = ["sub/*"]
    (repo / "release/import-manifest.json").write_text(json.dumps(manifest))
    assert import_sources.main(args) == 0
    assert not (repo / "firmware/sub/b.h").exists()


def test_import_reads_committed_content_not_worktree(tmp_path):
    src = make_source(tmp_path)
    (src / "fw" / "a.c").write_text("uncommitted")
    repo = make_repo(tmp_path, [{"id": "fw", "from": "fw", "to": "firmware"}])
    import_sources.main(["--source", str(src), "--repo", str(repo)])
    assert (repo / "firmware/a.c").read_text() == "a"
    import_sources.main(["--source", str(src), "--repo", str(repo), "--ref", "WORKTREE"])
    assert (repo / "firmware/a.c").read_text() == "uncommitted"
    assert json.loads((repo / "release/provenance.json").read_text())["source_dirty"] is True


def test_entry_ref_override(tmp_path):
    src = make_source(tmp_path)
    (src / "one.ps1").write_text("new")
    repo = make_repo(tmp_path, [
        {"id": "fw", "from": "fw", "to": "firmware"},
        {"id": "one", "from": "one.ps1", "to": "one.ps1", "ref": "WORKTREE"},
    ])
    (src / "fw" / "a.c").write_text("dirty")
    import_sources.main(["--source", str(src), "--repo", str(repo)])
    prov = json.loads((repo / "release/provenance.json").read_text())
    assert (repo / "one.ps1").read_text() == "new"
    assert (repo / "firmware/a.c").read_text() == "a"
    assert prov["files"]["one.ps1"]["ref"] == "WORKTREE" and prov["source_dirty"] is True


def test_resolve_macros():
    src = (b"a\n#if FOO\nfoo\n#else\nnofoo\n#endif\n#if !FOO\nnot\n#endif\n"
           b"#ifdef BAR\n#if FOO // c\nnested\n#endif\n#else\nb\n#endif\n#if OTHER\no\n#endif\n")
    out = import_sources.resolve_macros(src, {"FOO": 1})
    assert out == b"a\nfoo\n#ifdef BAR\nnested\n#else\nb\n#endif\n#if OTHER\no\n#endif\n"
    assert import_sources.resolve_macros(b"#if FOO\r\nx\r\n#endif\r\ny\r\n", {"FOO": 0}) == b"y\r\n"


def test_patches_survive_reimport(tmp_path):
    src = make_source(tmp_path)
    (src / "fw" / "m.c").write_text("#if P\nkeep\n#else\ndrop\n#endif\nsecret\n")
    sh(src, "add", ".")
    sh(src, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-qm", "m")
    repo = make_repo(tmp_path, [{"id": "fw", "from": "fw", "to": "firmware", "resolve_macros": {"P": 1}}])
    sh(repo, "init", "-q")
    args = ["--source", str(src), "--repo", str(repo)]
    assert import_sources.main(args) == 0
    m = repo / "firmware/m.c"
    assert m.read_text() == "keep\nsecret\n"

    m.write_text("keep\nredacted\n")
    try:
        import_sources.main(args)
        raise AssertionError("unrecorded edit must block import")
    except SystemExit as e:
        assert "firmware/m.c" in str(e)
    assert import_sources.main(args + ["--make-patches"]) == 0
    assert (repo / ".release-patches/fw.patch").exists()

    (src / "fw" / "a.c").write_text("a2")
    sh(src, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-qam", "u")
    assert import_sources.main(args) == 0
    assert m.read_text() == "keep\nredacted\n"
    assert (repo / "firmware/a.c").read_text() == "a2"


def test_make_patches_keeps_stale_files_for_next_import(tmp_path):
    src = make_source(tmp_path)
    repo = make_repo(tmp_path, [{"id": "fw", "from": "fw", "to": "firmware"}])
    sh(repo, "init", "-q")
    args = ["--source", str(src), "--repo", str(repo)]
    import_sources.main(args)
    manifest = json.loads((repo / "release/import-manifest.json").read_text())
    manifest["entries"][0]["exclude"] = ["a.c"]
    (repo / "release/import-manifest.json").write_text(json.dumps(manifest))
    import_sources.main(args + ["--make-patches"])
    assert (repo / "firmware/a.c").exists()
    import_sources.main(args)
    assert not (repo / "firmware/a.c").exists()


def test_resolve_literals():
    src = b"a\n#if 0 // old\nx\n#else\ny\n#endif\n#if 1\nz\n#endif\n"
    assert import_sources.resolve_macros(src, {}) == src
    assert import_sources.resolve_macros(src, {}, literals=True) == b"a\ny\nz\n"


def test_directives_inside_block_comments_are_ignored():
    src = b"/** @file\n#if FOO\n*/\n#if FOO /* c */\nx\n#endif\n"
    assert import_sources.resolve_macros(src, {"FOO": 0}) == b"/** @file\n#if FOO\n*/\n"
