"""StrataHome's stable-release updater. Uses Strata's own setup --update, never model setup."""
import argparse
import contextlib
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import urllib.request
import zipfile

REPO = "Niko1221/Strata"
API = "https://api.github.com/repos/" + REPO
TAG = re.compile(r"v?(\d+(?:\.\d+){2,3})\Z")


def version(value):
    match = TAG.fullmatch(str(value))
    if not match:
        raise ValueError("Unrecognized Strata version: " + str(value))
    parts = tuple(map(int, match[1].split(".")))
    return parts + (0,) * (4 - len(parts))


def read_json(path, default=None):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return default


def installed(root):
    values = []
    for name in ("engine", "engine-cuda12"):
        build = read_json(root / name / "BUILD.json", {})
        try:
            values.append((version(build.get("version")), build["version"]))
        except ValueError:
            pass
    # Every installed toolkit must be current; setup --update services every model config.
    return min(values)[1] if values else "0.0.0"


def fetch_json(url):
    request = urllib.request.Request(url, headers={"User-Agent": "StrataHome/0.3", "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def validate_release(release):
    tag = release.get("tag_name", "")
    version(tag)
    if release.get("draft") or release.get("prerelease"):
        raise ValueError("Only stable Strata releases are installed")
    if release.get("html_url") != "https://github.com/" + REPO + "/releases/tag/" + tag:
        raise ValueError("The release is not from the official Strata repository")
    assets = {a["name"]: a for a in release.get("assets", [])}
    for name in ("strata-windows-x64.zip", "strata-windows-x64-cuda12.zip", "strata-windows-x64-hip.zip"):
        asset = assets.get(name, {})
        if not re.fullmatch(r"sha256:[a-fA-F0-9]{64}", str(asset.get("digest", ""))):
            raise ValueError("The official release is missing a checksum for " + name)
        expected = "https://github.com/" + REPO + "/releases/download/" + tag + "/" + name
        if asset.get("browser_download_url") != expected:
            raise ValueError("Unexpected engine download address")
    return release


def check(root):
    release = validate_release(fetch_json(API + "/releases/latest"))
    current = installed(root)
    return {"installed": current, "latest": release["tag_name"],
            "available": version(current) < version(release["tag_name"]), "url": release["html_url"]}


def run(args, root, capture=False):
    env = dict(os.environ, GIT_TERMINAL_PROMPT="0", PYTHONUNBUFFERED="1", PYTHONIOENCODING="utf-8")
    env.pop("STRATA_SKIP_SHA256", None)
    env.pop("STRATA_PREBUILT_URL", None)
    result = subprocess.run(args, cwd=root, env=env, check=True, stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE if capture else None,
                            stderr=subprocess.PIPE if capture else None, encoding="utf-8", errors="replace")
    return result.stdout.strip() if capture else ""


def protected(relative):
    parts = PurePosixPath(relative).parts
    if not parts:
        return True
    first = parts[0].lower()
    return (first in {".git", ".venv", "engine", "engine-cuda12", "models", "packs", "mtp", "data"}
            or first.startswith(".stratahome")
            or (len(parts) == 1 and (first.startswith("strata-") and first.endswith((".json", ".log", ".dmp"))
                                     or first.startswith("run-") and first.endswith((".bat", ".sh")))))


def extract_source(archive, target):
    """Validate the whole archive before writing anything, including Windows path and symlink checks."""
    with zipfile.ZipFile(archive) as source:
        members = source.infolist()
        if len(members) > 50000 or sum(m.file_size for m in members) > 2 * 1024**3:
            raise ValueError("Source archive is unexpectedly large")
        prefixes, validated = set(), []
        for member in members:
            name = member.orig_filename
            path = PurePosixPath(name)
            if ("\\" in name or ":" in name or path.is_absolute() or ".." in path.parts
                    or stat.S_ISLNK(member.external_attr >> 16)):
                raise ValueError("Unsafe source archive path")
            if not path.parts:
                continue
            prefixes.add(path.parts[0])
            if len(path.parts) > 1 and not member.is_dir():
                relative = PurePosixPath(*path.parts[1:]).as_posix()
                if not protected(relative):
                    validated.append((member, relative))
        if len(prefixes) != 1 or not any(rel == "setup.py" for _, rel in validated):
            raise ValueError("Not a Strata source archive")
        for member, relative in validated:
            destination = target / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            with source.open(member) as inp, destination.open("wb") as out:
                shutil.copyfileobj(inp, out)


def config_paths(root):
    return [p for p in root.glob("strata-*.json") if isinstance(read_json(p), dict)]


def identity(cfg):
    args = cfg.get("args", [])
    result = {key: cfg.get(key) for key in ("model_name", "tokenizer", "port", "host", "api_key")}
    for flag in ("--native", "--pack", "--ple-gguf", "--max-context"):
        result[flag] = args[args.index(flag)+1] if flag in args else None
    return result


@contextlib.contextmanager
def update_lock(root):
    path = root / ".stratahome-update.lock"
    with path.open("a+b") as lock:
        lock.seek(0)
        if not lock.read(1):
            lock.write(b"0")
            lock.flush()
        lock.seek(0)
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            yield
        finally:
            lock.seek(0)
            if os.name == "nt":
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)


def apply(root, tag, backup_base):
    version(tag)
    release = validate_release(fetch_json(API + "/releases/tags/" + tag))
    if version(installed(root)) >= version(tag):
        print("Strata is already up to date", flush=True)
        return
    if not (root / "setup.py").is_file() or not (root / ".venv/Scripts/python.exe").is_file():
        raise ValueError("Choose an installed Strata folder containing setup.py and .venv")
    with update_lock(root):
        assert_stopped(root)
        if (root / ".git").exists() and run(["git", "status", "--porcelain", "--untracked-files=no"], root, True):
            raise ValueError("Strata has local source edits. Commit or stash them before updating; nothing was changed")
        backup_base.mkdir(parents=True, exist_ok=True)
        backup = Path(tempfile.mkdtemp(prefix="before-" + tag + "-", dir=backup_base))
        print("Recovery copy: " + str(backup), flush=True)
        old_head = None
        changed_source = []
        git = (root / ".git").exists()
        configs = {p.name: p.read_bytes() for p in config_paths(root)}
        engines = [name for name in ("engine", "engine-cuda12") if (root / name).is_dir()]
        # All backups finish before source or engine files are changed.
        for name in engines:
            shutil.copytree(root / name, backup / name, ignore=shutil.ignore_patterns(".previous", "_unpack"))
        for name, data in configs.items():
            (backup / name).write_bytes(data)
        manifest = {"release": tag, "root": str(root), "engines": engines, "configs": list(configs)}
        marker = root / ".stratahome-release.json"
        if marker.exists():
            shutil.copy2(marker, backup / "previous-marker.json")
        if git:
            if run(["git", "status", "--porcelain", "--untracked-files=no"], root, True):
                raise ValueError("Strata has local source edits. Commit or stash them before updating; nothing was changed")
            old_head = run(["git", "rev-parse", "HEAD"], root, True)
            manifest["previous_commit"] = old_head
        (backup / "recovery.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        try:
            if git:
                print("Fetching the official Strata " + tag + " source", flush=True)
                run(["git", "fetch", "--no-tags", "https://github.com/" + REPO + ".git", "refs/tags/" + tag], root)
                target = run(["git", "rev-parse", "FETCH_HEAD^{commit}"], root, True)
                if old_head != target:
                    run(["git", "merge-base", "--is-ancestor", old_head, target], root)
                    run(["git", "merge", "--ff-only", target], root)
            else:
                print("Downloading the official Strata " + tag + " source", flush=True)
                archive = backup / "source.zip"
                url = "https://codeload.github.com/" + REPO + "/zip/refs/tags/" + tag
                request = urllib.request.Request(url, headers={"User-Agent": "StrataHome/0.3"})
                with urllib.request.urlopen(request, timeout=60) as inp, archive.open("wb") as out:
                    shutil.copyfileobj(inp, out)
                stage = backup / "source"
                extract_source(archive, stage)
                for source in sorted(stage.rglob("*")):
                    if not source.is_file():
                        continue
                    relative = source.relative_to(stage).as_posix()
                    destination = root / relative
                    # Do not follow user-created links outside the selected install.
                    if not destination.resolve().is_relative_to(root.resolve()):
                        raise ValueError("A Strata source path points outside its folder")
                    existed = destination.is_file()
                    if existed:
                        previous = backup / "previous-source" / relative
                        previous.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(destination, previous)
                    changed_source.append((relative, existed))
                    manifest["changed_source"] = changed_source
                    (backup / "recovery.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(source, destination)
            print("Running Strata's official setup --update (models are not downloaded)", flush=True)
            downloads = download_engines(root, backup, release, engines)
            run([str(root / ".venv/Scripts/python.exe"), "-u", str(root / "setup.py"), "--update", "--yes",
                 "--prebuilt", str(downloads)], root)
            if version(installed(root)) < version(tag):
                raise RuntimeError("Setup finished without installing the requested engine version")
            for name, data in configs.items():
                before = json.loads(data.decode("utf-8-sig"))
                if "exe" in before and identity(before) != identity(read_json(root / name, {})):
                    raise RuntimeError("The upstream update changed the model, context or connection settings")
            marker.write_text(json.dumps({"release": tag, "backup": str(backup), "url": release["html_url"]}, indent=2), encoding="utf-8")
            print("Installed Strata " + installed(root), flush=True)
        except BaseException:
            print("Update failed; restoring the previous engine and settings", flush=True)
            try:
                restore(root, backup)
            except Exception as error:
                print("Recovery needs attention: " + str(error) + ". Backup: " + str(backup), flush=True)
            raise


def assert_stopped(root):
    import psutil
    for process in psutil.process_iter(["pid", "exe", "cmdline"]):
        if process.pid == os.getpid():
            continue
        try:
            exe = Path(process.info["exe"] or "").resolve()
            command = process.info["cmdline"] or []
            if (exe.parent in {(root / "engine").resolve(), (root / "engine-cuda12").resolve()}
                    or any(Path(arg).resolve() == (root / "serve/server.py").resolve() for arg in command[1:] if "server.py" in arg)):
                raise RuntimeError("A Strata server or engine is still running. No files were changed")
        except (psutil.NoSuchProcess, psutil.AccessDenied, OSError):
            continue


def download_engines(root, backup, release, engines):
    """Pin and verify bytes ourselves: upstream allows installing when its checksum API is offline."""
    downloads = backup / "verified-downloads"
    downloads.mkdir()
    assets = {a["name"]: a for a in release["assets"]}
    for engine in engines:
        build = read_json(root / engine / "BUILD.json", {})
        if build.get("source") == "local":
            raise ValueError("This engine was compiled locally. Update it with Strata's build tools")
        name = "strata-windows-x64-cuda12.zip" if engine == "engine-cuda12" else (
            "strata-windows-x64-hip.zip" if build.get("backend") == "hip" else "strata-windows-x64.zip")
        asset = assets[name]
        print("Downloading and verifying " + name, flush=True)
        request = urllib.request.Request(asset["browser_download_url"], headers={"User-Agent": "StrataHome/0.3"})
        digest, count = hashlib.sha256(), 0
        with urllib.request.urlopen(request, timeout=60) as inp, (downloads / name).open("wb") as out:
            while block := inp.read(1024 * 1024):
                count += len(block)
                if count > asset["size"]:
                    raise ValueError("Engine download is larger than the published size")
                digest.update(block)
                out.write(block)
        if count != asset["size"] or digest.hexdigest() != asset["digest"].split(":")[1].lower():
            raise ValueError("Engine download failed SHA-256 or size verification")
    return downloads


def restore(root, backup):
    manifest = read_json(backup / "recovery.json", {})
    if Path(manifest.get("root", "")).resolve() != root.resolve():
        raise ValueError("The recovery copy belongs to a different Strata install")
    errors = []
    for name in manifest["engines"]:
        try:
            if name not in ("engine", "engine-cuda12"):
                raise ValueError("Invalid engine recovery path")
            current = root / name
            if current.is_symlink() or current.resolve().parent != root.resolve():
                raise ValueError("Engine path is outside the Strata folder")
            if current.exists():
                shutil.rmtree(current)
            shutil.copytree(backup / name, current)
        except Exception as error:
            errors.append(str(error))
    for name in manifest["configs"]:
        if Path(name).name != name or not name.startswith("strata-") or not name.endswith(".json"):
            raise ValueError("Invalid config recovery path")
        shutil.copy2(backup / name, root / name)
    if manifest.get("previous_commit"):
        try:
            run(["git", "reset", "--keep", manifest["previous_commit"]], root)
        except Exception as error:
            errors.append(str(error))
    for relative, existed in reversed(manifest.get("changed_source", [])):
        destination = root / relative
        if not destination.resolve().is_relative_to(root.resolve()) or protected(relative):
            raise ValueError("Invalid source recovery path")
        if existed:
            shutil.copy2(backup / "previous-source" / relative, destination)
        else:
            destination.unlink(missing_ok=True)
    marker = root / ".stratahome-release.json"
    if (backup / "previous-marker.json").exists():
        shutil.copy2(backup / "previous-marker.json", marker)
    else:
        marker.unlink(missing_ok=True)
    if errors:
        raise RuntimeError("; ".join(errors))
    print("Previous source, engine and settings restored", flush=True)


def rollback(root):
    marker = read_json(root / ".stratahome-release.json", {})
    if not marker.get("backup"):
        raise ValueError("No completed update recovery copy found")
    with update_lock(root):
        assert_stopped(root)
        restore(root, Path(marker["backup"]))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dir", required=True, type=Path)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--apply", metavar="RELEASE")
    parser.add_argument("--rollback", action="store_true")
    parser.add_argument("--backup-dir", type=Path)
    args = parser.parse_args()
    root = args.dir.resolve()
    if args.check:
        print(json.dumps(check(root)), flush=True)
    elif args.apply and args.backup_dir:
        apply(root, args.apply, args.backup_dir.resolve())
    elif args.rollback:
        rollback(root)
    else:
        parser.error("Use --check, or --apply RELEASE --backup-dir PATH")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print("Strata update: " + str(error), file=sys.stderr, flush=True)
        sys.exit(1)
