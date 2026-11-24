"""Exercise signed cloud receipt publication and offline recovery with separate trust custody."""

from contextlib import contextmanager, redirect_stdout
from datetime import datetime, timezone
import hashlib
import base64
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid

import cloud_receipt as cloud
import backup_mirror
import onedrive_reader
from instance import NO_WINDOW
from offhost import FORMAT, RecoveryError


class CloudReceipts(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.root = Path(scratch.name)
        self.controller = self.root / "controller"
        self.controller.mkdir()
        self.provider = self.root / "provider"
        self.provider.mkdir()
        self.key = self.controller / "signing"
        self.pub = self.keygen(self.key)
        self.trust = self.root / "independent-trust.json"
        self.source, self.workspace = (str(uuid.uuid4()) for _ in range(2))
        self.pin(self.pub)
        self.point = "20250518T010203Z-" + uuid.uuid4().hex
        content = b"synthetic ciphertext for an isolated qualification"
        self.archive = self.provider / (self.point + ".age")
        self.archive.write_bytes(content)
        self.receipt = {
            "format": FORMAT,
            "point": self.point,
            "bytes": len(content),
            "sha256": hashlib.sha256(content).hexdigest(),
            "sourceInstance": self.source,
            "workspaceId": self.workspace,
            "publicationContext": "1" * 64,
        }
        self.output = self.root / "recovered"

        @contextmanager
        def transport(route, scratch):
            def transfer(source, destination):
                def local(value):
                    return (
                        (self.provider / value.removeprefix("sample:V2/"))
                        if isinstance(value, str)
                        else value
                    )

                source, destination = local(source), local(destination)
                if (
                    destination.exists()
                    and destination.read_bytes() != source.read_bytes()
                ):
                    raise RecoveryError("Immutable object already exists.")
                shutil.copyfile(source, destination)

            yield "sample:V2/", transfer

        mock = patch.object(cloud, "transport", transport)
        mock.start()
        self.addCleanup(mock.stop)

    def keygen(self, path):
        subprocess.run(
            ["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", path],
            check=True, creationflags=NO_WINDOW,
        )
        return " ".join(path.with_suffix(".pub").read_text().split()[:2])

    def pin(self, public, **changes):
        self.trust.write_text(
            json.dumps(
                {
                    "publicKey": public,
                    "sourceId": self.source,
                    "workspaceId": self.workspace,
                }
                | changes
            )
        )

    def publish(self, receipt=None, key=None):
        cloud.publish_signed(
            None, self.controller, receipt or self.receipt, key or self.key, self.trust
        )

    def download(self):
        cloud.download_signed(None, self.output, self.point, self.trust)

    def reader_profile(self):
        profile = self.root / "reader"
        profile.mkdir()
        (profile / "reader.conf").write_bytes(b"RCLONE_ENCRYPT_V0:\nfixture")
        (profile / ".brickvault-v2-offhost").write_text("onedrive-reader")
        (profile / "authorization.json").write_text(
            json.dumps({"state": "ready", "verifiedAt": "enrollment"})
        )
        return profile

    @unittest.skipUnless(os.name == "nt", "Windows windowless DPAPI credential pipe")
    def test_windowless_password_pipe(self):
        secret = self.root / "sample password.dpapi"
        args = onedrive_reader.password_command(secret)
        subprocess.run([*args, "-Initialize"], check=True, creationflags=NO_WINDOW)
        expected = subprocess.run(args, capture_output=True, check=True, creationflags=NO_WINDOW).stdout
        with (
            patch.object(sys, "argv", ["onedrive_reader.py", "reader-password", "--protected-file", str(secret)]),
            patch.object(subprocess, "run") as launch,
        ):
            onedrive_reader.main()
            self.assertEqual(launch.call_args.args[0], args)
            self.assertEqual(launch.call_args.kwargs["creationflags"], subprocess.CREATE_NO_WINDOW)
            self.assertIsNone(launch.call_args.kwargs.get("stdout"))
        base = onedrive_reader.command(self.root, "rclone")
        windowless = Path(sys.executable).with_name("pythonw.exe")
        cli = [windowless.as_posix(), "-E", "-s", Path(onedrive_reader.__file__).resolve().as_posix(),
               "reader-password", "--protected-file", (self.root / "reader.dpapi").as_posix()]
        self.assertEqual(base[base.index("--password-command") + 1], subprocess.list2cmdline(cli))
        secret.rename(self.root / "reader.dpapi")
        # Capture the same stdout pipe that rclone supplies to its GUI-subsystem child.
        actual = subprocess.run(cli, capture_output=True, check=True, creationflags=NO_WINDOW).stdout
        self.assertEqual(len(base64.b64decode(actual)), 48)
        self.assertTrue(actual == expected, "The credential pipe changed the generated password bytes.")

    def test_onedrive_signed_mirror_and_independent_download(self):
        self.publish()
        point = self.point
        raw = (self.provider / (point + cloud.SIDECAR)).read_bytes()
        mirror = self.root / "mirror"
        mirror.mkdir()
        encoded = base64.b64encode(raw).decode()
        trust = cloud.load_small(self.trust)
        signed = self.receipt | {"signedReceipt": encoded}
        state = self.root / "scratch"
        state.mkdir()
        valid_bytes = self.archive.read_bytes()
        (mirror / (point + ".age")).write_bytes(valid_bytes)
        backup_mirror.sync_point([], mirror, state, signed, trust)
        self.assertEqual((mirror / (point + cloud.SIDECAR)).read_bytes(), raw)
        with self.assertRaises(RecoveryError):
            backup_mirror.sync_point([], mirror, state, signed | {"bytes": 1}, trust)
        profile = self.reader_profile()
        calls = []
        original_run = subprocess.run

        def provider(command, **kwargs):
            if "listremotes" in command:
                self.assertIn("onedrive", command)
                return SimpleNamespace(returncode=0, stdout=b"bv2onedrive:\n")
            if "copyto" in command:
                src, dst = command[-2:]
                self.assertTrue(src.startswith("bv2onedrive:BrickVault V2 Backup/"))
                name = src.split("/")[-1]
                calls.append(name)
                remote = self.provider / name
                if not remote.exists():
                    return SimpleNamespace(returncode=3)
                shutil.copyfile(remote, dst)
                return SimpleNamespace(returncode=0)
            return original_run(command, **kwargs)

        with patch.object(
            onedrive_reader, "rclone_program", return_value="fixture"
        ), patch.object(subprocess, "run", side_effect=provider):
            config = {"oneDriveProfile": profile, "receiptTrust": self.trust}
            state = self.root / "state"
            state.mkdir()
            status = {}
            backup_mirror.verify_onedrive(config, state, [self.receipt], status)
            self.assertTrue(status["cloudSyncVerified"])
            self.assertEqual(status["cloudPoint"], point)
            self.assertEqual(calls, [point + cloud.SIDECAR, point + ".age"])
            (mirror / (point + ".age")).write_bytes(self.archive.read_bytes())
            self.archive.write_bytes(b"corrupt remote, healthy local mirror")
            with self.assertRaises(RecoveryError):
                onedrive_reader.download(profile, self.trust, point, self.output)
            self.assertFalse(self.output.exists())
            (self.provider / (point + cloud.SIDECAR)).unlink()
            with self.assertRaises(RecoveryError):
                onedrive_reader.download(profile, self.trust, point, self.output)
            self.assertFalse(self.output.exists())

    def test_unattended_readback_waits_only_for_unavailable_cloud_objects(self):
        latest = (
            datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ-") + uuid.uuid4().hex
        )
        config = {
            "state": self.root / "state",
            "mirror": self.root / "mirror",
            "sshConfig": "fixture",
            "oneDriveProfile": self.reader_profile(),
            "receiptTrust": self.trust,
        }
        content = self.archive.read_bytes()
        listing = []
        for point in (self.point, latest):
            receipt = self.receipt | {"point": point}
            (self.provider / (point + ".age")).write_bytes(content)
            self.publish(receipt)
            signed = (self.controller / (point + cloud.SIDECAR)).read_bytes()
            listing.append(
                cloud.encoded(
                    receipt | {"signedReceipt": base64.b64encode(signed).decode()}
                )
            )
        provider_reads = []
        controller_reads = []
        original_run = subprocess.run
        fault = "delayed"
        different = self.receipt | {"point": latest, "publicationContext": "2" * 64}
        signature = cloud.ssh_keygen(
            ["sign", "-f", self.key, "-n", cloud.NAMESPACE], cloud.encoded(different)
        ).decode()
        other_signed = cloud.encoded({"receipt": different, "signature": signature})

        def transfer(command, **kwargs):
            # Exercise every child of a real catch-up, including directory ACLs,
            # SSH list/get, signature verification and OneDrive list/copy.
            self.assertEqual(kwargs.get("creationflags"), getattr(subprocess, "CREATE_NO_WINDOW", 0))
            if command[0] == "ssh":
                controller_reads.append(command[-1])
                if command[-1] == "list":
                    return SimpleNamespace(stdout=b"".join(listing))
                self.assertIn(
                    command[-1], ["get " + json.loads(row)["point"] for row in listing]
                )
                kwargs["stdout"].write(content)
                return SimpleNamespace(returncode=0)
            if "listremotes" in command:
                return SimpleNamespace(stdout=b"bv2onedrive:\n")
            if "copyto" in command:
                remote, destination = command[-2:]
                name = remote.split("/")[-1]
                provider_reads.append(name)
                if name == latest + cloud.SIDECAR and fault == "missing-sidecar":
                    return SimpleNamespace(returncode=4)
                if name == latest + cloud.SIDECAR and fault == "wrong-signature":
                    envelope = json.loads((self.provider / name).read_text())
                    envelope["receipt"]["publicationContext"] = "2" * 64
                    destination.write_bytes(cloud.encoded(envelope))
                    return SimpleNamespace(returncode=0)
                if name == latest + cloud.SIDECAR and fault == "different-metadata":
                    destination.write_bytes(other_signed)
                    return SimpleNamespace(returncode=0)
                if name == latest + ".age":
                    if fault in ("wrong-signature", "different-metadata"):
                        return SimpleNamespace(returncode=4)
                    if fault == "delayed":
                        return SimpleNamespace(returncode=3)
                    if isinstance(fault, int):
                        return SimpleNamespace(returncode=fault)
                    if fault == "corrupt":
                        destination.write_bytes(b"invalid ciphertext")
                        return SimpleNamespace(returncode=0)
                shutil.copyfile(self.provider / name, destination)
                return SimpleNamespace(returncode=0)
            return original_run(command, **kwargs)

        status_file = config["state"] / "status.json"

        def wait_for_upload(seconds):
            nonlocal fault
            waiting = json.loads(status_file.read_text())
            self.assertEqual(waiting["state"], "waiting-onedrive")
            self.assertEqual(waiting["pendingReadbacks"], 1)
            self.assertTrue(waiting["nextReadbackAt"])
            with self.assertRaises(RecoveryError):
                backup_mirror.health(config)
            if fault == "delayed":
                self.assertEqual(waiting["cloudPoint"], self.point)
                self.assertEqual(waiting["point"], latest)
                fault = None

        with (
            patch.object(onedrive_reader, "rclone_program", return_value="fixture"),
            patch.object(subprocess, "run", side_effect=transfer),
            patch.object(backup_mirror, "READBACK_DELAYS", (1, 2)),
            patch.object(backup_mirror, "sleep", side_effect=wait_for_upload) as sleep,
            redirect_stdout(io.StringIO()),
        ):
            backup_mirror.catch_up(config)
            backup_mirror.health(config)
            sleep.assert_called_once_with(1)
            self.assertEqual(
                controller_reads, ["list", "get " + self.point, "get " + latest]
            )
            self.assertEqual(provider_reads.count(self.point + ".age"), 1)
            self.assertEqual(provider_reads.count(latest + ".age"), 2)
            verified = json.loads(status_file.read_text())
            self.assertEqual(verified["state"], "verified-onedrive")
            self.assertEqual(verified["cloudPoint"], latest)
            self.assertEqual(verified["pendingReadbacks"], 0)
            proof = config["state"] / "onedrive-readbacks" / (latest + ".json")
            saved_proof = proof.read_bytes()
            for fault, waits in (
                (3, 2),
                (4, 2),
                (5, 2),
                (7, 0),
                ("corrupt", 0),
                ("wrong-signature", 0),
                ("different-metadata", 0),
                ("missing-sidecar", 2),
            ):
                with self.subTest(fault=fault):
                    sleep.reset_mock()
                    provider_reads.clear()
                    controller_reads.clear()
                    with self.assertRaises(RecoveryError):
                        backup_mirror.catch_up(config)
                    with self.assertRaises(RecoveryError):
                        backup_mirror.health(config)
                    failed = json.loads(status_file.read_text())
                    self.assertEqual(failed["state"], "failed")
                    self.assertFalse(failed["cloudSyncVerified"])
                    self.assertIsNone(failed["nextReadbackAt"])
                    self.assertEqual(
                        failed["cloudVerifiedAt"], verified["cloudVerifiedAt"]
                    )
                    self.assertEqual(failed["cloudPoint"], latest)
                    self.assertEqual(proof.read_bytes(), saved_proof)
                    self.assertEqual(sleep.call_count, waits)
                    self.assertEqual(controller_reads, ["list"])
                    self.assertNotIn(self.point + ".age", provider_reads)
                    no_archive = fault in (
                        "missing-sidecar",
                        "wrong-signature",
                        "different-metadata",
                    )
                    expected_archives = 0 if no_archive else waits + 1
                    self.assertEqual(
                        provider_reads.count(latest + ".age"), expected_archives
                    )

            # An older subset cannot replace the cloud high-water mark, including on upgrade.
            fault = None
            saved_latest = listing.pop()
            saved_older = listing[0]
            # Actual pre-upgrade shape after the very first owner copy and a pending cloud upload.
            pending_state = backup_mirror.managed_dir(
                self.root / "pending-state", "nightly-mirror-state"
            )
            pending_config = config | {"state": pending_state}
            pending_file = pending_state / "status.json"
            baseline = {
                "state": "failed",
                "attemptedAt": verified["attemptedAt"],
                "lastError": "Provider file unavailable.",
                "point": latest,
                "verifiedAt": verified["verifiedAt"],
                "copied": 1,
                "cloudSyncVerified": False,
            }
            pending_file.write_bytes(cloud.encoded(baseline))
            provider_reads.clear()
            for _ in range(2):
                with self.assertRaisesRegex(RecoveryError, "ends before"):
                    backup_mirror.catch_up(pending_config)
                upgraded = json.loads(pending_file.read_text())
                self.assertEqual(upgraded["point"], latest)
                self.assertEqual(upgraded["verifiedAt"], baseline["verifiedAt"])
                self.assertNotIn("cloudPoint", upgraded)
                self.assertNotIn("cloudVerifiedAt", upgraded)
                self.assertFalse(upgraded["cloudSyncVerified"])
                self.assertEqual(
                    next(iter(upgraded["cloudMarks"].values())),
                    {"controllerPoint": latest},
                )
            self.assertEqual(provider_reads, [])

            # A legacy unsigned pair needs its controller's authenticated offer to establish lineage.
            pending_file.write_bytes(cloud.encoded(baseline))
            local_archive = config["mirror"] / (latest + ".age")
            local_archive.write_bytes(b"wrong local ciphertext")
            with self.assertRaisesRegex(RecoveryError, "fresh workstation state"):
                backup_mirror.catch_up(pending_config)
            self.assertNotIn("cloudMarks", json.loads(pending_file.read_text()))
            local_archive.write_bytes(content)
            (config["mirror"] / (latest + cloud.SIDECAR)).unlink()
            with self.assertRaisesRegex(RecoveryError, "fresh workstation state"):
                backup_mirror.catch_up(pending_config)
            unsigned = json.loads(pending_file.read_text())
            self.assertNotIn("cloudMarks", unsigned)
            self.assertEqual(unsigned["point"], latest)
            self.assertEqual(provider_reads, [])
            listing[:] = [saved_latest]
            fault = 3
            with patch.object(backup_mirror, "READBACK_DELAYS", ()):
                with self.assertRaisesRegex(RecoveryError, "still unavailable"):
                    backup_mirror.catch_up(pending_config)
            awaiting = json.loads(pending_file.read_text())
            self.assertEqual(
                next(iter(awaiting["cloudMarks"].values())), {"controllerPoint": latest}
            )
            self.assertNotIn("cloudPoint", awaiting)
            self.assertFalse(awaiting["cloudSyncVerified"])
            fault = None
            listing[:] = [saved_older]
            legacy_proof = json.loads(saved_proof)
            legacy_proof.pop("lineage")
            proof.write_bytes(cloud.encoded(legacy_proof))
            legacy_status = json.loads(status_file.read_text())
            legacy_status.pop("cloudScope")
            legacy_status.pop("cloudMarks")
            status_file.write_bytes(cloud.encoded(legacy_status))
            provider_reads.clear()
            proof.write_bytes(cloud.encoded(legacy_proof | {"sha256": "0" * 64}))
            with self.assertRaisesRegex(RecoveryError, "readback proof conflicts"):
                backup_mirror.catch_up(config)
            self.assertNotIn("cloudMarks", json.loads(status_file.read_text()))
            self.assertEqual(provider_reads, [])
            proof.write_bytes(cloud.encoded(legacy_proof))
            with self.assertRaisesRegex(RecoveryError, "ends before"):
                backup_mirror.catch_up(config)
            truncated = json.loads(status_file.read_text())
            self.assertFalse(truncated["cloudSyncVerified"])
            self.assertEqual(truncated["cloudPoint"], latest)
            self.assertEqual(truncated["cloudVerifiedAt"], verified["cloudVerifiedAt"])
            self.assertEqual(provider_reads, [])
            invalid_latest = json.loads(saved_latest)
            invalid_latest.pop("signedReceipt")
            listing.append(cloud.encoded(invalid_latest))
            with self.assertRaises(RecoveryError):
                backup_mirror.catch_up(config)
            partial = json.loads(status_file.read_text())
            self.assertEqual(partial["cloudPoint"], latest)
            self.assertEqual(partial["cloudVerifiedAt"], verified["cloudVerifiedAt"])
            self.assertFalse(partial["cloudSyncVerified"])

            # Pruning historical exports still permits a fresh verification of the newest point.
            listing[:] = [saved_latest]
            backup_mirror.catch_up(config)
            backup_mirror.health(config)
            listing[:] = [saved_older, saved_latest, saved_older]
            with self.assertRaisesRegex(RecoveryError, "duplicate recovery point"):
                backup_mirror.catch_up(config)
            duplicated = json.loads(status_file.read_text())
            self.assertEqual(duplicated["cloudPoint"], latest)
            self.assertFalse(duplicated["cloudSyncVerified"])

            # A later enrollment must authenticate provider bytes before acquiring its own mark.
            listing[:] = [saved_older]
            enrollment = config["oneDriveProfile"] / "authorization.json"
            enrollment.write_text(
                json.dumps({"state": "ready", "verifiedAt": "new-enrollment"})
            )
            with patch.object(
                onedrive_reader, "download", side_effect=RecoveryError("No access.")
            ):
                with self.assertRaises(RecoveryError):
                    backup_mirror.catch_up(config)
            unavailable = json.loads(status_file.read_text())
            self.assertEqual(unavailable["cloudScope"], duplicated["cloudScope"])
            self.assertEqual(
                unavailable["cloudVerifiedAt"], duplicated["cloudVerifiedAt"]
            )
            provider_reads.clear()
            backup_mirror.catch_up(config)
            reenrolled = json.loads(status_file.read_text())
            self.assertEqual(reenrolled["cloudPoint"], self.point)
            self.assertNotEqual(reenrolled["cloudScope"], duplicated["cloudScope"])
            self.assertEqual(
                provider_reads, [self.point + cloud.SIDECAR, self.point + ".age"]
            )
            enrollment.write_text(
                json.dumps({"state": "ready", "verifiedAt": "enrollment"})
            )
            with self.assertRaisesRegex(RecoveryError, "ends before"):
                backup_mirror.catch_up(config)
            restored = json.loads(status_file.read_text())
            self.assertEqual(restored["cloudPoint"], latest)
            self.assertEqual(restored["cloudVerifiedAt"], duplicated["cloudVerifiedAt"])
            other_source = str(uuid.uuid4())
            self.pin(self.pub, sourceId=other_source)
            other_point = "20250519T010203Z-" + uuid.uuid4().hex
            other_receipt = self.receipt | {
                "point": other_point,
                "sourceInstance": other_source,
            }
            (self.provider / (other_point + ".age")).write_bytes(content)
            self.publish(other_receipt)
            other_sidecar = (
                self.controller / (other_point + cloud.SIDECAR)
            ).read_bytes()
            listing[:] = [
                cloud.encoded(
                    other_receipt
                    | {"signedReceipt": base64.b64encode(other_sidecar).decode()}
                )
            ]
            # The same reader enrollment cannot assign an unscoped old proof to a new commissioned lineage.
            foreign_state = backup_mirror.managed_dir(
                self.root / "old-source-state", "nightly-mirror-state"
            )
            foreign_proofs = foreign_state / "onedrive-readbacks"
            foreign_proofs.mkdir()
            prior_proof = foreign_proofs / (latest + ".json")
            prior_proof.write_bytes(cloud.encoded(legacy_proof))
            foreign_file = foreign_state / "status.json"
            foreign_file.write_bytes(cloud.encoded(legacy_status))
            provider_reads.clear()
            for _ in range(2):
                with self.assertRaisesRegex(RecoveryError, "fresh workstation state"):
                    backup_mirror.catch_up(config | {"state": foreign_state})
                rejected = json.loads(foreign_file.read_text())
                self.assertNotIn("cloudScope", rejected)
                self.assertNotIn("cloudMarks", rejected)
                self.assertEqual(rejected["cloudPoint"], latest)
                self.assertFalse(rejected["cloudSyncVerified"])
            self.assertEqual(prior_proof.read_bytes(), cloud.encoded(legacy_proof))
            self.assertEqual(provider_reads, [])
            fresh = config | {"state": self.root / "new-source-state"}
            backup_mirror.catch_up(fresh)
            self.assertEqual(
                json.loads((fresh["state"] / "status.json").read_text())["cloudPoint"],
                other_point,
            )
            backup_mirror.catch_up(config)
            recommissioned = json.loads(status_file.read_text())
            self.assertNotEqual(recommissioned["cloudScope"], restored["cloudScope"])
            self.assertEqual(recommissioned["cloudPoint"], other_point)

    def test_each_retried_point_shares_the_remaining_provider_budget(self):
        config = {"oneDriveProfile": self.reader_profile(), "receiptTrust": self.trust}
        receipts = [
            self.receipt
            | {"point": "20250518T01020" + str(i) + "Z-" + uuid.uuid4().hex}
            for i in range(4)
        ]
        clock = 0
        attempted = []

        def read(profile, trust, state, receipt, scope, deadline):
            nonlocal clock
            i = receipts.index(receipt)
            attempted.append((i, deadline))
            if deadline is None and i < 3:
                raise onedrive_reader.ReadbackPending("Not visible.")
            if deadline is not None:
                clock = deadline
            return "verified"

        with (
            patch.object(backup_mirror, "record_readback", side_effect=read),
            patch.object(backup_mirror, "READBACK_WINDOW", 10),
            patch.object(backup_mirror, "READBACK_DELAYS", (1, 2)),
            patch.object(backup_mirror, "monotonic", side_effect=lambda: clock),
            patch.object(onedrive_reader, "monotonic", side_effect=lambda: clock),
            patch.object(backup_mirror, "sleep") as sleep,
        ):
            status = {}
            with self.assertRaisesRegex(RecoveryError, "still unavailable"):
                backup_mirror.verify_onedrive(config, self.root, receipts, status)
            self.assertEqual(
                attempted, [(0, None), (1, None), (2, None), (3, None), (0, 10)]
            )
            self.assertEqual(status["cloudPoint"], receipts[3]["point"])
            self.assertEqual(status["pendingReadbacks"], 2)
            self.assertIsNone(status["nextReadbackAt"])
            sleep.assert_called_once_with(1)

    def test_a_locally_authenticated_point_cannot_disappear_while_awaiting_upload(self):
        config = {"oneDriveProfile": self.reader_profile(), "receiptTrust": self.trust}
        newer = self.receipt | {"point": "20250519T010203Z-" + uuid.uuid4().hex}
        status = {}
        with patch.object(
            backup_mirror, "record_readback", return_value="earlier"
        ) as download:
            backup_mirror.verify_onedrive(config, self.root, [self.receipt], status)
            download.side_effect = [
                "earlier",
                onedrive_reader.ReadbackPending("Upload pending."),
            ]
            with self.assertRaisesRegex(RecoveryError, "still unavailable"):
                backup_mirror.verify_onedrive(
                    config, self.root, [self.receipt, newer], status, allow_retry=False
                )
            download.reset_mock()
            for _ in range(2):
                with self.assertRaisesRegex(RecoveryError, "ends before"):
                    backup_mirror.verify_onedrive(
                        config, self.root, [self.receipt], status
                    )
            self.assertFalse(status["cloudSyncVerified"])
            self.assertEqual(status["cloudPoint"], self.point)
            self.assertEqual(status["cloudVerifiedAt"], "earlier")
            download.assert_not_called()

    def test_provider_commands_share_deadline_and_keep_staging_private(self):
        self.publish()
        profile = self.reader_profile()
        original_run = subprocess.run
        for sidecar_duration, expected_timeouts in ((3, [9, 7, 4]), (7, [9, 7])):
            with self.subTest(sidecar_duration=sidecar_duration):
                clock = 1
                timeouts = []

                def transfer(command, **kwargs):
                    nonlocal clock
                    if "listremotes" in command:
                        timeouts.append(kwargs["timeout"])
                        clock += 2
                        return SimpleNamespace(stdout=b"bv2onedrive:\n")
                    if "copyto" in command:
                        timeouts.append(kwargs["timeout"])
                        name = command[-2].split("/")[-1]
                        shutil.copyfile(self.provider / name, command[-1])
                        clock += sidecar_duration if name.endswith(cloud.SIDECAR) else 1
                        return SimpleNamespace(returncode=0)
                    return original_run(command, **kwargs)

                with (
                    patch.object(
                        onedrive_reader, "monotonic", side_effect=lambda: clock
                    ),
                    patch.object(subprocess, "run", side_effect=transfer),
                ):
                    output = self.root / ("budget-" + str(sidecar_duration))
                    if sidecar_duration == 7:
                        with self.assertRaises(onedrive_reader.ReadbackDeadline):
                            onedrive_reader.download(
                                profile,
                                self.trust,
                                self.point,
                                output,
                                "fixture",
                                deadline=10,
                            )
                        self.assertFalse(output.exists())
                    else:
                        onedrive_reader.download(
                            profile,
                            self.trust,
                            self.point,
                            output,
                            "fixture",
                            deadline=10,
                        )
                        self.assertEqual(
                            cloud.verify_directory(output, self.point, self.trust),
                            self.receipt,
                        )
                    self.assertEqual(timeouts, expected_timeouts)

    def test_readback_deadline_covers_transfer_time_and_suspended_workstation(self):
        config = {"oneDriveProfile": self.reader_profile(), "receiptTrust": self.trust}
        for ticks, waits in (([0, 7201], 0), ([0, 1, 7201], 1)):
            with (
                self.subTest(ticks=ticks),
                patch.object(backup_mirror, "monotonic", side_effect=ticks),
                patch.object(backup_mirror, "sleep") as sleep,
                patch.object(
                    backup_mirror,
                    "record_readback",
                    side_effect=onedrive_reader.ReadbackPending("Upload pending."),
                ) as download,
            ):
                status = {}
                with self.assertRaisesRegex(RecoveryError, "still unavailable"):
                    backup_mirror.verify_onedrive(
                        config, self.root, [self.receipt], status
                    )
                self.assertIsNone(status["nextReadbackAt"])
                self.assertEqual(status["pendingReadbacks"], 1)
                self.assertEqual(download.call_count, 1)
                self.assertEqual(sleep.call_count, waits)

    def test_mirror_does_not_publish_unauthenticated_data(self):
        self.publish()
        raw = (self.provider / (self.point + cloud.SIDECAR)).read_bytes()
        trust = cloud.load_small(self.trust)
        for existing in (True, False):
            for fault in ("missing", "invalid", "substituted", "conflicting"):
                with self.subTest(
                    existing=existing, fault=fault
                ), tempfile.TemporaryDirectory(dir=self.root) as temp:
                    target = Path(temp) / "sync"
                    state = Path(temp) / "scratch"
                    target.mkdir()
                    state.mkdir()
                    archive = target / (self.point + ".age")
                    signed = target / (self.point + cloud.SIDECAR)
                    if existing:
                        archive.write_bytes(b"preserve damaged or older archive")
                        signed.write_bytes(raw)
                    header = self.receipt | {
                        "signedReceipt": base64.b64encode(raw).decode()
                    }
                    if fault == "missing":
                        del header["signedReceipt"]
                    elif fault == "invalid":
                        damaged = json.loads(raw)
                        damaged["signature"] = "invalid"
                        header["signedReceipt"] = base64.b64encode(
                            cloud.encoded(damaged)
                        ).decode()
                    elif fault == "substituted":
                        header["sourceInstance"] = str(uuid.uuid4())
                    else:
                        signed.write_bytes(b"preserve conflicting local sidecar")
                    before = {file.name: file.read_bytes() for file in target.iterdir()}
                    with self.assertRaises(RecoveryError):
                        backup_mirror.sync_point(
                            ["must-never-start-an-ssh-transfer"],
                            target,
                            state,
                            header,
                            trust,
                        )
                    self.assertEqual(
                        {file.name: file.read_bytes() for file in target.iterdir()},
                        before,
                    )

    def test_reader_enrollment_requires_observed_consent_and_atomic_encryption(self):
        profile = self.root / "reader"
        profile.mkdir()
        (profile / "reader.dpapi").write_bytes(b"fixture")
        config = profile / "reader.conf"
        state = profile / "authorization.json"
        saved = b"RCLONE_ENCRYPT_V0:\nprevious credentials"
        config.write_bytes(saved)
        state.write_text(json.dumps({"state": "ready", "verifiedAt": "original"}))

        def encrypt(command, **kwargs):
            stage = Path(command[command.index("--config") + 1])
            self.assertNotEqual(stage, config)
            stage.write_bytes(b"RCLONE_ENCRYPT_V0:\nfresh credentials")
            return SimpleNamespace(returncode=0)

        with patch.object(subprocess, "run", side_effect=encrypt), patch.object(
            subprocess, "Popen"
        ) as procs:
            proc = procs.return_value.__enter__.return_value
            proc.stderr = iter([])
            proc.wait.return_value = 0
            proc.poll.return_value = 0
            with self.assertRaises(RecoveryError):
                onedrive_reader.enroll(profile, "fixture", reauthorize=True)
            self.assertEqual(config.read_bytes(), saved)
            self.assertEqual(json.loads(state.read_text())["verifiedAt"], "original")
        config.write_bytes(b"")
        with patch.object(
            subprocess, "run", side_effect=subprocess.CalledProcessError(1, "fixture")
        ):
            with self.assertRaises(subprocess.CalledProcessError):
                onedrive_reader.enroll(profile, "fixture")
            self.assertEqual(config.read_bytes(), b"")
        with patch.object(subprocess, "run", side_effect=encrypt), patch.object(
            onedrive_reader, "authorize"
        ), patch.object(onedrive_reader, "check_remote"):
            onedrive_reader.enroll(profile, "fixture")
        self.assertEqual(config.read_bytes(), b"RCLONE_ENCRYPT_V0:\nfresh credentials")

    def test_enrollment_rejects_broader_or_non_microsoft_permissions(self):
        start = "http://127.0.0.1:53682/auth?state=fixture"
        with patch.object(
            onedrive_reader.http.client, "HTTPConnection"
        ) as http, patch.object(onedrive_reader.os, "startfile", create=True) as launch:
            response = http.return_value.getresponse.return_value
            response.status = 307
            approved = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?scope=Files.Read%20offline_access"
            response.getheader.return_value = approved
            onedrive_reader.open_authorization(start)
            launch.assert_called_once_with(approved)
            for url in (
                approved + "%20Files.ReadWrite",
                approved.replace("login.microsoftonline.com", "example.com"),
                approved.replace("https:", "http:"),
            ):
                response.getheader.return_value = url
                with self.assertRaises(RecoveryError):
                    onedrive_reader.open_authorization(start)
            self.assertEqual(launch.call_count, 1)

    def test_retry_and_controller_loss(self):
        self.publish()
        self.publish(self.receipt | {"verifiedAt": "a different retry time"})
        shutil.rmtree(self.controller)
        self.download()
        shutil.rmtree(self.provider)
        verified = cloud.verify_directory(self.output, self.point, self.trust)
        self.assertEqual(verified, self.receipt)
        self.assertEqual(
            json.loads((self.output / (self.point + ".json")).read_text()), verified
        )

    def test_wrong_key_and_lineage(self):
        other = self.root / "unrelated"
        other_public = self.keygen(other)
        with self.assertRaises(RecoveryError):
            self.publish(key=other)
        self.assertFalse((self.provider / (self.point + cloud.SIDECAR)).exists())
        self.publish()
        for public, changes in (
            (other_public, {}),
            (self.pub, {"workspaceId": str(uuid.uuid4())}),
            (self.pub, {"sourceId": str(uuid.uuid4())}),
        ):
            with self.subTest(changes=changes), self.assertRaises(RecoveryError):
                self.pin(public, **changes)
                self.download()
            self.assertFalse(self.output.exists())

    def test_rotated_pins_support_retained_catch_up_and_provider_recovery(self):
        self.publish()
        old_pin = cloud.load_small(self.trust)
        original_trust = self.trust.read_bytes()
        new_key = self.root / "next-signing"
        new_pin = old_pin | {"publicKey": self.keygen(new_key)}
        incoming = self.root / "incoming.json"
        incoming.write_bytes(cloud.encoded(new_pin))
        history = self.root / "rotated.json"
        cutoff = "20250518T010204Z"
        cloud.rotate_trust(self.trust, incoming, cutoff, history)
        self.assertEqual(self.trust.read_bytes(), original_trust)

        next_point = cutoff + "-" + uuid.uuid4().hex
        next_receipt = self.receipt | {"point": next_point}
        shutil.copyfile(self.archive, self.provider / (next_point + ".age"))
        cloud.publish_signed(None, self.controller, next_receipt, new_key, history)
        listing = []
        for entry in (self.receipt, next_receipt):
            point = entry["point"]
            output = self.root / ("restore-" + point)
            cloud.download_signed(None, output, point, history)
            self.assertEqual(cloud.verify_directory(output, point, history), entry)
            signed = (self.controller / (point + cloud.SIDECAR)).read_bytes()
            listing.append(entry | {"signedReceipt": base64.b64encode(signed).decode()})
        original_run = subprocess.run

        def controller(command, **kwargs):
            if command[0] != "ssh":
                return original_run(command, **kwargs)
            if command[-1] == "list":
                return SimpleNamespace(
                    stdout=b"".join(cloud.encoded(row) for row in listing)
                )
            point = command[-1].removeprefix("get ")
            self.assertIn(point, (self.point, next_point))
            kwargs["stdout"].write((self.provider / (point + ".age")).read_bytes())
            return SimpleNamespace(returncode=0)

        settings = {
            "state": self.root / "state",
            "mirror": self.root / "mirror",
            "receiptTrust": history,
            "sshConfig": "fixture",
        }
        with patch.object(subprocess, "run", side_effect=controller):
            backup_mirror.catch_up(settings)
        status = json.loads((settings["state"] / "status.json").read_text())
        self.assertEqual(
            (status["state"], status["point"], status["copied"]),
            ("verified-local", next_point, 2),
        )

        forged = self.root / "forged"
        outsider = self.root / "untrusted-key"
        self.keygen(outsider)
        for body, signer in (
            (self.receipt, new_key),
            (next_receipt, self.key),
            (self.receipt, outsider),
            (next_receipt, outsider),
            (self.receipt | {"workspaceId": str(uuid.uuid4())}, self.key),
            (next_receipt | {"sourceInstance": str(uuid.uuid4())}, new_key),
        ):
            signature = cloud.ssh_keygen(
                ["sign", "-f", signer, "-n", cloud.NAMESPACE], cloud.encoded(body)
            )
            forged.write_bytes(
                cloud.encoded({"receipt": body, "signature": signature.decode()})
            )
            with self.subTest(
                point=body["point"], signer=signer.name
            ), self.assertRaises(RecoveryError):
                cloud.verify_sidecar(forged, body["point"], cloud.load_small(history))

    def test_historical_cutovers_are_complete_and_unambiguous(self):
        pin = cloud.load_small(self.trust)
        first = pin | {
            "publicKey": self.keygen(self.root / "first-key"),
            "before": "20250101T000000Z",
        }
        second = pin | {
            "publicKey": self.keygen(self.root / "second-key"),
            "before": "20260101T000000Z",
            "sourceId": uuid.UUID(pin["sourceId"]).hex.upper(),
            "workspaceId": "{" + pin["workspaceId"].upper() + "}",
        }
        history = pin | {"previous": [first, second]}
        self.assertIs(cloud.signing_pin(history, self.point), second)
        for bad in (
            [second, first],
            [first, second | {"before": first["before"]}],
            [first | {"before": "20250230T000000Z"}],
            [first | {"workspaceId": "invalid"}],
            [first | {"publicKey": "ssh-ed25519 AAAA"}],
            [{"before": "20250101T000000Z"}],
            None,
        ):
            with self.subTest(bad=bad), self.assertRaises(RecoveryError):
                cloud.signing_pin(pin | {"previous": bad}, self.point)
        incoming = self.root / "independent-pin.json"
        outgoing = self.root / "combined.json"
        next_pin = pin | {"publicKey": self.keygen(self.root / "next-key")}
        incoming.write_bytes(
            cloud.encoded(next_pin | {"workspaceId": str(uuid.uuid4())})
        )
        with self.assertRaises(RecoveryError):
            cloud.rotate_trust(self.trust, incoming, "20270101T000000Z", outgoing)
        self.assertFalse(outgoing.exists())
        self.trust.write_bytes(cloud.encoded(history))
        incoming.write_bytes(cloud.encoded(next_pin))
        cloud.rotate_trust(self.trust, incoming, "20270101T000000Z", outgoing)
        combined = cloud.load_small(outgoing)
        self.assertEqual(
            combined["previous"], [first, second, pin | {"before": "20270101T000000Z"}]
        )

    def test_reject_mixed_imported_history_before_verification_or_rotation(self):
        retired = cloud.load_small(self.trust) | {"before": "20260101T000000Z"}
        commissioned = cloud.load_small(self.trust) | {
            "publicKey": self.keygen(self.root / "current")
        }
        earlier = commissioned | {
            "before": "20250101T000000Z",
            "publicKey": self.keygen(self.root / "earlier"),
        }
        incoming = self.root / "incoming.json"
        incoming.write_bytes(
            cloud.encoded(commissioned | {"publicKey": self.keygen(self.root / "next")})
        )
        original_incoming = incoming.read_bytes()
        current_point = "20260101T000000Z-" + uuid.uuid4().hex
        sidecar = self.root / "retained.receipt.json"
        for field, receipt_field in (
            ("sourceId", "sourceInstance"),
            ("workspaceId", "workspaceId"),
        ):
            with self.subTest(field=field):
                foreign_id = str(uuid.uuid4())
                body = self.receipt | {receipt_field: foreign_id}
                signed = cloud.ssh_keygen(
                    ["sign", "-f", self.key, "-n", cloud.NAMESPACE], cloud.encoded(body)
                )
                sidecar.write_bytes(
                    cloud.encoded({"receipt": body, "signature": signed.decode()})
                )
                history = commissioned | {
                    "previous": [earlier, retired | {field: foreign_id}]
                }
                self.trust.write_bytes(cloud.encoded(history))
                original_history = self.trust.read_bytes()
                with self.assertRaises(RecoveryError):
                    cloud.verify_sidecar(sidecar, self.point, history)
                with self.assertRaises(RecoveryError):
                    cloud.signing_pin(history, current_point)
                output = self.root / ("rotation-" + field)
                with self.assertRaises(RecoveryError):
                    cloud.rotate_trust(self.trust, incoming, "20270101T000000Z", output)
                self.assertFalse(output.exists())
                output.write_bytes(b"previously commissioned trust")
                with self.assertRaises(RecoveryError):
                    cloud.rotate_trust(self.trust, incoming, "20270101T000000Z", output)
                self.assertEqual(output.read_bytes(), b"previously commissioned trust")
                self.assertEqual(self.trust.read_bytes(), original_history)
                self.assertEqual(incoming.read_bytes(), original_incoming)

    def test_each_signing_period_requires_distinct_key_material(self):
        current = cloud.load_small(self.trust)
        retired = current | {"publicKey": self.keygen(self.root / "retired")}
        fresh = current | {"publicKey": self.keygen(self.root / "fresh")}
        historical = retired | {"before": "20250101T000000Z"}
        reused_current = current | {
            "publicKey": current["publicKey"].replace(" ", "\t")
        }
        reused_retired = retired | {
            "publicKey": retired["publicKey"].replace(" ", "\t")
        }
        for case, previous, incoming_pin, invalid_history in (
            ("current-key", [], reused_current, False),
            ("retired-key", [historical], reused_retired, False),
            (
                "retired-key-padding",
                [historical],
                retired | {"publicKey": retired["publicKey"] + "="},
                False,
            ),
            (
                "repeated-current",
                [reused_current | {"before": "20250101T000000Z"}],
                fresh,
                True,
            ),
            (
                "repeated-history",
                [historical, reused_retired | {"before": "20260101T000000Z"}],
                fresh,
                True,
            ),
        ):
            with self.subTest(case=case):
                history = current | {"previous": previous}
                if invalid_history:
                    with self.assertRaises(RecoveryError):
                        cloud.signing_pin(history, self.point)
                self.trust.write_bytes(cloud.encoded(history))
                incoming = self.root / "incoming.json"
                incoming.write_bytes(cloud.encoded(incoming_pin))
                output = self.root / case
                with self.assertRaises(RecoveryError):
                    cloud.rotate_trust(self.trust, incoming, "20270101T000000Z", output)
                self.assertFalse(output.exists())
                self.assertEqual(self.trust.read_bytes(), cloud.encoded(history))
                self.assertEqual(incoming.read_bytes(), cloud.encoded(incoming_pin))

    def test_tamper_substitution_and_conflict(self):
        self.publish()
        side = self.provider / (self.point + cloud.SIDECAR)
        original = side.read_bytes()
        envelope = json.loads(original)
        envelope["receipt"]["publicationContext"] = "2" * 64
        side.write_text(json.dumps(envelope))
        with self.assertRaises(RecoveryError):
            self.download()
        self.assertFalse(self.output.exists())
        with self.assertRaises(RecoveryError):
            self.publish()
        side.write_bytes(original)
        self.archive.write_bytes(b"x" * self.archive.stat().st_size)
        with self.assertRaises(RecoveryError):
            self.download()
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
