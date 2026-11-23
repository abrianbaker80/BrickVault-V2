"""Qualify off-host operations with a separate local rclone destination and synthetic data."""

import json
from pathlib import Path
import secrets
import subprocess
import sys

from instance import Instance, write_private
from recovery import digest
from runtime import start, stop
from setup import rclone_program


def verify_offhost(source, lab, port, expected, client_type):
    folder = lab / "offhost-drill"
    folder.mkdir(mode=0o700)
    provider = folder / "provider"
    provider.mkdir()
    config = folder / "profile.conf"
    write_private(config, "[sample]\ntype = alias\nremote = " + str(provider) + "\n")
    write_private(folder / "password", secrets.token_urlsafe(32))
    helper = folder / "read_password.py"
    write_private(
        helper,
        "from pathlib import Path\nprint(Path(__file__).with_name('password').read_text())\n",
    )
    password_command = f'"{sys.executable}" "{helper}"'
    subprocess.run(
        [
            rclone_program(),
            "--config",
            config,
            "--password-command",
            password_command,
            "config",
            "encryption",
            "set",
        ],
        check=True,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    fingerprint = digest(config)
    route = folder / "route.json"
    write_private(
        route,
        json.dumps(
            {
                "remote": "sample:",
                "config": str(config),
                "password_command": password_command,
            }
        ),
    )
    cli = [sys.executable, Path(__file__).with_name("offhost.py")]
    receipts = folder / "receipts"
    mirror = folder / "mirror"
    publish = [
        *cli,
        "publish",
        "--state",
        source.path,
        "--transport",
        route,
        "--receipts",
        receipts,
        "--mirror",
        mirror,
    ]
    subprocess.run(publish, check=True)
    (receipt,) = receipts.glob("*.json")
    point = receipt.stem
    subprocess.run([*publish, "--resume", point], check=True)
    copies = list(provider.rglob("*.age"))
    if (
        len(copies) != 1
        or copies[0].parent != provider / "BrickVault-V2"
        or digest(copies[0]) != digest(mirror / (point + ".age"))
    ):
        raise AssertionError(
            "Recovery point did not remain isolated and byte-identical after a retry."
        )
    stop(source)
    target = folder / "recovered"
    key = lab / "recovery" / (source.config["id"] + ".agekey")
    subprocess.run(
        [
            *cli,
            "recover",
            "--receipt",
            receipt,
            "--identity",
            key,
            "--state",
            target,
            "--recovery",
            folder / "new-keys",
            "--port",
            str(port),
            "--transport",
            route,
        ],
        check=True,
    )
    restored = Instance(target)
    try:
        start(restored)
        browser = client_type(restored)
        browser.login((target / "owner-login.txt").read_text().splitlines()[1])
        workspace = browser.call("workspace")
        if any(
            workspace[group] != expected[group]
            for group in ("catalog", "locations", "audit")
        ):
            raise AssertionError(
                "Provider download did not reproduce original business records."
            )
    finally:
        stop(restored)
    if digest(config) != fingerprint:
        raise AssertionError("The supplied transport configuration changed.")
    print(
        "Encrypted off-host CLI publish, retry, independent download and restored sign-in passed.",
        flush=True,
    )
