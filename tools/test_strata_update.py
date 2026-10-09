"""Offline updater tests: exercise real backup/overlay/recovery with a simulated official setup."""
import copy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import subprocess
import unittest
from unittest.mock import patch
import zipfile
import strata_update as u


def archive(files):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w") as z:
        for name, data in files.items():
            info = zipfile.ZipInfo(name)
            info.filename = name  # preserve hostile backslashes even when tests run on Windows
            z.writestr(info, data)
    return stream.getvalue()


def release():
    tag = "v0.1.41"
    base = "https://github.com/" + u.REPO + "/releases/"
    names = ("strata-windows-x64.zip", "strata-windows-x64-cuda12.zip", "strata-windows-x64-hip.zip")
    return {"tag_name": tag, "html_url": base + "tag/" + tag, "assets": [
        {"name": name, "browser_download_url": base + "download/" + tag + "/" + name,
         "size": 6, "digest": "sha256:" + hashlib.sha256(b"engine").hexdigest()} for name in names]}


class UpdateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "Strata"
        self.root.mkdir()
        self.backups = Path(self.temp.name) / "backups"
        (self.root / "engine").mkdir()
        (self.root / "engine/BUILD.json").write_text('{"version":"0.1.40","source":"release"}')
        (self.root / "engine/strata.exe").write_bytes(b"previous engine")
        (self.root / ".venv/Scripts").mkdir(parents=True)
        (self.root / ".venv/Scripts/python.exe").touch()
        (self.root / "setup.py").write_text("old source")
        (self.root / "models").mkdir()
        (self.root / "models/user.gguf").write_bytes(b"precious model")
        self.config = {"exe": str(self.root / "engine/strata.exe"), "model_name": "swift-iq3_s", "port": 8080,
                       "args": ["--native", "D:/models/user.gguf", "--max-context", "262144"]}
        (self.root / "strata-swift.json").write_text(json.dumps(self.config))
        self.source = archive({"Strata-v0.1.41/setup.py": "new source", "Strata-v0.1.41/serve/new.py": "new file",
                               "Strata-v0.1.41/models/user.gguf": "wrong", "Strata-v0.1.41/strata-swift.json": "wrong"})

    def apply(self, fail=False, change_identity=False, corrupt=False):
        def urlopen(request, **kwargs):
            return io.BytesIO(self.source if "codeload" in request.full_url else b"broken" if corrupt else b"engine")
        def setup(args, root, capture=False):
            self.assertIn("--update", args)
            self.assertTrue((Path(args[-1]) / "strata-windows-x64.zip").is_file())
            (root / "engine/BUILD.json").write_text('{"version":"0.1.41"}')
            (root / "engine/strata.exe").write_bytes(b"new engine")
            if change_identity:
                cfg = copy.deepcopy(self.config)
                cfg["args"][-1] = "32768"
                (root / "strata-swift.json").write_text(json.dumps(cfg))
            if fail:
                raise RuntimeError("setup failed")
        with patch.object(u, "fetch_json", return_value=release()), patch.object(u, "assert_stopped"), \
             patch.object(u.urllib.request, "urlopen", side_effect=urlopen), patch.object(u, "run", side_effect=setup):
            u.apply(self.root, "v0.1.41", self.backups)

    def assert_restored(self):
        self.assertEqual(u.installed(self.root), "0.1.40")
        self.assertEqual((self.root / "setup.py").read_text(), "old source")
        self.assertFalse((self.root / "serve/new.py").exists())
        self.assertEqual(json.loads((self.root / "strata-swift.json").read_text()), self.config)
        self.assertEqual((self.root / "models/user.gguf").read_bytes(), b"precious model")

    def test_success_and_post_restart_rollback(self):
        self.apply()
        self.assertEqual(u.installed(self.root), "0.1.41")
        self.assertEqual((self.root / "setup.py").read_text(), "new source")
        self.assertEqual(json.loads((self.root / "strata-swift.json").read_text()), self.config)
        self.assertEqual((self.root / "models/user.gguf").read_bytes(), b"precious model")
        with patch.object(u, "assert_stopped"):
            u.rollback(self.root)
        self.assert_restored()

    def test_setup_failure_restores_source_engine_and_config(self):
        with self.assertRaisesRegex(RuntimeError, "setup failed"):
            self.apply(fail=True)
        self.assert_restored()

    def test_changed_model_identity_is_rolled_back(self):
        with self.assertRaisesRegex(RuntimeError, "changed the model"):
            self.apply(change_identity=True)
        self.assert_restored()

    def test_corrupt_download_is_refused_and_restored(self):
        with self.assertRaisesRegex(ValueError, "verification"):
            self.apply(corrupt=True)
        self.assert_restored()

    def test_running_engine_is_refused_before_backup_or_writes(self):
        with patch.object(u, "fetch_json", return_value=release()), patch.object(u, "assert_stopped", side_effect=RuntimeError("still running")):
            with self.assertRaisesRegex(RuntimeError, "still running"):
                u.apply(self.root, "v0.1.41", self.backups)
        self.assertFalse(self.backups.exists())
        self.assert_restored()

    def test_offline_check_does_not_touch_install(self):
        with patch.object(u, "fetch_json", side_effect=OSError("offline")):
            with self.assertRaisesRegex(OSError, "offline"):
                u.check(self.root)
        self.assert_restored()

    def test_release_origin_digest_and_stability(self):
        for field, value in (("prerelease", True), ("draft", True), ("html_url", "https://example.com/release"), ("tag_name", "latest")):
            rel = release()
            rel[field] = value
            with self.assertRaises(ValueError):
                u.validate_release(rel)
        for field, value in (("digest", ""), ("browser_download_url", "https://example.com/engine.zip")):
            rel = release()
            rel["assets"][0][field] = value
            with self.assertRaises(ValueError):
                u.validate_release(rel)

    def test_source_archive_cannot_escape_or_overwrite_models(self):
        for name in ("Strata/../escape.py", "/absolute.py", "Strata/C:/evil", "Strata/dir\\evil.py"):
            z = self.root / "test.zip"
            z.write_bytes(archive({"Strata/setup.py": "x", name: "bad"}))
            with self.assertRaises(ValueError):
                u.extract_source(z, self.root / "stage")
            self.assertFalse((self.root / "stage").exists())

    def test_numeric_version_order_and_no_downgrade(self):
        self.assertGreater(u.version("v0.1.41"), u.version("0.1.40.1"))
        self.assertGreater(u.version("0.1.100"), u.version("0.1.99"))
        (self.root / "engine/BUILD.json").write_text('{"version":"0.1.42"}')
        with patch.object(u, "fetch_json", return_value=release()):
            self.assertFalse(u.check(self.root)["available"])

    def git_fixture(self):
        def git(*args):
            return subprocess.check_output(["git", "-C", str(self.root), *args], stderr=subprocess.PIPE, text=True).strip()
        git("init", "-q")
        git("config", "user.email", "tests@example.invalid")
        git("config", "user.name", "Updater tests")
        git("add", "setup.py")
        git("commit", "-qm", "previous")
        before = git("rev-parse", "HEAD")
        (self.root / "setup.py").write_text("new source")
        git("commit", "-qam", "release")
        git("tag", "v0.1.41")
        git("reset", "--hard", before)
        return git, before

    def test_git_update_and_failed_setup_restores_commit(self):
        git, before = self.git_fixture()
        original_run = u.run
        def run(args, root, capture=False):
            if args[0] == "git":
                if args[1] == "fetch":
                    args = ["git", "fetch", "--no-tags", str(root), "refs/tags/v0.1.41"]
                return original_run(args, root, capture)
            (root / "engine/BUILD.json").write_text('{"version":"0.1.41"}')
            raise RuntimeError("git setup failure")
        with patch.object(u, "fetch_json", return_value=release()), patch.object(u, "assert_stopped"), \
             patch.object(u, "download_engines", return_value=self.backups), patch.object(u, "run", side_effect=run):
            with self.assertRaisesRegex(RuntimeError, "git setup failure"):
                u.apply(self.root, "v0.1.41", self.backups)
        self.assertEqual(git("rev-parse", "HEAD"), before)
        self.assert_restored()

    def test_git_local_edits_are_preserved_and_refused(self):
        self.git_fixture()
        (self.root / "setup.py").write_text("my source edit")
        with patch.object(u, "fetch_json", return_value=release()), patch.object(u, "assert_stopped"):
            with self.assertRaisesRegex(ValueError, "local source edits"):
                u.apply(self.root, "v0.1.41", self.backups)
        self.assertEqual((self.root / "setup.py").read_text(), "my source edit")
        self.assertFalse(self.backups.exists())


if __name__ == "__main__":
    unittest.main()
