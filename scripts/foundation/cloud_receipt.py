"""Sign controller receipts and verify provider copies with an independent public key."""

import argparse
import base64
from datetime import datetime
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import uuid

from instance import child_env
from offhost import (
    FORMAT,
    HEX,
    POINT,
    RecoveryError,
    capture_transport,
    failure_reason,
    outside_checkout,
    point_time,
    transport,
    validate_point,
    verify_file,
)
from setup import rclone_program

NAMESPACE = "brickvault-v2-cloud-receipt-v1"
SIGNER = "controller"
SIDECAR = ".receipt.json"
FIELDS = (
    "format",
    "point",
    "sourceInstance",
    "workspaceId",
    "publicationContext",
    "bytes",
    "sha256",
)


def encoded(value):
    return (
        json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)
        + "\n"
    ).encode("ascii")


def load_small(path):
    with Path(path).open("rb") as stream:
        data = stream.read(16385)
    if len(data) > 16384:
        raise RecoveryError("Oversized receipt or public trust file.")
    return json.loads(data)


def receipt_body(receipt, point, trust):
    if (
        not isinstance(receipt, dict)
        or receipt.get("point") != validate_point(point)
        or receipt.get("format") != FORMAT
        or type(receipt.get("bytes")) is not int
        or receipt["bytes"] <= 0
        or not HEX.fullmatch(str(receipt.get("sha256")))
        or not HEX.fullmatch(str(receipt.get("publicationContext")))
    ):
        raise RecoveryError("Invalid signed recovery receipt.")
    if uuid.UUID(receipt["sourceInstance"]) != uuid.UUID(
        trust["sourceId"]
    ) or uuid.UUID(receipt["workspaceId"]) != uuid.UUID(trust["workspaceId"]):
        raise RecoveryError(
            "The signed recovery point belongs to another source or workspace."
        )
    return {name: receipt[name] for name in FIELDS}


def ssh_keygen(args, content):
    result = subprocess.run(
        ["ssh-keygen", "-Y", *args],
        input=content,
        capture_output=True,
        env=child_env(),
        timeout=30,
    )
    if result.returncode:
        raise RecoveryError(
            "OpenSSH could not sign or authenticate this recovery receipt."
        )
    return result.stdout


def validated_pin(pin):
    if not isinstance(pin, dict):
        raise RecoveryError("Invalid signing pin in independently held receipt trust.")
    public = str(pin.get("publicKey", "")).split()
    if len(public) != 2 or public[0] != "ssh-ed25519":
        raise RecoveryError("Use an independently pinned Ed25519 public key.")
    try:
        key = base64.b64decode(public[1], validate=True)
    except ValueError as exc:
        raise RecoveryError("Invalid OpenSSH public-key encoding.") from exc
    # OpenSSH encodes the algorithm and raw 32-byte Ed25519 key as two SSH strings.
    prefix = b"\x00\x00\x00\x0bssh-ed25519\x00\x00\x00\x20"
    if not key.startswith(prefix) or len(key) != len(prefix) + 32:
        raise RecoveryError("Invalid OpenSSH Ed25519 public key.")
    try:
        lineage = uuid.UUID(pin["sourceId"]), uuid.UUID(pin["workspaceId"])
    except (AttributeError, TypeError, ValueError, KeyError) as exc:
        raise RecoveryError(
            "Signing pins require their source and workspace IDs."
        ) from exc
    return key, lineage


def signing_periods(trust):
    current_key, commissioned = validated_pin(trust)
    seen_keys = {current_key}
    previous = trust.get("previous", [])
    if not isinstance(previous, list):
        raise RecoveryError("Historical signing pins must be an ordered list.")
    last_cutover = ""
    for pin in previous:
        key, lineage = validated_pin(pin)
        if lineage != commissioned:
            raise RecoveryError(
                "Historical signing pins must retain the commissioned source and workspace IDs."
            )
        if key in seen_keys:
            raise RecoveryError("Each signing period requires a distinct Ed25519 key.")
        seen_keys.add(key)
        cutoff = pin.get("before")
        if not isinstance(cutoff, str) or not re.fullmatch(
            r"[0-9]{8}T[0-9]{6}Z", cutoff
        ):
            raise RecoveryError(
                "A historical signing pin needs an exclusive UTC cutover."
            )
        try:
            datetime.strptime(cutoff, "%Y%m%dT%H%M%SZ")
        except ValueError as exc:
            raise RecoveryError(
                "Invalid UTC cutover for historical signing pin."
            ) from exc
        if cutoff <= last_cutover:
            raise RecoveryError(
                "Historical signing cutovers must be strictly increasing."
            )
        last_cutover = cutoff
    return previous


def signing_pin(trust, point):
    """Select a single operator-pinned key and lineage for the signed capture time."""
    captured = point_time(point).strftime("%Y%m%dT%H%M%SZ")
    for pin in signing_periods(trust):
        if captured < pin["before"]:
            return pin
    return trust


def rotate_trust(current_file, new_pin_file, cutover, output):
    """Build a new operator-owned trust file from independently authenticated inputs."""
    current = load_small(current_file)
    previous = signing_periods(current)
    new = load_small(new_pin_file)
    _, incoming_lineage = validated_pin(new)
    if "previous" in new:
        raise RecoveryError(
            "Supply the single incoming controller pin and its lineage."
        )
    _, commissioned_lineage = validated_pin(current)
    if incoming_lineage != commissioned_lineage:
        raise RecoveryError(
            "A signing-key rotation must retain the commissioned source and workspace IDs."
        )
    fields = ("publicKey", "sourceId", "workspaceId")
    outgoing = {field: current[field] for field in fields} | {"before": cutover}
    combined = {field: new[field] for field in fields} | {
        "previous": [*previous, outgoing]
    }
    output = outside_checkout(output)
    with tempfile.TemporaryDirectory(dir=output.parent) as temp:
        stage = Path(temp) / "trust.json"
        stage.write_bytes(encoded(combined))
        signing_periods(load_small(stage))
        publish_local(stage, output)


def verify_sidecar(sidecar, point, trust):
    pin = signing_pin(trust, point)
    envelope = load_small(sidecar)
    if not isinstance(envelope, dict) or set(envelope) != {"receipt", "signature"}:
        raise RecoveryError("Invalid signed receipt envelope.")
    receipt = receipt_body(envelope["receipt"], point, pin)
    if envelope["receipt"] != receipt:
        raise RecoveryError("Unexpected fields in the signed receipt.")
    public = pin["publicKey"].split()
    with tempfile.TemporaryDirectory() as tmp:
        allowed = Path(tmp) / "allowed"
        allowed.write_bytes((SIGNER + " " + " ".join(public) + "\n").encode("ascii"))
        signature = Path(tmp) / "receipt.sig"
        signature.write_bytes(envelope["signature"].encode("ascii"))
        ssh_keygen(
            ["verify", "-f", allowed, "-I", SIGNER, "-n", NAMESPACE, "-s", signature],
            encoded(receipt),
        )
    return receipt


def publish_signed(route, folder, receipt, signing_key, trust_file):
    """Publish the signed sidecar last, after the caller has verified the provider ciphertext."""
    trust = load_small(trust_file)
    point = validate_point(receipt["point"])
    body = receipt_body(receipt, point, signing_pin(trust, point))
    with tempfile.TemporaryDirectory(dir=folder) as tmp:
        scratch = Path(tmp)
        sidecar = scratch / (point + SIDECAR)
        signature = ssh_keygen(
            ["sign", "-f", outside_checkout(signing_key), "-n", NAMESPACE],
            encoded(body),
        )
        sidecar.write_bytes(
            encoded({"receipt": body, "signature": signature.decode("ascii")})
        )
        verify_sidecar(sidecar, point, trust)
        with transport(route, scratch) as (prefix, transfer):
            transfer(sidecar, prefix + sidecar.name)
            readback = scratch / "provider.json"
            transfer(prefix + sidecar.name, readback)
        if (
            readback.read_bytes() != sidecar.read_bytes()
            or verify_sidecar(readback, point, trust) != body
        ):
            raise RecoveryError("Provider readback did not match the signed receipt.")
        publish_local(readback, Path(folder) / sidecar.name)


def publish_local(staged, destination):
    try:
        os.link(staged, destination)
    except FileExistsError:
        if destination.is_symlink() or destination.read_bytes() != staged.read_bytes():
            raise RecoveryError("A conflicting local recovery receipt already exists.")


def verify_directory(directory, point, trust_file):
    """Verify signed lineage and ciphertext fully offline before creating a usable receipt."""
    directory = outside_checkout(directory)
    point = validate_point(point)
    receipt = verify_sidecar(
        directory / (point + SIDECAR), point, load_small(trust_file)
    )
    verify_file(directory / (point + ".age"), receipt)
    with tempfile.TemporaryDirectory(dir=directory) as tmp:
        staged = Path(tmp) / "verified.json"
        staged.write_bytes(encoded(receipt))
        publish_local(staged, directory / (point + ".json"))
    return receipt


def download_signed(route, output, point, trust_file):
    point = validate_point(point)
    output = outside_checkout(output)
    if output.exists():
        raise RecoveryError("Choose a new directory for the cloud recovery download.")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output.parent) as tmp:
        scratch = Path(tmp)
        directory = scratch / "verified"
        directory.mkdir(mode=0o700)
        with transport(route, scratch) as (prefix, transfer):
            sidecar = directory / (point + SIDECAR)
            transfer(prefix + sidecar.name, sidecar)
            verify_sidecar(sidecar, point, load_small(trust_file))
            transfer(prefix + point + ".age", directory / (point + ".age"))
        verify_directory(directory, point, trust_file)
        directory.rename(output)


def latest_point(route, scratch):
    config = scratch / "list.conf"
    shutil.copyfile(route.snapshot, config)
    result = subprocess.run(
        [
            rclone_program(),
            "--config",
            config,
            "--ask-password=false",
            "--password-command",
            route.password_command,
            "lsf",
            "--files-only",
            "--max-depth",
            "1",
            "--include",
            "*" + SIDECAR,
            route.base,
        ],
        env=child_env(),
        capture_output=True,
        stdin=subprocess.DEVNULL,
        timeout=180,
    )
    if result.returncode:
        raise RecoveryError("Could not list signed recovery points in the provider.")
    points = [
        name.removesuffix(SIDECAR)
        for name in result.stdout.decode("utf-8").splitlines()
        if name.endswith(SIDECAR) and POINT.fullmatch(name.removesuffix(SIDECAR))
    ]
    if not points:
        raise RecoveryError("No signed recovery points are available in the provider.")
    return max(points)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    actions = parser.add_subparsers(dest="action", required=True)
    verify = actions.add_parser("verify")
    verify.add_argument("--directory", required=True, type=Path)
    verify.add_argument("--point", required=True)
    verify.add_argument("--trust", required=True, type=Path)
    download = actions.add_parser("download")
    download.add_argument("--output", required=True, type=Path)
    download.add_argument("--transport", required=True, type=Path)
    download.add_argument("--trust", required=True, type=Path)
    download.add_argument(
        "--point", help="Defaults to the newest signed point listed in the provider."
    )
    rotation = actions.add_parser("rotate-trust")
    rotation.add_argument("--trust", required=True, type=Path)
    rotation.add_argument("--new-pin", required=True, type=Path)
    rotation.add_argument(
        "--cutover",
        required=True,
        help="First UTC capture second using the incoming key, YYYYMMDDTHHMMSSZ.",
    )
    rotation.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        if args.action == "rotate-trust":
            rotate_trust(args.trust, args.new_pin, args.cutover, args.output)
            print("Prepared independently pinned receipt trust history.")
            return
        point = args.point
        if args.action == "verify":
            verify_directory(args.directory, point, args.trust)
        else:
            with tempfile.TemporaryDirectory() as tmp:
                scratch = Path(tmp)
                route = capture_transport(args.transport, scratch)
                point = point or latest_point(route, scratch)
                download_signed(route, args.output, point, args.trust)
        print("Authenticated provider recovery point " + point)
    except (
        OSError,
        ValueError,
        KeyError,
        TypeError,
        AttributeError,
        RecoveryError,
        subprocess.SubprocessError,
    ) as error:
        parser.exit(1, f"Cloud receipt {args.action} failed: {failure_reason(error)}\n")


if __name__ == "__main__":
    main()
