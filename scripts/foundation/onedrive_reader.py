"""Enroll a read-only OneDrive Personal reader and authenticate fresh cloud downloads."""

import argparse
from datetime import datetime, timezone
import http.client
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
from urllib.parse import parse_qs, urlsplit

from cloud_receipt import FIELDS, SIDECAR, load_small, verify_directory, verify_sidecar
from instance import child_env, write_private
from offhost import (
    RecoveryError,
    failure_reason,
    managed_dir,
    outside_checkout,
    validate_point,
)
from runtime import exclusive
from setup import rclone_program

REMOTE = "bv2onedrive"
SCOPES = {"Files.Read", "offline_access"}
FOLDER = "BrickVault V2 Backup"


class ReadbackPending(RecoveryError):
    """The expected provider object is missing or the transfer is temporarily unavailable."""


class ReadbackDeadline(RecoveryError):
    """The current invocation has exhausted its provider retry window."""


def remaining_timeout(limit, deadline):
    if deadline is None:
        return limit
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise ReadbackDeadline("OneDrive provider retry window expired.")
    return min(limit, remaining)


def reader_command(args, timeout, deadline, **options):
    try:
        result = subprocess.run(
            args,
            env=child_env(),
            timeout=remaining_timeout(timeout, deadline),
            **options,
        )
    except subprocess.TimeoutExpired:
        remaining_timeout(timeout, deadline)
        raise
    remaining_timeout(timeout, deadline)
    return result


def private_path(path):
    path = outside_checkout(path)
    for name in ("OneDrive", "OneDriveConsumer", "OneDriveCommercial"):
        value = os.environ.get(name)
        if value and path.is_relative_to(Path(value).resolve()):
            raise RecoveryError(
                "Keep the reader profile and independent downloads outside OneDrive sync storage."
            )
    return path


def profile_path(path):
    directory = private_path(path)
    if (directory / ".brickvault-v2-offhost").read_text() != "onedrive-reader":
        raise RecoveryError("Use the protected V2 OneDrive reader profile.")
    return directory


def command(profile, program, config_file=None):
    helper = Path(__file__).with_name("OneDrive-ReaderPassword.ps1").resolve()
    loader = [
        "pwsh",
        "-NoProfile",
        "-NonInteractive",
        "-File",
        helper.as_posix(),
        "-ProtectedFile",
        (profile / "reader.dpapi").as_posix(),
    ]
    return [
        str(program),
        "--config",
        str(config_file or profile / "reader.conf"),
        "--ask-password=false",
        "--password-command",
        subprocess.list2cmdline(loader),
    ]


def check_remote(base, deadline=None):
    listing = reader_command(
        [*base, "listremotes", "--exact", "--name", REMOTE, "--type", "onedrive"],
        60,
        deadline,
        capture_output=True,
        check=True,
    )
    if listing.stdout.decode().strip() != REMOTE + ":":
        raise RecoveryError(
            "Expected a native OneDrive remote in the V2 reader profile."
        )


def open_authorization(local_url):
    start = urlsplit(local_url)
    if (
        start.scheme != "http"
        or start.netloc != "127.0.0.1:53682"
        or start.path != "/auth"
    ):
        raise RecoveryError("Unexpected local authorization address.")
    connection = http.client.HTTPConnection(start.hostname, start.port, timeout=15)
    try:
        connection.request("GET", start.path + "?" + start.query)
        response = connection.getresponse()
        location = response.getheader("Location", "")
        auth = urlsplit(location)
        scope = set(parse_qs(auth.query).get("scope", [""])[0].split())
        if (
            response.status not in (302, 303, 307)
            or auth.scheme != "https"
            or auth.netloc != "login.microsoftonline.com"
            or auth.path != "/consumers/oauth2/v2.0/authorize"
            or scope != SCOPES
        ):
            raise RecoveryError(
                "Microsoft authorization did not request exactly Files.Read and offline_access."
            )
    finally:
        connection.close()
    os.startfile(location)


def encrypted_config(path):
    if path.is_symlink():
        raise RecoveryError("The V2 reader configuration is a symbolic link.")
    with path.open("rb") as raw:
        if b"RCLONE_ENCRYPT_V0:" not in raw.read(256):
            raise RecoveryError(
                "Reader profile requires rclone configuration encryption."
            )


def authorize(base, progress):
    args = [
        *base,
        "config",
        "create",
        REMOTE,
        "onedrive",
        "access_scopes",
        "Files.Read offline_access",
        "config_is_local",
        "true",
        "config_auth_no_browser",
        "true",
        "config_type",
        "onedrive",
        "auth_url",
        "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize",
        "token_url",
        "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
        "--no-output",
    ]
    observed = False
    write_private(progress, json.dumps({"state": "starting"}))
    with subprocess.Popen(
        args,
        env=child_env(),
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
    ) as proc:
        try:
            for line in proc.stderr:
                match = re.search(r"http://127\.0\.0\.1:53682/auth\?state=[^\s]+", line)
                if match:
                    open_authorization(match.group())
                    observed = True
                    write_private(
                        progress,
                        json.dumps(
                            {
                                "state": "awaiting-account-owner",
                                "scopes": sorted(SCOPES),
                            }
                        ),
                    )
            if proc.wait() != 0 or not observed:
                raise RecoveryError(
                    "Microsoft sign-in did not complete. Rerun the reader connection."
                )
        finally:
            if proc.poll() is None:
                proc.kill()


def enroll(profile, program, reauthorize=False):
    state = profile / "authorization.json"
    encrypted = profile / "reader.conf"
    if encrypted.is_symlink():
        raise RecoveryError("The reader profile must contain its own configuration.")
    if encrypted.exists() and encrypted.stat().st_size:
        encrypted_config(encrypted)
        if (
            state.is_file()
            and json.loads(state.read_text()).get("state") == "ready"
            and not reauthorize
        ):
            check_remote(command(profile, program))
            print("OneDrive reader already enrolled.")
            return
    secret = profile / "reader.dpapi"
    if not secret.exists():
        if encrypted.exists() and encrypted.stat().st_size:
            raise RecoveryError(
                "Existing encrypted reader profile needs its original Windows credential."
            )
        subprocess.run(
            [
                "pwsh",
                "-NoProfile",
                "-NonInteractive",
                "-File",
                Path(__file__).with_name("OneDrive-ReaderPassword.ps1"),
                "-ProtectedFile",
                secret,
                "-Initialize",
            ],
            env=child_env(),
            check=True,
            stdout=subprocess.DEVNULL,
        )
    # A new encrypted config forces a fresh grant. An interrupted enrollment cannot alter existing credentials.
    with tempfile.TemporaryDirectory(dir=profile) as temp:
        staged = Path(temp) / "reader.conf"
        staged.touch(exist_ok=False)
        base = command(profile, program, staged)
        subprocess.run(
            [*base, "config", "encryption", "set"],
            env=child_env(),
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        encrypted_config(staged)
        authorize(base, profile / "enrollment.json")
        check_remote(base)
        os.replace(staged, encrypted)
    write_private(
        state,
        json.dumps(
            {
                "state": "ready",
                "scopes": sorted(SCOPES),
                "verifiedAt": datetime.now(timezone.utc).isoformat(),
            }
        ),
    )
    write_private(profile / "enrollment.json", json.dumps({"state": "complete"}))
    print("OneDrive read-only account connected.")


def connect(path, program=None, reauthorize=False):
    if os.name != "nt":
        raise RecoveryError(
            "Enroll the local V2 reader under its Windows owner account."
        )
    profile = managed_dir(private_path(path), "onedrive-reader")
    with exclusive(profile / "connect.lock"):
        enroll(profile, program or rclone_program(), reauthorize)


def download(
    profile, trust, point, output, program=None, *, expected_receipt=None, deadline=None
):
    directory = profile_path(profile)
    if (
        json.loads((directory / "authorization.json").read_text()).get("state")
        != "ready"
    ):
        raise RecoveryError("The owner must complete the OneDrive reader connection.")
    encrypted_config(directory / "reader.conf")
    base = command(directory, program or rclone_program())
    check_remote(base, deadline)
    point = validate_point(point)
    output = private_path(output)
    if output.exists():
        raise RecoveryError("Choose a fresh OneDrive recovery download directory.")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output.parent) as tmp:
        stage = Path(tmp) / "download"
        stage.mkdir(mode=0o700)
        for suffix in (SIDECAR, ".age"):
            name = point + suffix
            result = reader_command(
                [
                    *base,
                    "copyto",
                    "--immutable",
                    "--retries",
                    "2",
                    "--low-level-retries",
                    "3",
                    "--timeout",
                    "2m",
                    REMOTE + ":" + FOLDER + "/" + name,
                    stage / name,
                ],
                1800,
                deadline,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            if result.returncode in (3, 4, 5):
                raise ReadbackPending(
                    f"OneDrive recovery object is unavailable (rclone exit {result.returncode})."
                )
            if result.returncode:
                raise RecoveryError(
                    f"OneDrive has not returned the requested recovery point (rclone exit {result.returncode})."
                )
            if suffix == SIDECAR:
                signed = verify_sidecar(stage / name, point, load_small(trust))
                if expected_receipt is not None and any(
                    signed[field] != expected_receipt.get(field) for field in FIELDS
                ):
                    raise RecoveryError(
                        "OneDrive's signed point differs from the selected controller point."
                    )
        if verify_directory(stage, point, trust) != signed:
            raise RecoveryError(
                "OneDrive's signed receipt changed during the download."
            )
        stage.rename(output)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    commands = p.add_subparsers(dest="action", required=True)
    enroll = commands.add_parser("connect")
    enroll.add_argument("--reauthorize", action="store_true")
    retrieve = commands.add_parser("download")
    for item in (enroll, retrieve):
        item.add_argument("--profile", type=Path, required=True)
    for name in ("trust", "output", "point"):
        retrieve.add_argument("--" + name, required=True)
    args = p.parse_args()
    try:
        if args.action == "connect":
            connect(args.profile, reauthorize=args.reauthorize)
        else:
            download(args.profile, args.trust, args.point, args.output)
            print("Authenticated fresh OneDrive recovery download " + args.point)
    except (
        OSError,
        ValueError,
        KeyError,
        RecoveryError,
        subprocess.SubprocessError,
    ) as exc:
        p.exit(1, f"OneDrive {args.action} failed: {failure_reason(exc)}\n")


if __name__ == "__main__":
    main()
