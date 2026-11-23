"""Read-only controller export and checksum-verified workstation backup catch-up."""

import argparse
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

from instance import child_env, write_private
from offhost import (
    FORMAT,
    POINT,
    HEX,
    RecoveryError,
    failure_reason,
    managed_dir,
    outside_checkout,
    point_time,
    read_receipt,
    validate_point,
    verify_file,
)
from runtime import exclusive


def export_point(source, receipt, target):
    """Publish an immutable, read-only export for the dedicated mirror account on the controller."""
    import grp

    group = grp.getgrnam(target["group"]).gr_gid
    root = Path(target["directory"])
    if (
        root.is_symlink()
        or not root.is_dir()
        or root.stat().st_uid != 0
        or root.stat().st_mode & 0o022
    ):
        raise RecoveryError(
            "Use a root-owned mirror export directory with no group or public writes."
        )
    point = validate_point(receipt["point"])
    final = root / point
    if final.exists():
        current = read_receipt(final / (point + ".json"), point)
        if (
            current["sha256"] != receipt["sha256"]
            or current["bytes"] != receipt["bytes"]
        ):
            raise RecoveryError(
                "Existing encrypted mirror export conflicts with the verified point."
            )
        return
    with tempfile.TemporaryDirectory(dir=root) as temporary:
        staged = Path(temporary) / "point"
        staged.mkdir()
        encrypted = staged / (point + ".age")
        # Both directories are on the controller's backup filesystem. One inode holds the ciphertext.
        os.link(source, encrypted)
        receipt_file = staged / (point + ".json")
        write_private(receipt_file, json.dumps(receipt, sort_keys=True))
        for file in (receipt_file, encrypted):
            os.chown(file, 0, group)
            file.chmod(0o640)
        os.chown(staged, 0, group)
        staged.chmod(0o750)
        staged.rename(final)


def serve(directory):
    """The dedicated SSH key can list verified receipts and read a named encrypted point."""
    request = os.environ.get("SSH_ORIGINAL_COMMAND", "")
    if request == "list":
        for child in sorted(directory.iterdir()):
            if not POINT.fullmatch(child.name) or not child.is_dir():
                continue
            receipt = read_receipt(child / (child.name + ".json"), child.name)
            print(json.dumps(receipt))
    elif request.startswith("get "):
        point = validate_point(request.removeprefix("get "))
        with (directory / point / (point + ".age")).open("rb") as file:
            shutil.copyfileobj(file, sys.stdout.buffer)
    else:
        raise RecoveryError("Unknown mirror request.")


def catch_up(config):
    state = managed_dir(config["state"], "nightly-mirror-state")
    ssh = [
        "ssh",
        "-F",
        config["sshConfig"],
        "-T",
        "-o",
        "BatchMode=yes",
        "-o",
        "ClearAllForwardings=yes",
        "brickvault-v2-backup-mirror",
    ]
    with exclusive(state / "sync.lock"):
        status_file = state / "status.json"
        status = json.loads(status_file.read_text()) if status_file.exists() else {}
        status.update(
            attemptedAt=datetime.now(timezone.utc).isoformat(),
            state="running",
            lastError=None,
        )
        write_private(status_file, json.dumps(status))
        try:
            target = managed_dir(config["mirror"], "mirror")
            response = subprocess.run(
                [*ssh, "list"],
                env=child_env(),
                stdin=subprocess.DEVNULL,
                capture_output=True,
                check=True,
                timeout=60,
            )
            copied = 0
            latest = None
            cache = state / "verified-files.json"
            checked = json.loads(cache.read_text()) if cache.exists() else {}
            for line in response.stdout.splitlines():
                receipt = json.loads(line)
                point = validate_point(receipt["point"])
                if (
                    receipt.get("format") != FORMAT
                    or not HEX.fullmatch(str(receipt.get("sha256")))
                    or type(receipt.get("bytes")) is not int
                    or receipt["bytes"] <= 0
                ):
                    raise RecoveryError("Mirror response contained an invalid receipt.")
                file = target / (point + ".age")
                meta = target / (point + ".json")
                if not file.exists():
                    with tempfile.TemporaryDirectory(dir=target) as temporary:
                        scratch = Path(temporary) / "received.age"
                        with scratch.open("xb") as output:
                            subprocess.run(
                                [*ssh, "get " + point],
                                env=child_env(),
                                stdin=subprocess.DEVNULL,
                                stdout=output,
                                stderr=subprocess.DEVNULL,
                                check=True,
                                timeout=3600,
                            )
                        verify_file(scratch, receipt)
                        os.link(scratch, file)
                    copied += 1
                stamp = file.stat()
                mark = [receipt["sha256"], stamp.st_size, stamp.st_mtime_ns]
                if checked.get(point) != mark:
                    verify_file(file, receipt)
                    checked[point] = mark
                if meta.exists():
                    saved = read_receipt(meta, point)
                    if (
                        saved["sha256"] != receipt["sha256"]
                        or saved["bytes"] != receipt["bytes"]
                    ):
                        raise RecoveryError(
                            "Local receipt conflicts with the controller."
                        )
                else:
                    write_private(meta, json.dumps(receipt, sort_keys=True))
                latest = point
            if latest is None:
                raise RecoveryError("No verified controller points are available yet.")
            write_private(cache, json.dumps(checked, sort_keys=True))
            status.update(
                state="verified-local",
                point=latest,
                verifiedAt=datetime.now(timezone.utc).isoformat(),
                copied=copied,
                cloudSyncVerified=False,
            )
            write_private(status_file, json.dumps(status))
        except Exception as exc:
            status.update(state="failed", lastError=failure_reason(exc))
            write_private(status_file, json.dumps(status))
            raise
    print("Verified local mirror through " + latest)


def health(config):
    state = outside_checkout(config["state"])
    if (state / ".brickvault-v2-offhost").read_text() != "nightly-mirror-state":
        raise RecoveryError("Expected a managed V2 mirror state directory.")
    status = json.loads((state / "status.json").read_text())
    point = status.get("point")
    fresh = False
    if point:
        age = datetime.now(timezone.utc) - point_time(point)
        fresh = timedelta(0) <= age <= timedelta(hours=config.get("freshnessHours", 30))
    print(json.dumps(status | {"fresh": fresh}))
    if not fresh or status.get("state") == "failed":
        raise RecoveryError("A fresh verified workstation mirror is required.")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    actions = p.add_subparsers(dest="action", required=True)
    actions.add_parser("serve").add_argument("--directory", type=Path, required=True)
    for name in ("sync", "status"):
        actions.add_parser(name).add_argument("--config", type=Path, required=True)
    args = p.parse_args()
    try:
        if args.action == "serve":
            serve(args.directory)
        else:
            config = json.loads(args.config.read_text(encoding="utf-8"))
            if args.action == "status":
                health(config)
            else:
                catch_up(config)
    except (
        RecoveryError,
        OSError,
        ValueError,
        KeyError,
        subprocess.SubprocessError,
    ) as ex:
        p.exit(1, f"Mirror {args.action} failed: {failure_reason(ex)}\n")


if __name__ == "__main__":
    main()
