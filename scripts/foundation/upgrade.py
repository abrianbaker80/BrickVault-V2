"""Stage a schema upgrade on an independent copy of a stopped instance."""

import os
from pathlib import Path
import shutil
import tempfile

from instance import Instance, RELEASE, ROOT, private_dir
from recovery import row_signatures, signatures
from runtime import exclusive, operation


def verify_opening_stock(instance):
    """The v4 opening projection must reproduce every v3 placement and allocation."""
    matches = instance.sql("""
        WITH original AS (
            SELECT id,id AS origin_placement_id,purchase_id,catalog_id,catalog_revision,
                   location_id,location_code,quantity AS introduced_quantity,delivery_id,quantity
            FROM brickvault.placements
        ), current AS (
            SELECT id,origin_placement_id,purchase_id,catalog_id,catalog_revision,
                   location_id,location_code,introduced_quantity,delivery_id,quantity
            FROM brickvault.current_placement_stock
        ), backing AS (
            SELECT offer_id,placement_id,quantity FROM brickvault.offer_backing
        ), projected AS (
            SELECT offer_id,placement_id,quantity FROM brickvault.current_offer_backing
        )
        SELECT NOT EXISTS (
            (TABLE original EXCEPT ALL TABLE current) UNION ALL
            (TABLE current EXCEPT ALL TABLE original)
        ) AND NOT EXISTS (
            (TABLE backing EXCEPT ALL TABLE projected) UNION ALL
            (TABLE projected EXCEPT ALL TABLE backing)
        );
    """)
    if matches != "t":
        raise ValueError(
            "Opening position or offer backing differs from receiving history."
        )


def stage_upgrade(source, output, release=RELEASE):
    output = Path(output).resolve()
    release = Path(release).resolve(strict=True)
    if (
        output == source.path
        or source.path in output.parents
        or output in source.path.parents
        or output == ROOT
        or ROOT in output.parents
        or output == release
        or release in output.parents
        or output in release.parents
    ):
        raise ValueError(
            "Use an independent private output directory outside the checkout."
        )
    if (
        not (release / "BrickVault.Api.dll").is_file()
        or not (release / "wwwroot/index.html").is_file()
    ):
        raise ValueError(
            "Select a built Workbench release with its API and browser assets."
        )

    with operation(source), exclusive(source.path / "supervisor.lock"):
        if (source.cluster / "postmaster.pid").exists():
            raise ValueError("Stop the source database before staging the upgrade.")
        if source.cluster.is_symlink() or source.cluster.is_junction():
            raise ValueError("The source cluster must use self-contained storage.")
        # Reserve a new container. Its instance/ child appears only after verification.
        output.mkdir(mode=0o700)
        try:
            private_dir(output)
            with tempfile.TemporaryDirectory(dir=output) as tmp:
                stage = Path(tmp) / "instance"
                private_dir(stage)
                # The supported foundation uses a self-contained PostgreSQL cluster.
                # Links to external storage must never reach back into the original.
                for root, dirs, files in os.walk(source.cluster):
                    for name in dirs + files:
                        p = Path(root) / name
                        if p.is_symlink() or p.is_junction():
                            raise ValueError(
                                "The source cluster contains an external storage link."
                            )
                shutil.copytree(source.cluster, stage / "pgdata")
                for name in ("instance.json", "owner-login.txt"):
                    shutil.copy2(source.path / name, stage / name)
                for name in ("caddy-store", "verified-restores"):
                    if (source.path / name).is_dir():
                        shutil.copytree(source.path / name, stage / name)
                shutil.copytree(release, stage / "release")
                candidate = Instance(stage)
                try:
                    candidate.pg_start()
                    before = signatures(candidate, candidate.database)
                    projections = ("current_placement_stock", "current_offer_backing")
                    if "stock_build_holds" in before:
                        projections += ("current_build_holds",)
                    if "recount_batches" in before:
                        projections += ("recount_tasks",)
                    if "draft_backing_withdrawals" in before:
                        projections += ("offer_backing_ledger", "internal_draft_offers")
                    if "stock_quarantines" in before:
                        projections += ("current_stock_quarantines", "current_position_quarantine")
                    opening = None
                    if "placement_positions" in before:
                        opening = row_signatures(candidate, candidate.database, projections)
                    candidate.migrate()
                    candidate.grants()
                    after = signatures(candidate, candidate.database)
                    if (
                        after["schema_version"]["rows"]
                        <= before["schema_version"]["rows"]
                        or any(
                            before[t] != after[t]
                            for t in before
                            if t != "schema_version"
                        )
                        or any(
                            after[t]["rows"] for t in after.keys() - before.keys()
                            if t != "placement_positions"
                        )
                    ):
                        raise ValueError(
                            "The staged upgrade failed its business-record checks."
                        )
                    if opening is not None:
                        if row_signatures(candidate, candidate.database, projections) != opening:
                            raise ValueError("An existing stock balance, commitment or recount task changed during upgrade.")
                    elif "placement_positions" in after:
                        verify_opening_stock(candidate)
                finally:
                    candidate.pg_stop()
                stage.rename(output / "instance")
        except BaseException:
            # Leave a nonempty container intact for inspection if cleanup itself failed.
            if not any(output.iterdir()):
                output.rmdir()
            raise
