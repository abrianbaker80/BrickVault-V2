"""Exercise signed cloud receipt publication and offline recovery with separate trust custody."""

from contextlib import contextmanager
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import uuid

import cloud_receipt as cloud
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
