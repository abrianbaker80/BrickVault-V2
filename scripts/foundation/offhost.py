"""Publish and recover an encrypted BrickVault V2 database and its exact release."""

import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import json
import ntpath
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import uuid

from instance import (
    BIN,
    EXT,
    NO_WINDOW,
    ROOT,
    Instance,
    child_env,
    execute,
    init,
    private_dir,
    write_private,
)
from recovery import activate, backup, digest, restore
from runtime import operation, running
from setup import rclone_program

POINT = re.compile(r"\d{8}T\d{6}Z-[0-9a-f]{32}")
HEX = re.compile(r"[0-9a-f]{64}")
FORMAT = "brickvault-v2-offhost-1"


class RecoveryError(Exception):
    """An operator-facing error without credentials, private paths or provider output."""


def outside_checkout(path):
    path = Path(path).resolve()
    if path == ROOT or ROOT in path.parents:
        raise RecoveryError(
            "Keep backup state and recovery material outside the checkout."
        )
    return path


def disjoint(*paths):
    for index, path in enumerate(paths):
        for other in paths[index + 1 :]:
            if path == other or path in other.parents or other in path.parents:
                raise RecoveryError(
                    "Choose separate, non-overlapping source, receipt, key and mirror directories."
                )


def managed_dir(path, purpose):
    """Never change permissions or write a marker in an unrelated populated directory."""
    path = outside_checkout(path)
    marker = path / ".brickvault-v2-offhost"
    if path.exists() and any(path.iterdir()):
        if (
            marker.is_symlink()
            or not marker.is_file()
            or marker.read_text(encoding="utf-8") != purpose
        ):
            raise RecoveryError(
                "Choose an empty or previously initialized V2 "
                + purpose
                + " directory."
            )
    private_dir(path)
    write_private(marker, purpose)
    return path


def validate_point(point):
    if not isinstance(point, str) or not POINT.fullmatch(point):
        raise RecoveryError("Invalid V2 recovery point identifier.")
    return point


def read_receipt(file, point):
    receipt = json.loads(file.read_text(encoding="utf-8"))
    if (
        not isinstance(receipt, dict)
        or receipt.get("format") != FORMAT
        or receipt.get("point") != point
    ):
        raise RecoveryError("The receipt does not identify the expected V2 point.")
    if (
        not HEX.fullmatch(str(receipt.get("sha256", "")))
        or type(receipt.get("bytes")) is not int
        or receipt["bytes"] <= 0
    ):
        raise RecoveryError("Invalid recovery-set digest or byte count.")
    return receipt


def verify_file(file, receipt):
    if file.stat().st_size != receipt["bytes"] or digest(file) != receipt["sha256"]:
        raise RecoveryError("Recovery-set bytes differ from the trusted receipt.")


def reserved_name(name):
    if hasattr(ntpath, "isreserved"):
        return ntpath.isreserved(name)
    return PureWindowsPath(name).is_reserved()  # Python 3.12


def portable_path(name):
    path = PurePosixPath(name)
    if (
        not path.parts
        or path.is_absolute()
        or path.as_posix() != name
        or any(
            part in (".", "..")
            or part.endswith((" ", "."))
            or re.search(r'[\\:<>"|?*\x00-\x1f]', part)
            or reserved_name(part)
            for part in path.parts
        )
    ):
        raise RecoveryError("Unexpected recovery-set member name.")
    return path


def operating_system():
    machine = platform.machine().lower()
    return [sys.platform, "x64" if machine in ("amd64", "x86_64") else machine]


@contextmanager
def transport(descriptor, scratch):
    descriptor = outside_checkout(descriptor)
    route = json.loads(descriptor.read_text(encoding="utf-8"))
    if not isinstance(route, dict) or any(
        not isinstance(route.get(k), str) or not route[k]
        for k in ("remote", "config", "password_command")
    ):
        raise RecoveryError(
            "The external transport needs a named remote, encrypted config and password command."
        )
    match = re.fullmatch(r"([A-Za-z0-9_][A-Za-z0-9_. -]*):([^:\\]*)", route["remote"])
    if not match:
        raise RecoveryError(
            "Use a named remote in the supplied transport configuration."
        )
    prefix = match[2].rstrip("/")
    if prefix:
        portable_path(prefix)
    base = match[1] + ":" + (prefix + "/" if prefix else "") + "BrickVault-V2/"
    config = outside_checkout(route["config"])
    isolated = scratch / "transfer.conf"
    # rclone can refresh its active config. Only an encrypted copy ever enters this scratch directory.
    with config.open("rb") as source:
        if not re.search(rb"(?m)^RCLONE_ENCRYPT_V0:\r?$", source.read(256)):
            raise RecoveryError(
                "A password-protected rclone configuration is required."
            )
        source.seek(0)
        with isolated.open("xb") as target:
            shutil.copyfileobj(source, target)
    program = rclone_program()
    command = [
        program,
        "--config",
        str(isolated),
        "--ask-password=false",
        "--password-command",
        route["password_command"],
        "--retries",
        "3",
        "--low-level-retries",
        "5",
        "--contimeout",
        "30s",
        "--timeout",
        "2m",
    ]

    def transfer(source, destination):
        result = subprocess.run(
            [*command, "copyto", "--immutable", "--checksum", source, destination],
            env=child_env(),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=1800,
            creationflags=NO_WINDOW,
        )
        if result.returncode:
            raise RecoveryError(
                f"The configured rclone transfer failed (exit {result.returncode})."
            )

    try:
        yield base, transfer
    finally:
        isolated.unlink(missing_ok=True)


def release_manifest(instance, folder):
    online = running(instance)
    if not online:
        instance.pg_start()
    try:
        return backup(instance, folder / "database.age")
    finally:
        if not online:
            instance.pg_stop()


def make_set(instance, receipt_dir, point):
    with tempfile.TemporaryDirectory(dir=instance.path) as temporary:
        folder = Path(temporary)
        with operation(instance):
            database = release_manifest(instance, folder)
            meta = {
                "format": FORMAT,
                "point": point,
                "system": operating_system(),
                "release": database["release"],
                "databaseSha256": digest(folder / "database.age"),
            }
            (folder / "set.json").write_text(
                json.dumps(meta, sort_keys=True), encoding="utf-8"
            )
            release = instance.path / "release"
            with tarfile.open(
                folder / "set.tar.gz", "w:gz", dereference=True
            ) as archive:
                archive.add(folder / "set.json", arcname="set.json")
                archive.add(folder / "database.age", arcname="database.age")
                for name, checksum in meta["release"].items():
                    relative = portable_path(name)
                    source = release / relative
                    if (
                        source.resolve() != source
                        or not source.is_file()
                        or digest(source) != checksum
                    ):
                        raise RecoveryError(
                            "The source release changed during capture."
                        )
                    archive.add(source, arcname="release/" + name, recursive=False)
            staged = receipt_dir / ("." + point + ".age")
            try:
                execute(
                    [
                        BIN / ("age" + EXT),
                        "-r",
                        instance.config["recipient"],
                        "-o",
                        staged,
                        folder / "set.tar.gz",
                    ]
                )
                os.link(staged, receipt_dir / (point + ".age"))
            finally:
                staged.unlink(missing_ok=True)
    file = receipt_dir / (point + ".age")
    receipt = {
        "format": FORMAT,
        "point": point,
        "sha256": digest(file),
        "bytes": file.stat().st_size,
    }
    write_private(
        receipt_dir / (point + ".pending.json"), json.dumps(receipt, sort_keys=True)
    )


def mirror_set(mirror, encrypted, receipt):
    folder = managed_dir(mirror, "mirror")
    dst = folder / encrypted.name
    if dst.exists():
        verify_file(dst, receipt)
    else:
        with tempfile.TemporaryDirectory(dir=folder) as temp:
            staged = Path(temp) / encrypted.name
            shutil.copyfile(encrypted, staged)
            verify_file(staged, receipt)
            os.replace(staged, dst)
    write_private(
        folder / (receipt["point"] + ".json"), json.dumps(receipt, sort_keys=True)
    )


def publish(args):
    source = outside_checkout(args.state)
    receipts = outside_checkout(args.receipts)
    paths = [source, receipts]
    if args.mirror:
        paths.append(outside_checkout(args.mirror))
    disjoint(*paths)
    managed_dir(receipts, "receipts")
    if args.resume:
        point = validate_point(args.resume)
    else:
        point = (
            datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ-") + uuid.uuid4().hex
        )
        make_set(Instance(source), receipts, point)
    print("Prepared recovery point " + point, flush=True)
    pending = receipts / (point + ".pending.json")
    final = receipts / (point + ".json")
    receipt = read_receipt(final if final.exists() else pending, point)
    local = receipts / (point + ".age")
    verify_file(local, receipt)
    with tempfile.TemporaryDirectory(dir=receipts) as temp:
        scratch = Path(temp)
        with transport(args.transport, scratch) as (base, transfer):
            remote = base + point + ".age"
            transfer(local, remote)
            received = scratch / "provider-readback.age"
            transfer(remote, received)
            verify_file(received, receipt)
    if args.mirror:
        mirror_set(args.mirror, local, receipt)
    write_private(final, json.dumps(receipt, sort_keys=True))
    pending.unlink(missing_ok=True)
    print("Verified published recovery point " + point)


def unpack_release(archive_file, scratch, point):
    with tarfile.open(archive_file, "r:gz") as archive:
        members = archive.getmembers()
        names = [m.name for m in members]
        if any(not m.isfile() for m in members) or len(names) != len(
            set(n.casefold() for n in names)
        ):
            raise RecoveryError("Invalid or duplicate recovery-set member.")
        if (
            "set.json" not in names
            or archive.getmember("set.json").size > 4 * 1024 * 1024
        ):
            raise RecoveryError(
                "Recovery-set metadata is missing or exceeds its format limit."
            )
        with archive.extractfile("set.json") as file:
            meta = json.load(file)
        if (
            not isinstance(meta, dict)
            or meta.get("format") != FORMAT
            or meta.get("point") != point
            or meta.get("system") != operating_system()
            or not isinstance(meta.get("release"), dict)
        ):
            raise RecoveryError(
                "Incompatible point, operating system or recovery format."
            )
        expected = {"database.age": meta.get("databaseSha256")}
        for name, checksum in meta["release"].items():
            portable_path(name)
            expected["release/" + name] = checksum
        if (
            "release/BrickVault.Api.dll" not in expected
            or set(names) != {"set.json", *expected}
            or any(
                not isinstance(h, str) or not HEX.fullmatch(h)
                for h in expected.values()
            )
        ):
            raise RecoveryError("Unexpected release contents in the recovery set.")
        for name, checksum in expected.items():
            file = scratch / portable_path(name)
            file.parent.mkdir(parents=True, exist_ok=True)
            with archive.extractfile(name) as src, file.open("xb") as dst:
                shutil.copyfileobj(src, dst)
            if os.name != "nt" and archive.getmember(name).mode & 0o100:
                file.chmod(0o700)
            if digest(file) != checksum:
                raise RecoveryError(
                    "Recovered content failed its release or database fingerprint."
                )


def recover(args):
    receipt_path = outside_checkout(args.receipt)
    point = validate_point(receipt_path.stem)
    receipt = read_receipt(receipt_path, point)
    state = outside_checkout(args.state)
    keys = outside_checkout(args.recovery)
    identity = outside_checkout(args.identity)
    disjoint(state, keys)
    if state.exists() or state in identity.parents or not identity.is_file():
        raise RecoveryError(
            "Recovery needs an unused destination path and an independently held source key."
        )
    if keys.exists() and any(
        not (
            p.is_file()
            and not p.is_symlink()
            and re.fullmatch(r"[0-9a-f]{32}\.agekey", p.name)
        )
        for p in keys.iterdir()
    ):
        raise RecoveryError("Use a dedicated recovery key directory.")
    state.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=state.parent) as temporary:
        scratch = private_dir(temporary)
        with transport(args.transport, scratch) as (base, transfer):
            transfer(base + point + ".age", scratch / "remote.age")
        verify_file(scratch / "remote.age", receipt)
        execute(
            [
                BIN / ("age" + EXT),
                "-d",
                "-i",
                identity,
                "-o",
                scratch / "set.tar.gz",
                scratch / "remote.age",
            ]
        )
        unpack_release(scratch / "set.tar.gz", scratch, point)
        # All release and database verification completes on a private staging instance.
        staged = init(
            scratch / "runtime",
            scratch / "keys",
            args.port,
            release=scratch / "release",
        )
        staged.pg_start()
        target = "bv_m2_restored"
        try:
            restore(staged, scratch / "database.age", identity, target)
        finally:
            staged.pg_stop()
        activate(staged, target)
        private_dir(keys)
        new_key = staged.config["id"] + ".agekey"
        key_file = keys / new_key
        with (scratch / "keys" / new_key).open("rb") as src, os.fdopen(
            os.open(
                key_file,
                os.O_CREAT | os.O_EXCL | os.O_WRONLY | getattr(os, "O_BINARY", 0),
                0o600,
            ),
            "wb",
        ) as dst:
            shutil.copyfileobj(src, dst)
            dst.flush()
            os.fsync(dst.fileno())
        try:
            staged.path.rename(state)
        except OSError:
            key_file.unlink()
            raise
    print("Verified and activated recovered point " + point)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    send = commands.add_parser(
        "publish",
        help="Build the V2 recovery set and verify a fresh provider download.",
    )
    send.add_argument("--receipts", type=Path, required=True)
    send.add_argument(
        "--mirror",
        type=Path,
        help="Optional dedicated directory for a local encrypted mirror.",
    )
    send.add_argument(
        "--resume",
        metavar="POINT",
        help="Resume a retained encrypted point after a transport failure.",
    )
    receive = commands.add_parser(
        "recover", help="Recover a trusted receipt to a new, stopped local instance."
    )
    receive.add_argument("--receipt", type=Path, required=True)
    receive.add_argument("--identity", type=Path, required=True)
    receive.add_argument("--recovery", type=Path, required=True)
    receive.add_argument("--port", type=int, required=True)
    for command in (send, receive):
        command.add_argument("--state", type=Path, required=True)
        command.add_argument("--transport", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.action == "publish":
            publish(args)
        else:
            recover(args)
    except RecoveryError as exc:
        parser.exit(1, str(exc) + "\n")
    except (
        OSError,
        ValueError,
        KeyError,
        tarfile.TarError,
        subprocess.SubprocessError,
        RuntimeError,
    ) as exc:
        parser.exit(
            1,
            f"Recovery {args.action} failed ({type(exc).__name__}). Check the inputs and private instance diagnostics.\n",
        )


if __name__ == "__main__":
    main()
