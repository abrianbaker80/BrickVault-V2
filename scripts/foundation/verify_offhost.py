"""Qualify off-host operations with a separate local rclone destination and synthetic data."""

import json
from pathlib import Path
import secrets
import subprocess
import sys

from instance import Instance, init, write_private
from recovery import digest
from runtime import start, stop
from setup import rclone_program


def rejected(command):
    if (
        subprocess.run(
            command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL
        ).returncode
        == 0
    ):
        raise AssertionError("An invalid off-host operation succeeded.")


def verify_offhost(source, lab, port, expected, client_type):
    folder = lab / "offhost-drill"
    folder.mkdir(mode=0o700)
    provider = folder / "provider"
    provider.write_text("Provider temporarily unavailable", encoding="utf-8")
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
    rejected(publish)
    (pending,) = receipts.glob("*.pending.json")
    point = pending.name.removesuffix(".pending.json")
    receipt = receipts / (point + ".json")
    if receipt.exists() or mirror.exists():
        raise AssertionError("An interrupted transfer was recorded as complete.")
    provider.unlink()
    provider.mkdir()
    other_route = folder / "distinct-route.json"
    settings = json.loads(route.read_text())
    settings["remote"] = "sample:other"
    write_private(other_route, json.dumps(settings))
    retry = [*publish, "--resume", point]
    wrong_route = retry.copy()
    wrong_route[wrong_route.index("--transport") + 1] = other_route
    rejected(wrong_route)
    wrong_mirror = retry.copy()
    wrong_mirror[wrong_mirror.index("--mirror") + 1] = folder / "other-mirror"
    rejected(wrong_mirror)
    if (folder / "other-mirror").exists():
        raise AssertionError("A retry redirected the local mirror.")
    original_profile = config.read_bytes()
    other_store = folder / "another-provider"
    other_store.mkdir()
    changed_profile = folder / "changed.conf"
    write_private(
        changed_profile, "[sample]\ntype = alias\nremote = " + str(other_store) + "\n"
    )
    subprocess.run(
        [
            rclone_program(),
            "--config",
            changed_profile,
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
    try:
        config.write_bytes(changed_profile.read_bytes())
        rejected(retry)
    finally:
        config.write_bytes(original_profile)
    if any(other_store.iterdir()):
        raise AssertionError("A repointed alias received a previous recovery point.")
    alternate = init(folder / "unrelated-instance", folder / "unrelated-keys", port)
    wrong_source = retry.copy()
    wrong_source[wrong_source.index("--state") + 1] = alternate.path
    rejected(wrong_source)
    if any(provider.iterdir()) or receipt.exists() or not pending.is_file():
        raise AssertionError("A mismatched retry sent or finalized a recovery point.")
    subprocess.run(retry, check=True)
    subprocess.run(retry, check=True)
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
    recover = [
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
    ]
    damaged = folder / "wrong-receipt" / receipt.name
    damaged.parent.mkdir()
    contents = json.loads(receipt.read_text())
    contents["sha256"] = "0" * 64
    write_private(damaged, json.dumps(contents))
    incorrect = recover.copy()
    incorrect[incorrect.index("--receipt") + 1] = damaged
    rejected(incorrect)
    if target.exists() or (folder / "new-keys").exists():
        raise AssertionError(
            "Failure to verify trusted receipt changed destination state."
        )
    subprocess.run(recover, check=True)
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
