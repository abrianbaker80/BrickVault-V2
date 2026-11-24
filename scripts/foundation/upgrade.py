"""Stage the receiving release on an independent copy of a stopped M2 instance."""

import os
from pathlib import Path
import shutil
import tempfile

from instance import Instance, RELEASE, ROOT, private_dir
from recovery import FACTS, RECEIVING_FACTS, signatures
from runtime import exclusive, operation


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
                    if before["schema_version"]["rows"] != 2:
                        raise ValueError("This upgrade requires the M2 schema.")
                    candidate.migrate()
                    candidate.grants()
                    after = signatures(candidate, candidate.database)
                    if (
                        after["schema_version"]["rows"] != 3
                        or any(
                            before[t] != after[t]
                            for t in FACTS
                            if t != "schema_version"
                        )
                        or any(after[t]["rows"] for t in RECEIVING_FACTS)
                    ):
                        raise ValueError(
                            "The staged upgrade failed its business-record checks."
                        )
                finally:
                    candidate.pg_stop()
                stage.rename(output / "instance")
        except BaseException:
            # Leave a nonempty container intact for inspection if cleanup itself failed.
            if not any(output.iterdir()):
                output.rmdir()
            raise
