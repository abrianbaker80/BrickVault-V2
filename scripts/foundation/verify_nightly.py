"""Exercise resumable nightly publication and invalid capture handling with a synthetic stream."""

from contextlib import redirect_stdout
from datetime import timedelta
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import uuid

import backup_mirror
import nightly
from offhost import FORMAT, RecoveryError, read_receipt


class NightlyChecks(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.directory = Path(self.scratch.name)
        self.settings = {
            "sourceId": str(uuid.uuid4()),
            "workspaceId": str(uuid.uuid4()),
            "maxBytes": 4096,
            "reserveBytes": 1024,
            "transport": str(self.directory / "route.json"),
        }
        self.content = b"synthetic encrypted capture fixture"
        self.point = nightly.utcnow().strftime("%Y%m%dT%H%M%SZ-") + uuid.uuid4().hex
        self.receipt = {
            "point": self.point,
            "bytes": len(self.content),
            "sha256": hashlib.sha256(self.content).hexdigest(),
            "format": FORMAT,
            "sourceInstance": self.settings["sourceId"],
            "workspaceId": self.settings["workspaceId"],
        }

    def receive(self, folder, data=None, **changes):
        header = json.dumps(self.receipt | changes).encode("utf-8")
        stream = io.BytesIO(header + b"\n" + (self.content if data is None else data))
        return nightly.receive(stream, folder, self.settings, "test-context")

    def test_source_size_and_digest(self):
        for changes in (
            {"sourceInstance": str(uuid.uuid4())},
            {"workspaceId": str(uuid.uuid4())},
            {"bytes": 0},
            {"bytes": 4097},
            {"sha256": "0" * 64},
            {"data": self.content[:-1]},
            {"data": self.content + b"extra"},
        ):
            with (
                self.subTest(changes=changes),
                tempfile.TemporaryDirectory(dir=self.directory) as tmp,
            ):
                with self.assertRaises(RecoveryError):
                    self.receive(Path(tmp), **changes)
                self.assertFalse((Path(tmp) / "pending.json").exists())

    def test_workstation_preserves_last_verified_point_after_repeated_failures(self):
        config = {
            **self.settings,
            "state": self.directory / "state",
            "mirror": self.directory / "mirror",
            "sshConfig": "fixture",
        }
        for path in (config["mirror"], config["state"]):
            path.mkdir()
        (config["state"] / ".brickvault-v2-offhost").write_text("nightly-mirror-state")
        (config["mirror"] / (self.point + ".age")).write_bytes(self.content)
        with (
            patch.object(
                backup_mirror, "managed_dir", side_effect=lambda path, purpose: path
            ),
            patch.object(backup_mirror.subprocess, "run") as response,
            redirect_stdout(io.StringIO()),
        ):
            response.return_value.stdout = json.dumps(self.receipt).encode() + b"\n"
            backup_mirror.catch_up(config)
            backup_mirror.health(config)
            verified = json.loads((config["state"] / "status.json").read_text())
            response.side_effect = subprocess.CalledProcessError(255, "fixture")
            for _ in range(2):
                with self.assertRaises(subprocess.CalledProcessError):
                    backup_mirror.catch_up(config)
                with self.assertRaises(RecoveryError):
                    backup_mirror.health(config)
                status = json.loads((config["state"] / "status.json").read_text())
                self.assertEqual(
                    (status["point"], status["verifiedAt"]),
                    (self.point, verified["verifiedAt"]),
                )
                self.assertEqual(status["state"], "failed")

    def test_pending_retries_without_recapture_and_stale_alarm(self):
        def capture(points, settings, context):
            directory = points / self.point
            directory.mkdir()
            return self.receive(directory)

        with (
            patch.object(nightly, "capture_transport"),
            patch.object(nightly, "binding", return_value="test-context"),
            patch.object(nightly, "capture", side_effect=capture) as capture_call,
            patch.object(nightly, "publish_verified_copy") as publish,
            redirect_stdout(io.StringIO()),
        ):
            publish.side_effect = RecoveryError("Provider unavailable in fixture")
            with self.assertRaises(RecoveryError):
                nightly.run(self.settings, self.directory)
            point_dir = self.directory / "points" / self.point
            self.assertTrue((point_dir / "pending.json").is_file())
            self.assertFalse((point_dir / (self.point + ".json")).exists())
            with self.assertRaises(RecoveryError):
                nightly.health(self.directory, 30)
            publish.side_effect = None
            nightly.run(self.settings, self.directory)
            nightly.run(self.settings, self.directory)
            self.assertFalse((point_dir / "pending.json").exists())
            self.assertEqual(
                read_receipt(point_dir / (self.point + ".json"), self.point)["sha256"],
                self.receipt["sha256"],
            )
            self.assertEqual(capture_call.call_count, 1)
            self.assertEqual(publish.call_count, 2)
            nightly.health(self.directory, 30)
            previous = nightly.status_data(self.directory)
            capture_call.side_effect = RecoveryError("Guest offline in fixture")
            for _ in range(2):
                with self.assertRaises(RecoveryError):
                    nightly.run(self.settings, self.directory, before_change=True)
                with self.assertRaises(RecoveryError):
                    nightly.health(self.directory, 30)
                status = nightly.status_data(self.directory)
                self.assertEqual(
                    (status["lastPoint"], status["verifiedAt"]),
                    (previous["lastPoint"], previous["verifiedAt"]),
                )
            self.point = nightly.utcnow().strftime("%Y%m%dT%H%M%SZ-") + uuid.uuid4().hex
            self.receipt["point"] = self.point
            capture_call.side_effect = capture
            nightly.run(self.settings, self.directory, before_change=True)
            nightly.run(self.settings, self.directory)
            self.assertNotEqual(
                previous["lastPoint"], nightly.status_data(self.directory)["lastPoint"]
            )
            self.assertEqual(len(list((self.directory / "points").glob("*/*.json"))), 2)
            self.assertEqual(publish.call_count, 3)
            with patch.object(
                nightly, "utcnow", return_value=nightly.utcnow() + timedelta(hours=31)
            ):
                with self.assertRaises(RecoveryError):
                    nightly.health(self.directory, 30)


if __name__ == "__main__":
    unittest.main()
