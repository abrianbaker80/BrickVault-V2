"""Exercise resumable nightly publication and invalid capture handling with a synthetic stream."""

from contextlib import redirect_stdout
from datetime import timedelta
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
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
            "receiptSigningKey": "fixture-key",
            "receiptTrust": "fixture-trust",
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

    def test_pending_is_bound_to_the_original_optional_mirror_and_group(self):
        settings = self.settings | {"hostKeyAlias": "guest"}
        route = SimpleNamespace(
            base="sample:BrickVault-V2/", config="test-profile", config_sha256="0" * 64
        )
        mirror = {"directory": str(self.directory / "mirror"), "group": "readers"}
        original = settings | {"mirrorExport": mirror}
        context = nightly.binding(original, route)
        equivalent = mirror | {
            "directory": str(self.directory / "ancestor" / ".." / "mirror")
        }
        self.assertEqual(
            context, nightly.binding(settings | {"mirrorExport": equivalent}, route)
        )
        point_dir = self.directory / "points" / self.point
        point_dir.mkdir(parents=True)
        (point_dir / "pending.json").write_text(
            json.dumps(self.receipt | {"publicationContext": context})
        )
        with (
            patch.object(nightly, "capture_transport", return_value=route),
            patch.object(
                nightly,
                "capture",
                side_effect=RecoveryError("Guest offline in fixture"),
            ),
            patch.object(nightly, "publish_verified_copy") as publish,
        ):
            for changed in [
                None,
                mirror | {"group": "other"},
                mirror | {"directory": str(self.directory / "other")},
            ]:
                with self.assertRaises(RecoveryError):
                    nightly.run(settings | {"mirrorExport": changed}, self.directory)
                publish.assert_not_called()
                self.assertTrue((point_dir / "pending.json").exists())
                self.assertFalse((point_dir / (self.point + ".json")).exists())

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

    def test_workstation_repairs_damaged_history_and_keeps_newer_point(self):
        target, state = self.directory / "mirror", self.directory / "state"
        target.mkdir()
        state.mkdir()
        older = (nightly.utcnow() - timedelta(days=1)).strftime(
            "%Y%m%dT%H%M%SZ-"
        ) + uuid.uuid4().hex
        old_receipt = dict(self.receipt, point=older)
        (target / (older + ".json")).write_text(json.dumps(old_receipt))
        (target / (older + ".age")).write_bytes(b"damaged")
        config = {"state": state, "mirror": target, "sshConfig": "fixture"}

        def ssh(command, **kwargs):
            if command[-1] == "list":
                return SimpleNamespace(
                    stdout=b"\n".join(
                        json.dumps(r).encode() for r in [old_receipt, self.receipt]
                    )
                )
            self.assertIn(command[-1], {"get " + older, "get " + self.point})
            kwargs["stdout"].write(self.content)
            return SimpleNamespace(returncode=0)

        with (
            patch.object(
                backup_mirror, "managed_dir", side_effect=lambda path, purpose: path
            ),
            patch.object(backup_mirror.subprocess, "run", side_effect=ssh),
            redirect_stdout(io.StringIO()),
        ):
            backup_mirror.catch_up(config)
            self.assertEqual((target / (older + ".age")).read_bytes(), self.content)
            self.assertEqual(
                (target / (self.point + ".age")).read_bytes(), self.content
            )
            self.assertEqual(
                [f.read_bytes() for f in (state / "quarantine").iterdir()], [b"damaged"]
            )
            self.assertEqual(
                json.loads((state / "status.json").read_text())["point"], self.point
            )
            conflict = json.dumps(old_receipt | {"sha256": "0" * 64})
            (target / (older + ".json")).write_text(conflict)
            (target / (self.point + ".age")).unlink()
            with self.assertRaises(RecoveryError):
                backup_mirror.catch_up(config)
            self.assertEqual((target / (older + ".json")).read_text(), conflict)
            self.assertEqual(
                (target / (self.point + ".age")).read_bytes(), self.content
            )

    @unittest.skipUnless(
        os.name == "posix" and hasattr(os, "geteuid") and os.geteuid() == 0,
        "requires a Linux controller account",
    )
    def test_controller_repairs_existing_export_bytes_from_an_independent_source(self):
        import grp

        root = self.directory / "export"
        root.mkdir(mode=0o750)
        source = self.directory / "source.age"
        source.write_bytes(self.content)
        config = {"directory": str(root), "group": grp.getgrgid(os.getgid()).gr_name}
        backup_mirror.export_point(source, self.receipt, config)
        file = root / self.point / (self.point + ".age")
        file.write_bytes(b"damaged")
        self.assertEqual(source.read_bytes(), self.content)
        backup_mirror.export_point(source, self.receipt, config)
        self.assertEqual(file.read_bytes(), self.content)
        self.assertEqual(
            [f.read_bytes() for f in (self.directory / "quarantine").iterdir()],
            [b"damaged"],
        )
        file.unlink()
        backup_mirror.export_point(source, self.receipt, config)
        self.assertEqual(file.read_bytes(), self.content)

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
            patch.object(nightly, "publish_signed"),
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
