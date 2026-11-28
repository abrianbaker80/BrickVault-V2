"""Stage a schema upgrade on an independent copy of a stopped instance."""

import os
from pathlib import Path
import shutil
import tempfile

from instance import Instance, RELEASE, ROOT, private_dir
from recovery import ADDED_COLUMNS, row_signatures, signatures
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
                    if "stock_loss_recoveries" in before:
                        projections += ("delivery_loss_balances",)
                    if "stock_part_outs" in before:
                        projections += ("delivery_part_out_balances",)
                    opening = None
                    if "placement_positions" in before:
                        opening = row_signatures(candidate, candidate.database, projections)
                    candidate.migrate()
                    candidate.grants()
                    after = signatures(candidate, candidate.database)
                    comparable = after.copy()
                    source_version = before["schema_version"]["rows"]
                    target_version = after["schema_version"]["rows"]
                    if "offers" in before and source_version < 16 <= target_version:
                        # The old price had been entered in the purchase currency. Verify that fact
                        # and compare every pre-existing offer column against its original signature.
                        if candidate.sql("""
                            SELECT NOT EXISTS (
                                SELECT 1 FROM brickvault.offers o JOIN brickvault.purchases p ON p.id=o.purchase_id
                                WHERE o.currency IS DISTINCT FROM p.currency OR o.price_scale_verified
                            );
                        """) != "t":
                            raise ValueError("An opening offer differs from its historical currency or claims an unverified price scale.")
                    if "offer_backing" in before and source_version < 17 <= target_version:
                        if candidate.sql("""
                            SELECT NOT EXISTS (
                                SELECT 1 FROM brickvault.offer_backing b
                                JOIN brickvault.offers o ON o.id=b.offer_id
                                JOIN brickvault.commands c ON c.command_id=o.command_id
                                WHERE b.purchase_id IS DISTINCT FROM o.purchase_id
                                   OR b.purchase_revision IS DISTINCT FROM c.revision
                            ) AND NOT EXISTS (
                                SELECT 1 FROM brickvault.draft_backing_withdrawals WHERE expected_offer_total IS NOT NULL
                            )
                        """) != "t":
                            raise ValueError("A migrated allocation differs from its original purchase revision or a past withdrawal acquired a new confirmation.")
                        contents_tables = ("deliveries", "placements", "stock_part_out_outputs", "offers", "stock_loss_recoveries")
                        for table in contents_tables:
                            if candidate.sql(f"SELECT EXISTS (SELECT 1 FROM brickvault.{table} WHERE set_contents IS NOT NULL)") != "f":
                                raise ValueError(f"The upgrade assigned unobserved box or manual contents to existing {table}.")
                        if candidate.sql("SELECT EXISTS (SELECT 1 FROM brickvault.stock_loss_recoveries WHERE result_position_id IS DISTINCT FROM position_id)") != "f":
                            raise ValueError("An earlier stock recovery changed its receiving position during upgrade.")
                    reshaped = {
                        name for version, tables in ADDED_COLUMNS.items() if source_version < version <= target_version
                        for name in tables if name in before
                    }
                    if reshaped:
                        comparable.update(row_signatures(candidate, candidate.database, reshaped, source_version=source_version))
                    if (
                        after["schema_version"]["rows"]
                        <= before["schema_version"]["rows"]
                        or any(
                            before[t] != comparable[t]
                            for t in before
                            if t != "schema_version"
                        )
                        or any(
                            after[t]["rows"] for t in after.keys() - before.keys()
                            if t not in ("placement_positions", "location_labels", "location_capacity_fences")
                        )
                    ):
                        raise ValueError(
                            "The staged upgrade failed its business-record checks."
                        )
                    if "location_labels" in after and "location_labels" not in before:
                        canonical = candidate.sql("""
                            WITH expected AS (
                                SELECT code AS label_key,code AS label,id AS location_id,revision AS claimed_revision
                                FROM brickvault.locations
                            ), actual AS (
                                SELECT label_key,label,location_id,claimed_revision FROM brickvault.location_labels
                            )
                            SELECT NOT EXISTS (
                                (TABLE expected EXCEPT ALL TABLE actual) UNION ALL
                                (TABLE actual EXCEPT ALL TABLE expected)
                            )
                        """)
                        if canonical != "t":
                            raise ValueError("Opening bin labels differ from the original location identities or revisions.")
                    if "location_capacity_fences" in after and "location_capacity_fences" not in before:
                        complete = candidate.sql("""
                            SELECT NOT EXISTS (
                                SELECT 1 FROM brickvault.locations l FULL JOIN brickvault.location_capacity_fences f
                                    ON f.location_id=l.id
                                WHERE l.id IS NULL OR f.location_id IS NULL
                            )
                        """)
                        if complete != "t":
                            raise ValueError("The capacity migration did not initialize exactly the original bins.")
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
