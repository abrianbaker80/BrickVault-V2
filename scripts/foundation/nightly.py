"""Controller-operated private guest capture, verified publication and freshness status."""

import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import uuid
from zoneinfo import ZoneInfo

from backup_mirror import export_point
from instance import Instance, write_private
from offhost import (
    FORMAT,
    HEX,
    RecoveryError,
    capture_transport,
    failure_reason,
    make_set,
    managed_dir,
    outside_checkout,
    point_time,
    publish_verified_copy,
    read_receipt,
    transport,
    validate_point,
    verify_file,
)
from runtime import exclusive


def utcnow():
    return datetime.now(timezone.utc)


def stream(state):
    """Forced SSH command. Capture one portable point and return only receipt and ciphertext."""
    instance = Instance(state)
    point = utcnow().strftime("%Y%m%dT%H%M%SZ-") + uuid.uuid4().hex
    with tempfile.TemporaryDirectory(dir=instance.path) as temp:
        directory = Path(temp)
        make_set(instance, directory, point, None, None)
        receipt = read_receipt(directory / (point + ".pending.json"), point)
        output = sys.stdout.buffer
        output.write(json.dumps(receipt, separators=(",", ":")).encode("ascii") + b"\n")
        with (directory / (point + ".age")).open("rb") as archive:
            shutil.copyfileobj(archive, output)
        output.flush()


def guest_address(settings):
    # Use the explicitly selected VM and interface. Pin the SSH host key independently of DHCP.
    vmid = settings["vmId"]
    if not isinstance(vmid, int) or not 100 <= vmid <= 999999999:
        raise RecoveryError("Invalid guest VM ID.")
    network = ipaddress.IPv4Network(settings["privateNetwork"])
    if not network.is_private or network.prefixlen == 0:
        raise RecoveryError("Use the authorized private guest network.")
    interfaces = subprocess.run(
        ["qm", "guest", "cmd", str(vmid), "network-get-interfaces"],
        check=True,
        capture_output=True,
        timeout=35,
    )
    addresses = []
    for interface in json.loads(interfaces.stdout):
        if (
            interface.get("hardware-address", "").lower()
            != settings["guestMac"].lower()
        ):
            continue
        for entry in interface.get("ip-addresses", []):
            if entry["ip-address-type"] == "ipv4":
                addr = ipaddress.IPv4Address(entry["ip-address"])
                if addr in network and not addr.is_loopback:
                    addresses.append(str(addr))
    if len(addresses) != 1:
        raise RecoveryError("Guest interface has no unique authorized private address.")
    return addresses[0]


def capture_command(settings):
    host_alias = settings["hostKeyAlias"]
    account = settings["guestAccount"]
    if not re.fullmatch(r"[A-Za-z0-9.-]+", host_alias) or not re.fullmatch(
        r"[a-z_][a-z0-9_-]*", account
    ):
        raise RecoveryError("Invalid guest identity configuration.")
    return [
        "ssh",
        "-F",
        "/dev/null",
        "-T",
        "-o",
        "BatchMode=yes",
        "-o",
        "IdentitiesOnly=yes",
        "-o",
        "StrictHostKeyChecking=yes",
        "-o",
        "UpdateHostKeys=no",
        "-o",
        "GlobalKnownHostsFile=/dev/null",
        "-o",
        "HostKeyAlias=" + host_alias,
        "-o",
        "UserKnownHostsFile=" + settings["knownHosts"],
        "-o",
        "ConnectTimeout=15",
        "-o",
        "ClearAllForwardings=yes",
        "-o",
        "ServerAliveInterval=20",
        "-o",
        "ServerAliveCountMax=3",
        "-i",
        settings["guestKey"],
        account + "@" + guest_address(settings),
        "capture",
    ]


def binding(settings, route):
    values = [
        str(uuid.UUID(settings["sourceId"])),
        str(uuid.UUID(settings["workspaceId"])),
        settings["hostKeyAlias"],
        route.base,
        str(route.config),
        route.config_sha256,
    ]
    return hashlib.sha256(json.dumps(values).encode("ascii")).hexdigest()


def receive(stream_input, folder, settings, context):
    data = stream_input.readline(8193)
    if len(data) > 8192 or not data.endswith(b"\n"):
        raise RecoveryError("Guest did not produce a complete capture header.")
    payload = json.loads(data)
    point = validate_point(payload.get("point"))
    capture_age = utcnow() - point_time(point)
    if not -timedelta(minutes=5) <= capture_age <= timedelta(hours=2):
        raise RecoveryError(
            "Guest capture timestamp is outside the allowed two-hour window. Check guest and controller clocks."
        )
    size = payload.get("bytes")
    if (
        payload.get("format") != FORMAT
        or type(size) is not int
        or size <= 0
        or size > settings["maxBytes"]
        or not HEX.fullmatch(str(payload.get("sha256", "")))
        or uuid.UUID(payload["sourceInstance"]) != uuid.UUID(settings["sourceId"])
        or uuid.UUID(payload["workspaceId"]) != uuid.UUID(settings["workspaceId"])
    ):
        raise RecoveryError(
            "Captured point has an unexpected source identity, size or format."
        )
    reserve = int(settings.get("reserveBytes", 1024**3))
    if shutil.disk_usage(folder).free < reserve + 2 * size:
        raise RecoveryError(
            "Insufficient controller space for a capture and verified download."
        )
    destination = folder / (point + ".age")
    remaining = size
    with destination.open("xb") as out:
        while remaining:
            chunk = stream_input.read(min(1024 * 1024, remaining))
            if not chunk:
                raise RecoveryError("Guest capture ended early.")
            out.write(chunk)
            remaining -= len(chunk)
        out.flush()
        os.fsync(out.fileno())
    if stream_input.read(1):
        raise RecoveryError("Guest supplied extra bytes after its capture.")
    # Construct the manifest explicitly; the app guest cannot choose controller metadata or destinations.
    receipt = {
        "format": FORMAT,
        "point": point,
        "bytes": size,
        "sha256": payload["sha256"],
        "sourceInstance": settings["sourceId"],
        "workspaceId": settings["workspaceId"],
        "publicationContext": context,
    }
    verify_file(destination, receipt)
    write_private(folder / "pending.json", json.dumps(receipt, sort_keys=True))
    return point


def capture(points, settings, context):
    with tempfile.TemporaryDirectory(dir=points) as tmp:
        # Promote the inner directory; TemporaryDirectory owns and removes only the outer scratch.
        stage = Path(tmp) / "capture"
        stage.mkdir(mode=0o700)
        with subprocess.Popen(
            capture_command(settings),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
        ) as proc:
            try:
                point = receive(proc.stdout, stage, settings, context)
                if proc.wait(timeout=60):
                    raise RecoveryError("Guest capture command failed.")
            finally:
                if proc.poll() is None:
                    proc.kill()
        stage.rename(points / point)
    return point


def status_data(directory):
    file = directory / "status.json"
    return json.loads(file.read_text(encoding="utf-8")) if file.exists() else {}


def update_status(directory, status, **changes):
    status.update(changes)
    write_private(
        directory / "status.json", json.dumps(status, indent=2, sort_keys=True)
    )


def captured_day(point):
    return point_time(point).astimezone(ZoneInfo("America/Chicago")).date()


def same_day(point):
    return (
        bool(point)
        and captured_day(point)
        == utcnow().astimezone(ZoneInfo("America/Chicago")).date()
    )


def publish_point(points, point, route, context, export):
    folder = points / point
    receipt = read_receipt(folder / "pending.json", point)
    if receipt.get("publicationContext") != context:
        raise RecoveryError(
            "Pending capture belongs to a different source or destination."
        )
    local = folder / (point + ".age")
    verify_file(local, receipt)
    publish_verified_copy(route, folder, local, receipt, None)
    receipt["verifiedAt"] = utcnow().isoformat()
    write_private(folder / (point + ".json"), json.dumps(receipt, sort_keys=True))
    if export:
        export_point(local, receipt, export)
    return receipt


def run(settings, directory, *, before_change=False):
    points = directory / "points"
    points.mkdir(mode=0o700, exist_ok=True)
    with exclusive(directory / "capture.lock"):
        status = status_data(directory)
        update_status(
            directory,
            status,
            state="running",
            activity="load-transport",
            lastError=None,
            attemptedAt=utcnow().isoformat(),
        )
        try:
            with tempfile.TemporaryDirectory(dir=directory) as scratch:
                route = capture_transport(Path(settings["transport"]), Path(scratch))
                context = binding(settings, route)
                update_status(directory, status, activity="retry-pending")
                for pending in sorted(points.glob("*/pending.json")):
                    point = validate_point(pending.parent.name)
                    if (
                        read_receipt(pending, point).get("publicationContext")
                        != context
                    ):
                        continue  # Retain earlier ciphertext without publishing it to a changed destination.
                    receipt = publish_point(
                        points, point, route, context, settings.get("mirrorExport")
                    )
                    update_status(
                        directory,
                        status,
                        lastPoint=point,
                        verifiedAt=receipt["verifiedAt"],
                        verifiedContext=context,
                    )
                    (points / point / "pending.json").unlink()
                if (
                    before_change
                    or not same_day(status.get("lastPoint"))
                    or status.get("verifiedContext") != context
                ):
                    update_status(directory, status, activity="capture-guest")
                    point = capture(points, settings, context)
                    update_status(directory, status, activity="verify-provider")
                    receipt = publish_point(
                        points, point, route, context, settings.get("mirrorExport")
                    )
                    update_status(
                        directory,
                        status,
                        lastPoint=point,
                        verifiedAt=receipt["verifiedAt"],
                        verifiedContext=context,
                    )
                    (points / point / "pending.json").unlink()
                pending_count = sum(1 for _ in points.glob("*/pending.json"))
                update_status(
                    directory,
                    status,
                    state="verified",
                    activity="complete",
                    retainedPending=pending_count,
                )
        except Exception as exc:
            update_status(
                directory,
                status,
                state="failed",
                lastError=failure_reason(exc),
                retainedPending=sum(1 for _ in points.glob("*/pending.json")),
            )
            raise
    print("Verified nightly encrypted backup " + status["lastPoint"])


def health(directory, max_age):
    status = status_data(directory)
    verified = status.get("verifiedAt")
    fresh = False
    if verified and status.get("lastPoint"):
        elapsed = utcnow() - point_time(status["lastPoint"])
        fresh = timedelta(0) <= elapsed <= timedelta(hours=max_age)
    print(
        json.dumps(
            {
                key: status.get(key)
                for key in (
                    "state",
                    "activity",
                    "lastPoint",
                    "verifiedAt",
                    "retainedPending",
                    "lastError",
                )
            }
            | {"fresh": fresh}
        )
    )
    if not fresh or status.get("state") == "failed":
        raise RecoveryError("A fresh verified nightly backup is required.")


def download(settings, directory, output, point):
    point = validate_point(point or status_data(directory)["lastPoint"])
    folder = directory / "points" / point
    receipt = read_receipt(folder / (point + ".json"), point)
    provider_root = outside_checkout(output)
    if provider_root.exists():
        raise RecoveryError("Download requires a fresh output directory.")
    provider_root.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=provider_root.parent) as temp:
        scratch = Path(temp)
        recovered = scratch / "download"
        recovered.mkdir(mode=0o700)
        with transport(
            capture_transport(Path(settings["transport"]), scratch), scratch
        ) as (base, transfer):
            archive = recovered / (point + ".age")
            transfer(base + point + ".age", archive)
        verify_file(archive, receipt)
        write_private(
            recovered / (point + ".json"), json.dumps(receipt, sort_keys=True)
        )
        recovered.rename(provider_root)
    print("Verified fresh provider download " + point)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    guest = sub.add_parser("stream")
    guest.add_argument("--state", type=Path, required=True)
    for name in ("run", "status", "download"):
        command = sub.add_parser(name)
        command.add_argument("--config", required=True, type=Path)
        if name == "run":
            command.add_argument(
                "--before-change",
                action="store_true",
                help="Immediately capture a new verified point, even if today already has one.",
            )
        if name == "download":
            command.add_argument("--output", required=True, type=Path)
            command.add_argument("--point")
    args = parser.parse_args()
    try:
        if args.command == "stream":
            stream(args.state)
            return
        settings = json.loads(outside_checkout(args.config).read_text(encoding="utf-8"))
        directory = outside_checkout(settings["controllerState"])
        if args.command == "run":
            directory = managed_dir(directory, "nightly-controller")
        elif (directory / ".brickvault-v2-offhost").read_text() != "nightly-controller":
            raise RecoveryError("Expected a managed V2 controller directory.")
        if args.command == "run":
            run(settings, directory, before_change=args.before_change)
        elif args.command == "status":
            health(directory, settings.get("freshnessHours", 30))
        else:
            download(settings, directory, args.output, args.point)
    except (
        OSError,
        ValueError,
        RuntimeError,
        RecoveryError,
        subprocess.SubprocessError,
        KeyError,
    ) as exc:
        parser.exit(1, f"Nightly {args.command} failed: {failure_reason(exc)}\n")


if __name__ == "__main__":
    main()
