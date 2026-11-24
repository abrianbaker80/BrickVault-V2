"""Exercise signed cloud receipt publication and offline recovery with separate trust custody."""

from contextlib import contextmanager
import hashlib
import base64
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid

import cloud_receipt as cloud
import backup_mirror
import onedrive_reader
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
            ["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", path], check=True
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
        profile = self.root / "reader"
        profile.mkdir()
        (profile / "reader.conf").write_bytes(b"RCLONE_ENCRYPT_V0:\nfixture")
        (profile / ".brickvault-v2-offhost").write_text("onedrive-reader")
        (profile / "authorization.json").write_text(
            json.dumps({"state": "ready", "verifiedAt": "enrollment"})
        )
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
