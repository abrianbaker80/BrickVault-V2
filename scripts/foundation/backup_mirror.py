"""Read-only controller export and checksum-verified workstation backup catch-up."""

import argparse
import base64
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from time import monotonic, sleep
import uuid

from cloud_receipt import (
    FIELDS,
    SIDECAR,
    encoded,
    load_small,
    publish_local,
    validated_pin,
    verify_sidecar,
)
from instance import NO_WINDOW, child_env, write_private
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
                    creationflags=NO_WINDOW,
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


def record_readback(profile, trust, state, receipt, scope, deadline):
    import onedrive_reader

    point = receipt["point"]
    with tempfile.TemporaryDirectory(dir=state) as temp:
        directory = Path(temp) / "download"
        onedrive_reader.download(
            profile,
            trust,
            point,
            directory,
            expected_receipt=receipt,
            deadline=deadline,
        )
    now = datetime.now(timezone.utc).isoformat()
    write_private(
        state / "onedrive-readbacks" / (point + ".json"),
        json.dumps(
            {
                "point": point,
                "sha256": receipt["sha256"],
                "verifiedAt": now,
                **scope,
            }
        ),
    )
    return now


def scope_id(scope):
    return hashlib.sha256(encoded(scope)).hexdigest()


def migrate_legacy_evidence(mirror, trust, proofs, scope, status):
    """Bind the pre-scope local observation and any provider proof to authenticated lineage."""
    local = status.get("point")
    cloud = status.get("cloudPoint")
    try:
        signed = {}
        for point in {local, cloud} - {None}:
            validate_point(point)
            sidecar = mirror / (point + SIDECAR)
            archive = mirror / (point + ".age")
            if sidecar.is_symlink() or archive.is_symlink():
                raise RecoveryError("A legacy recovery point uses a symbolic link.")
            signed[point] = verify_sidecar(sidecar, point, trust)
            verify_file(archive, signed[point])

        mark = {"controllerPoint": max(signed)}
        prior = scope
        if cloud:
            proof_file = proofs / (cloud + ".json")
            if proof_file.is_symlink():
                raise RecoveryError("The historic provider proof uses a symbolic link.")
            proof = load_small(proof_file)
            if not isinstance(proof, dict):
                raise RecoveryError("Invalid private OneDrive readback proof.")
            enrollment = proof.get("enrollment")
            if (
                proof.get("point") != cloud
                or proof.get("sha256") != signed[cloud]["sha256"]
                or not isinstance(enrollment, list)
                or len(enrollment) != 2
                or not all(isinstance(value, str) and value for value in enrollment)
                or proof.get("lineage", scope["lineage"]) != scope["lineage"]
                or not isinstance(proof.get("verifiedAt"), str)
                or not proof["verifiedAt"]
            ):
                raise RecoveryError(
                    "The private OneDrive readback proof conflicts with its signed point."
                )
            prior = scope | {"enrollment": enrollment}
            mark.update(
                cloudPoint=cloud,
                cloudVerifiedAt=proof["verifiedAt"],
                sha256=proof["sha256"],
            )
    except (
        OSError,
        AttributeError,
        KeyError,
        TypeError,
        ValueError,
        RecoveryError,
        subprocess.SubprocessError,
    ) as exc:
        raise RecoveryError(
            f"Cannot authenticate the legacy backup evidence with the commissioned receipt trust. {failure_reason(exc)} "
            "Restore its signed point and private evidence, or commission a separate source with a fresh workstation state."
        ) from exc
    prior_id = scope_id(prior)
    status["cloudMarks"] = {prior_id: mark}
    if cloud:
        status["cloudScope"] = prior_id


def verify_onedrive(
    config, state, receipts, status, *, allow_retry=True, local_status=None
):
    import onedrive_reader

    status["cloudSyncVerified"] = False
    legacy = "cloudMarks" not in status and (
        status.get("point") or status.get("cloudPoint")
    )
    if (
        local_status
        and not legacy
        and local_status["point"] >= (status.get("point") or "")
    ):
        status.update(local_status)
    profile = onedrive_reader.profile_path(config["oneDriveProfile"])
    authfile = profile / "authorization.json"
    auth = json.loads(authfile.read_text()) if authfile.is_file() else {}
    if auth.get("state") != "ready" or not auth.get("verifiedAt"):
        raise RecoveryError(
            "Complete the Microsoft account sign-in for the OneDrive read-only backup reader."
        )
    enrollment = [str(profile), auth["verifiedAt"]]
    trust = load_small(config["receiptTrust"])
    _, lineage = validated_pin(trust)
    scope = {"enrollment": enrollment, "lineage": [str(value) for value in lineage]}
    proofs = state / "onedrive-readbacks"
    proofs.mkdir(mode=0o700, exist_ok=True)
    latest = max(receipt["point"] for receipt in receipts)
    if legacy:
        migrate_legacy_evidence(
            outside_checkout(config["mirror"]), trust, proofs, scope, status
        )
    active_scope = scope_id(scope)
    current = status.setdefault("cloudMarks", {}).setdefault(active_scope, {})
    high_water = current.get("cloudPoint", "")
    if high_water:
        status.update(
            cloudScope=active_scope,
            cloudPoint=high_water,
            cloudVerifiedAt=current["cloudVerifiedAt"],
        )
    last_seen = max(high_water, current.get("controllerPoint", ""))
    current["controllerPoint"] = max(latest, last_seen)
    if latest < last_seen:
        raise RecoveryError(
            "The controller inventory ends before a previously authenticated recovery point."
        )
    if local_status:
        status.update(local_status)
    pending = []
    for receipt in receipts:
        point = receipt["point"]
        proof_file = proofs / (point + ".json")
        evidence = json.loads(proof_file.read_text()) if proof_file.exists() else {}
        if (
            point == high_water
            and current.get("sha256")
            and receipt["sha256"] != current["sha256"]
        ):
            raise RecoveryError(
                "The controller archive differs from the previously verified OneDrive point."
            )
        if (
            point != latest
            and evidence.get("point") == point
            and evidence.get("sha256") == receipt["sha256"]
            and evidence.get("enrollment") == enrollment
            and evidence.get("lineage") == scope["lineage"]
        ):
            continue
        pending.append(receipt)
    delays = iter(READBACK_DELAYS if allow_retry else ())
    deadline = monotonic() + READBACK_WINDOW
    retry_deadline = None
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
        for index, receipt in enumerate(pending):
            try:
                onedrive_reader.remaining_timeout(READBACK_WINDOW, retry_deadline)
                verified = record_readback(
                    profile,
                    config["receiptTrust"],
                    state,
                    receipt,
                    scope,
                    retry_deadline,
                )
                point = receipt["point"]
                if point >= high_water:
                    high_water = point
                    mark = {"cloudPoint": point, "cloudVerifiedAt": verified}
                    current.update(mark, sha256=receipt["sha256"])
                    status.update(cloudScope=active_scope, **mark)
                    write_private(status_file, json.dumps(status))
                if point == latest:
                    status["cloudSyncVerified"] = True
            except onedrive_reader.ReadbackDeadline as exc:
                delayed.extend(pending[index:])
                delay_reason = failure_reason(exc)
                break
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
        if delay is not None and monotonic() + delay < deadline:
            status.update(
                state="waiting-onedrive",
                nextReadbackAt=(
                    datetime.now(timezone.utc) + timedelta(seconds=delay)
                ).isoformat(),
            )
            write_private(status_file, json.dumps(status))
            sleep(delay)
            if monotonic() < deadline:
                pending = delayed
                retry_deadline = deadline
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
                creationflags=NO_WINDOW,
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
            seen = set()
            for line in response.stdout.splitlines():
                try:
                    receipt = json.loads(line)
                    point = validate_point(receipt["point"])
                    if point in seen:
                        raise RecoveryError(
                            "The controller inventory contains a duplicate recovery point."
                        )
                    seen.add(point)
                    copied += int(sync_point(ssh, target, state, receipt, trust))
                    latest = max(latest or "", point)
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
                local_status = dict(
                    point=latest,
                    verifiedAt=datetime.now(timezone.utc).isoformat(),
                    copied=copied,
                    cloudSyncVerified=False,
                )
            if receipts and config.get("oneDriveProfile"):
                try:
                    verify_onedrive(
                        config,
                        state,
                        receipts,
                        status,
                        allow_retry=not failed,
                        local_status=local_status,
                    )
                except (
                    OSError,
                    ValueError,
                    KeyError,
                    RecoveryError,
                    subprocess.SubprocessError,
                ) as exc:
                    failed.append(failure_reason(exc))
            elif latest:
                status.update(local_status)
            if failed:
                status["cloudSyncVerified"] = False
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
    public_status = {
        key: value
        for key, value in status.items()
        if key not in ("cloudScope", "cloudMarks")
    }
    print(json.dumps(public_status | {"fresh": fresh, "cloudFresh": cloud_fresh}))
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
