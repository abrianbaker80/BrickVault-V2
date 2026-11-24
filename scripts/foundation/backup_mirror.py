"""Read-only controller export and checksum-verified workstation backup catch-up."""

import argparse
import base64
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import uuid

from cloud_receipt import FIELDS, SIDECAR, load_small, publish_local, verify_sidecar
from instance import child_env, write_private
from offhost import (
    POINT,
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

# Wait for the desktop sync client to publish the already captured files.
READBACK_DELAYS = (60, 180, 420, 900, 1800, 3600)
READBACK_WINDOW = 2 * 60 * 60


@contextmanager
def on_destination_volume(staged, parent, verify_copy):
    if staged.stat().st_dev == parent.stat().st_dev:
        yield staged
        return
    with tempfile.TemporaryDirectory(dir=parent) as temp:
        copied = Path(temp) / staged.name
        shutil.copyfile(staged, copied)
        verify_copy(copied)
        yield copied


def install_new(staged, destination):
    if os.name == "nt":
        staged.rename(
            destination
        )  # Windows rename fails atomically if a file already exists.
    else:
        os.link(staged, destination)


def verify_or_repair(file, receipt, produce, quarantine, stage_dir=None):
    """Verify every existing archive. Preserve damaged bytes before atomic replacement."""
    if file.is_symlink():
        raise RecoveryError("An archive path is a symbolic link.")
    if file.exists():
        try:
            verify_file(file, receipt)
            return False
        except RecoveryError:
            pass
    with tempfile.TemporaryDirectory(dir=stage_dir or file.parent) as temporary:
        candidate = Path(temporary) / "received.age"
        produce(candidate)
        verify_file(candidate, receipt)
        with on_destination_volume(
            candidate, file.parent, lambda copy: verify_file(copy, receipt)
        ) as local:
            if file.exists():
                quarantine.mkdir(mode=0o700, parents=True, exist_ok=True)
                preserved = quarantine / (
                    file.name + "." + uuid.uuid4().hex + ".damaged"
                )
                with file.open("rb") as source, preserved.open("xb") as target:
                    shutil.copyfileobj(source, target)
                    target.flush()
                    os.fsync(target.fileno())
                os.replace(local, file)
            else:
                install_new(local, file)
    return True


def matching_receipt(file, receipt):
    if file.is_symlink():
        raise RecoveryError("A receipt path is a symbolic link.")
    current = read_receipt(file, receipt["point"])
    if any(current.get(field) != receipt.get(field) for field in FIELDS):
        raise RecoveryError("Existing receipt conflicts with the verified point.")


def copy_sidecar(source, point, directory, group):
    # The signed cloud receipt was independently verified when the controller finalized the point.
    original = source.parent / (point + SIDECAR)
    with tempfile.TemporaryDirectory(dir=directory) as temp:
        staged = Path(temp) / "receipt"
        with original.open("rb") as inp:
            data = inp.read(16385)
        if len(data) > 16384:
            raise RecoveryError("Signed receipt exceeds its format limit.")
        staged.write_bytes(data)
        os.chown(staged, 0, group)
        staged.chmod(0o640)
        publish_local(staged, directory / original.name)


def complete_previous_export(source, receipt, target):
    """Complete only a previously authorized export with matching ownership and archive."""
    import grp

    group = grp.getgrnam(target["group"]).gr_gid
    root = Path(target["directory"])
    point = validate_point(receipt["point"])
    previous = root / point
    if not previous.is_dir():
        return
    for path in (
        root,
        previous,
        previous / (point + ".json"),
        previous / (point + ".age"),
    ):
        attrs = path.stat()
        if (
            path.is_symlink()
            or attrs.st_uid != 0
            or attrs.st_gid != group
            or attrs.st_mode & 0o027
        ):
            raise RecoveryError(
                "Historical export ownership differs from its authorized reader."
            )
    matching_receipt(previous / (point + ".json"), receipt)
    if (previous / (point + SIDECAR)).exists():
        # Confirm the signed bytes without fetching or rewriting the historical ciphertext.
        copy_sidecar(source, point, previous, group)
        return
    verify_file(previous / (point + ".age"), receipt)
    copy_sidecar(source, point, previous, group)


def export_point(source, receipt, target):
    """Offer an independent verified ciphertext copy to the read-only mirror account."""
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

    def copy_encrypted(destination):
        shutil.copyfile(source, destination)
        os.chown(destination, 0, group)
        destination.chmod(0o640)

    point = validate_point(receipt["point"])
    final = root / point

    if final.exists():
        matching_receipt(final / (point + ".json"), receipt)
        copy_sidecar(source, point, final, group)
        verify_or_repair(
            final / (point + ".age"),
            receipt,
            copy_encrypted,
            source.parent / "quarantine",
        )
        return
    with tempfile.TemporaryDirectory(dir=root) as temporary:
        staged = Path(temporary) / "point"
        staged.mkdir()
        encrypted = staged / (point + ".age")
        copy_encrypted(encrypted)
        verify_file(encrypted, receipt)
        receipt_file = staged / (point + ".json")
        public_receipt = {k: v for k, v in receipt.items() if k != "mirrorBinding"}
        write_private(receipt_file, json.dumps(public_receipt, sort_keys=True))
        os.chown(receipt_file, 0, group)
        receipt_file.chmod(0o640)
        os.chown(staged, 0, group)
        staged.chmod(0o750)
        copy_sidecar(source, point, staged, group)
        staged.rename(final)


def serve(directory):
    """The dedicated SSH key can list verified receipts and read a named encrypted point."""
    request = os.environ.get("SSH_ORIGINAL_COMMAND", "")
    if request == "list":
        for child in sorted(directory.iterdir()):
            if not POINT.fullmatch(child.name) or not child.is_dir():
                continue
            receipt = read_receipt(child / (child.name + ".json"), child.name)
            signed = child / (child.name + SIDECAR)
            if signed.is_file():
                with signed.open("rb") as handle:
                    data = handle.read(16385)
                if len(data) <= 16384:
                    receipt["signedReceipt"] = base64.b64encode(data).decode("ascii")
            print(json.dumps(receipt))
    elif request.startswith("get "):
        point = validate_point(request.removeprefix("get "))
        with (directory / point / (point + ".age")).open("rb") as file:
            shutil.copyfileobj(file, sys.stdout.buffer)
    else:
        raise RecoveryError("Unknown mirror request.")


def check_sidecar(path, expected):
    if path.is_symlink():
        raise RecoveryError("The signed receipt is a symbolic link.")
    with path.open("rb") as file:
        if file.read(len(expected) + 1) != expected:
            raise RecoveryError("A conflicting signed receipt already exists.")


def authenticate_receipt(staged, receipt, trust):
    encoded = receipt.get("signedReceipt")
    if not isinstance(encoded, str) or len(encoded) > 22000:
        raise RecoveryError(
            "This controller point requires its signed recovery receipt."
        )
    staged.write_bytes(base64.b64decode(encoded, validate=True))
    verified = verify_sidecar(staged, validate_point(receipt["point"]), trust)
    if any(verified[field] != receipt.get(field) for field in FIELDS):
        raise RecoveryError(
            "Signed recovery receipt differs from the controller's point."
        )
    return verified


def sync_point(ssh, target, state, receipt, trust):
    point = validate_point(receipt["point"])
    with tempfile.TemporaryDirectory(dir=state) as temp:
        scratch = Path(temp)
        staged = scratch / (point + SIDECAR)
        trusted = authenticate_receipt(staged, receipt, trust)
        signed = target / staged.name
        sidecar_bytes = staged.read_bytes()
        if signed.exists() or signed.is_symlink():
            check_sidecar(signed, sidecar_bytes)
        metadata = target / (point + ".json")
        if metadata.exists() or metadata.is_symlink():
            matching_receipt(metadata, trusted)

        def download(destination):
            with destination.open("xb") as output:
                subprocess.run(
                    [*ssh, "get " + point],
                    env=child_env(),
                    stdin=subprocess.DEVNULL,
                    stdout=output,
                    stderr=subprocess.DEVNULL,
                    check=True,
                    timeout=3600,
                )

        copied = verify_or_repair(
            target / (point + ".age"), trusted, download, state / "quarantine", scratch
        )
        if not metadata.exists():
            write_private(metadata, json.dumps(trusted, sort_keys=True))
        if not signed.exists():
            with on_destination_volume(
                staged, target, lambda file: check_sidecar(file, sidecar_bytes)
            ) as local:
                try:
                    install_new(local, signed)
                except FileExistsError:
                    check_sidecar(signed, sidecar_bytes)
    return copied


def record_readback(profile, trust, state, receipt, enrollment):
    import onedrive_reader

    point = receipt["point"]
    with tempfile.TemporaryDirectory(dir=state) as temp:
        directory = Path(temp) / "download"
        onedrive_reader.download(profile, trust, point, directory)
        trusted = read_receipt(directory / (point + ".json"), point)
        if any(trusted.get(field) != receipt.get(field) for field in FIELDS):
            raise RecoveryError(
                "OneDrive's signed point differs from the selected controller point."
            )
    now = datetime.now(timezone.utc).isoformat()
    write_private(
        state / "onedrive-readbacks" / (point + ".json"),
        json.dumps(
            {
                "point": point,
                "sha256": receipt["sha256"],
                "verifiedAt": now,
                "enrollment": enrollment,
            }
        ),
    )
    return now


def verify_onedrive(config, state, receipts, status, *, allow_retry=True):
    import onedrive_reader

    profile = onedrive_reader.profile_path(config["oneDriveProfile"])
    authfile = profile / "authorization.json"
    auth = json.loads(authfile.read_text()) if authfile.is_file() else {}
    if auth.get("state") != "ready" or not auth.get("verifiedAt"):
        raise RecoveryError(
            "Complete the Microsoft account sign-in for the OneDrive read-only backup reader."
        )
    enrollment = [str(profile), auth["verifiedAt"]]
    proofs = state / "onedrive-readbacks"
    proofs.mkdir(mode=0o700, exist_ok=True)
    latest = receipts[-1]["point"]
    pending = []
    for receipt in receipts:
        point = receipt["point"]
        proof_file = proofs / (point + ".json")
        evidence = json.loads(proof_file.read_text()) if proof_file.exists() else {}
        if (
            point != latest
            and evidence.get("sha256") == receipt["sha256"]
            and evidence.get("enrollment") == enrollment
        ):
            continue
        pending.append(receipt)
    delays = iter(READBACK_DELAYS if allow_retry else ())
    deadline = time.monotonic() + READBACK_WINDOW
    status_file = state / "status.json"
    while pending:
        status.update(
            state="checking-onedrive",
            nextReadbackAt=None,
            pendingReadbacks=len(pending),
        )
        write_private(status_file, json.dumps(status))
        delayed = []
        failures = []
        for receipt in pending:
            try:
                verified = record_readback(
                    profile, config["receiptTrust"], state, receipt, enrollment
                )
                point = receipt["point"]
                if point == latest or point >= (status.get("cloudPoint") or ""):
                    status.update(cloudPoint=point, cloudVerifiedAt=verified)
                if point == latest:
                    status["cloudSyncVerified"] = True
            except onedrive_reader.ReadbackPending as exc:
                delayed.append(receipt)
                delay_reason = failure_reason(exc)
            except (
                OSError,
                ValueError,
                KeyError,
                RecoveryError,
                subprocess.SubprocessError,
            ) as exc:
                failures.append(failure_reason(exc))
        status["pendingReadbacks"] = len(delayed)
        if failures:
            raise RecoveryError(
                f"{len(failures)} OneDrive readback(s) require attention. {failures[0]}"
            )
        if not delayed:
            return
        delay = next(delays, None)
        if delay is not None and time.monotonic() + delay < deadline:
            status.update(
                state="waiting-onedrive",
                nextReadbackAt=(
                    datetime.now(timezone.utc) + timedelta(seconds=delay)
                ).isoformat(),
            )
            write_private(status_file, json.dumps(status))
            time.sleep(delay)
            if time.monotonic() < deadline:
                pending = delayed
                continue
        status["nextReadbackAt"] = None
        raise RecoveryError(
            f"{len(delayed)} OneDrive readback(s) still unavailable. {delay_reason}"
        )


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
            cloudSyncVerified=False,
            nextReadbackAt=None,
            pendingReadbacks=0,
        )
        write_private(status_file, json.dumps(status))
        try:
            target = managed_dir(config["mirror"], "mirror")
            trust = load_small(outside_checkout(config["receiptTrust"]))
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
            receipts = []
            failed = []
            for line in response.stdout.splitlines():
                try:
                    receipt = json.loads(line)
                    copied += int(sync_point(ssh, target, state, receipt, trust))
                    latest = receipt["point"]
                    receipts.append(receipt)
                except (
                    OSError,
                    RecoveryError,
                    ValueError,
                    KeyError,
                    subprocess.SubprocessError,
                ) as exc:
                    failed.append(failure_reason(exc))
            if latest is None and not failed:
                raise RecoveryError("No verified controller points are available yet.")
            if latest:
                status.update(
                    point=latest,
                    verifiedAt=datetime.now(timezone.utc).isoformat(),
                    copied=copied,
                    cloudSyncVerified=False,
                )
            if receipts and config.get("oneDriveProfile"):
                try:
                    verify_onedrive(
                        config, state, receipts, status, allow_retry=not failed
                    )
                except (
                    OSError,
                    ValueError,
                    KeyError,
                    RecoveryError,
                    subprocess.SubprocessError,
                ) as exc:
                    failed.append(failure_reason(exc))
            if failed:
                raise RecoveryError(
                    f"{len(failed)} mirror point(s) require attention. {failed[0]}"
                )
            status["state"] = (
                "verified-onedrive"
                if status.get("cloudSyncVerified")
                else "verified-local"
            )
            write_private(status_file, json.dumps(status))
        except Exception as exc:
            status.update(state="failed", lastError=failure_reason(exc))
            write_private(status_file, json.dumps(status))
            raise
    print(
        "Verified "
        + (
            "OneDrive cloud backup"
            if status.get("cloudSyncVerified")
            else "local mirror"
        )
        + " through "
        + latest
    )


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
    cloud_fresh = (
        fresh
        and status.get("state") == "verified-onedrive"
        and status.get("point") == status.get("cloudPoint")
        and status.get("cloudSyncVerified") is True
    )
    print(json.dumps(status | {"fresh": fresh, "cloudFresh": cloud_fresh}))
    if (
        not fresh
        or status.get("state") == "failed"
        or (config.get("oneDriveProfile") and not cloud_fresh)
    ):
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
