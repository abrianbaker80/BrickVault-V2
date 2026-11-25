"""Consistent encrypted logical backups and restore into a new database."""

from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tarfile
import tempfile
import uuid

from instance import BIN, EXT, PG_BIN, NO_WINDOW, execute, label, write_private

FACTS = (
    "schema_version",
    "catalog",
    "locations",
    "commands",
    "audit",
    "jobs",
    "job_effects",
)

RECEIVING_FACTS = ("purchases", "deliveries", "placements", "offers", "offer_backing")


TRANSFER_FACTS = ("placement_positions", "placement_transfers", "placement_transfer_backing")
COUNT_FACTS = ("stock_count_observations", "stock_shortage_cases")
HOLD_FACTS = ("stock_build_holds", "stock_build_hold_releases")
LOSS_FACTS = ("stock_loss_postings", "stock_loss_offer_reductions")
RECOUNT_FACTS = ("recount_previews", "recount_preview_targets", "recount_batches", "recount_results")
WITHDRAWAL_FACTS = ("draft_backing_withdrawals",)
QUARANTINE_FACTS = (
    "stock_quarantines", "stock_quarantine_releases",
    "stock_loss_quarantine_reviews", "stock_loss_quarantine_attributions",
)
SCHEMA_FACTS = {
    2: FACTS,
    3: FACTS + RECEIVING_FACTS,
    4: FACTS + RECEIVING_FACTS + TRANSFER_FACTS,
    5: FACTS + RECEIVING_FACTS + TRANSFER_FACTS + COUNT_FACTS,
    6: FACTS + RECEIVING_FACTS + TRANSFER_FACTS + COUNT_FACTS + HOLD_FACTS,
    7: FACTS + RECEIVING_FACTS + TRANSFER_FACTS + COUNT_FACTS + HOLD_FACTS + LOSS_FACTS,
    8: FACTS + RECEIVING_FACTS + TRANSFER_FACTS + COUNT_FACTS + HOLD_FACTS + LOSS_FACTS + RECOUNT_FACTS,
    9: FACTS + RECEIVING_FACTS + TRANSFER_FACTS + COUNT_FACTS + HOLD_FACTS + LOSS_FACTS + RECOUNT_FACTS + WITHDRAWAL_FACTS,
    10: FACTS + RECEIVING_FACTS + TRANSFER_FACTS + COUNT_FACTS + HOLD_FACTS + LOSS_FACTS + RECOUNT_FACTS + WITHDRAWAL_FACTS + QUARANTINE_FACTS,
}


def digest(path):
    with path.open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


def release_files(s):
    return {
        p.relative_to(s.path / "release").as_posix(): digest(p)
        for p in sorted((s.path / "release").rglob("*"))
        if p.is_file()
    }


@contextmanager
def snapshot(s):
    with subprocess.Popen(
        [
            PG_BIN / ("psql" + EXT),
            "-X",
            "-qAt",
            "-v",
            "ON_ERROR_STOP=1",
            "-d",
            s.database,
        ],
        env=s.sql_env("bv_migrator"),
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        creationflags=NO_WINDOW,
    ) as proc:
        try:
            proc.stdin.write(
                "BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY; SELECT pg_export_snapshot();\n"
            )
            proc.stdin.flush()
            token = proc.stdout.readline().strip()
            if not re.fullmatch("[A-Fa-f0-9-]+", token):
                raise RuntimeError(
                    "Could not establish a consistent database snapshot."
                )
            yield token
        finally:
            proc.stdin.close()
            proc.wait(15)


def signatures(s, database, token=None):
    # jsonb gives stable field order. Length-prefix each UTF-8 row to keep record boundaries unambiguous.
    session = "BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;\n"
    if token:
        session += f"SET TRANSACTION SNAPSHOT '{token}';\n"
    versions = s.sql(
        session + "SELECT version FROM brickvault.schema_version ORDER BY version; COMMIT;",
        database,
        role="bv_migrator",
    )
    history = versions.splitlines()
    tables = SCHEMA_FACTS.get(len(history))
    if tables is None or history != [str(i) for i in range(1, len(history) + 1)]:
        raise ValueError("Unsupported database schema history for backup verification.")
    return row_signatures(s, database, tables, session)


def row_signatures(s, database, tables, session="BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;\n"):
    groups = {}
    for table in tables:
        stmt = (
            session
            + f"SET TIME ZONE 'UTC'; SELECT to_jsonb(t)::text FROM brickvault.{table} t ORDER BY to_jsonb(t)::text COLLATE \"C\";\nCOMMIT;"
        )
        rows = s.sql(stmt, database, role="bv_migrator")
        sig = hashlib.sha256()
        count = 0
        for row in rows.split("\n") if rows else ():
            data = row.encode("utf-8")
            sig.update(len(data).to_bytes(8, "big"))
            sig.update(data)
            count += 1
        groups[table] = {"rows": count, "sha256": sig.hexdigest()}
    return groups


def backup(s, destination):
    destination = Path(destination).resolve()
    if destination.exists():
        raise ValueError("Choose a new destination for the encrypted backup.")
    destination.parent.mkdir(parents=True, exist_ok=True)
    staged = destination.with_name(
        "." + destination.name + "." + uuid.uuid4().hex + ".tmp"
    )
    try:
        with tempfile.TemporaryDirectory(dir=s.path) as scratch:
            folder = Path(scratch)
            dump = folder / "foundation.dump"
            with snapshot(s) as token:
                execute(
                    [
                        PG_BIN / ("pg_dump" + EXT),
                        "-Fc",
                        "--no-owner",
                        "--no-acl",
                        "--schema=brickvault",
                        "--exclude-table-data=brickvault.sessions",
                        "--exclude-table-data=brickvault.worker_health",
                        "--snapshot",
                        token,
                        "--file",
                        dump,
                        "-d",
                        s.database,
                    ],
                    env=s.sql_env("bv_migrator"),
                )
                facts = signatures(s, s.database, token)
            manifest = {
                "format": 1,
                "profile": "foundation",
                "api": "v1",
                "schema": facts["schema_version"]["rows"],
                "workspaceId": str(
                    uuid.UUID(s.config.get("workspaceId", s.config["id"]))
                ),
                "created": datetime.now(timezone.utc).isoformat(),
                "dumpSha256": digest(dump),
                "release": release_files(s),
                "facts": facts,
            }
            (folder / "manifest.json").write_text(
                json.dumps(manifest, sort_keys=True), encoding="utf-8"
            )
            with tarfile.open(folder / "snapshot.tar", "w") as archive:
                archive.add(dump, arcname="foundation.dump")
                archive.add(folder / "manifest.json", arcname="manifest.json")
            execute(
                [
                    BIN / ("age" + EXT),
                    "-r",
                    s.config["recipient"],
                    "-o",
                    staged,
                    folder / "snapshot.tar",
                ]
            )
            # Refuse overwriting a backup created concurrently at the requested destination.
            os.link(staged, destination)
        return manifest
    finally:
        staged.unlink(missing_ok=True)


def restore(s, source, identity, target):
    target = label(target)
    if target == s.database:
        raise ValueError(
            "Restore requires a new database; an active database cannot be the target."
        )
    identity = Path(identity).resolve()
    if s.path in identity.parents:
        raise ValueError(
            "Use an independent recovery identity outside the instance directory."
        )
    with tempfile.TemporaryDirectory(dir=s.path) as scratch:
        folder = Path(scratch)
        execute(
            [
                BIN / ("age" + EXT),
                "-d",
                "-i",
                identity,
                "-o",
                folder / "snapshot.tar",
                Path(source).resolve(),
            ]
        )
        with tarfile.open(folder / "snapshot.tar", "r:") as archive:
            members = archive.getmembers()
            if (
                len(members) != 2
                or {m.name for m in members} != {"manifest.json", "foundation.dump"}
                or not all(m.isfile() for m in members)
            ):
                raise ValueError("Invalid foundation archive layout.")
            archive.extractall(folder, filter="data")
        m = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
        dump = folder / "foundation.dump"
        if (m.get("format"), m.get("profile"), m.get("api")) != (
            1,
            "foundation",
            "v1",
        ) or m.get("schema") not in SCHEMA_FACTS:
            raise ValueError("Unsupported backup format or release contract.")
        if digest(dump) != m.get("dumpSha256") or release_files(s) != m.get("release"):
            raise ValueError(
                "Backup checksum or recovery release mismatch. Use the matching published release."
            )
        lineage = str(uuid.UUID(m["workspaceId"]))
        s.create_db(target)
        execute(
            [
                PG_BIN / ("pg_restore" + EXT),
                "--single-transaction",
                "--exit-on-error",
                "--no-owner",
                "--no-acl",
                "--dbname",
                target,
                dump,
            ],
            env=s.sql_env("bv_migrator"),
        )
        s.grants(target)
        facts = signatures(s, target)
        if facts != m["facts"] or facts["schema_version"]["rows"] != m["schema"]:
            raise RuntimeError(
                "Restored data failed verification. The isolated target is not promoted."
            )
        receipts = s.path / "verified-restores"
        receipts.mkdir(exist_ok=True)
        write_private(
            receipts / (target + ".json"),
            json.dumps(
                {"facts": m["facts"], "release": m["release"], "workspaceId": lineage}
            ),
        )
        return m


def activate(s, target):
    """Select a verified restore on this stopped instance and retain its former database."""
    from runtime import exclusive, operation

    target = label(target)
    receipt = s.path / "verified-restores" / (target + ".json")
    if not receipt.is_file():
        raise ValueError(
            "Choose a database with a successful restore on this instance."
        )
    with operation(s), exclusive(s.path / "supervisor.lock"):
        proof = json.loads(receipt.read_text(encoding="utf-8"))
        s.pg_start()
        try:
            if (
                release_files(s) != proof["release"]
                or signatures(s, target) != proof["facts"]
            ):
                raise ValueError(
                    "Restore changed after verification. Select a fresh verified recovery target."
                )
            s.grants(target)
            s.config["workspaceId"] = proof["workspaceId"]
            s.config["database"] = target
            s.save()
            s.database = target
        finally:
            s.pg_stop()
