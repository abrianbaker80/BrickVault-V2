"""Private state, PostgreSQL and process identities for one isolated foundation."""

import base64
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import subprocess
import tempfile
import uuid

from setup import ROOT, TOOLS, BIN, WIN, EXT

PG_BIN = TOOLS / "foundation/pgsql/bin"
DOTNET = TOOLS / "dotnet" / ("dotnet" + EXT)
RELEASE = TOOLS / "foundation/release"
API = ROOT / "src/BrickVault.Api"
UI = ROOT / "src/workbench"
PRIMARY = "bv_m2_primary"
NO_WINDOW = subprocess.CREATE_NO_WINDOW if WIN else 0


def child_env(*, desktop=False):
    # Give long-lived private processes only the OS settings they need to run.
    allowed = {
        "PATH",
        "SYSTEMROOT",
        "WINDIR",
        "COMSPEC",
        "PATHEXT",
        "TEMP",
        "TMP",
        "TMPDIR",
        "HOME",
        "USERPROFILE",
        "LOCALAPPDATA",
        "APPDATA",
        "LANG",
        "LC_ALL",
        "TZ",
        "SYSTEMDRIVE",
        "PROCESSOR_ARCHITECTURE",
        "NUMBER_OF_PROCESSORS",
        "PROGRAMFILES",
        "PROGRAMDATA",
        "PROGRAMFILES(X86)",
    }
    if desktop:
        allowed.update(
            (
                "DISPLAY",
                "XAUTHORITY",
                "WAYLAND_DISPLAY",
                "XDG_RUNTIME_DIR",
                "DBUS_SESSION_BUS_ADDRESS",
                "XDG_SESSION_TYPE",
            )
        )
    return {
        name: value for name, value in os.environ.items() if name.upper() in allowed
    }


def private_dir(path):
    path = Path(path).resolve()
    if ROOT == path or ROOT in path.parents:
        raise ValueError(
            "Keep private instance data and recovery keys outside the checkout."
        )
    path.mkdir(mode=0o700, parents=True, exist_ok=True)
    if WIN:
        subprocess.run(
            [
                "pwsh",
                "-NoProfile",
                "-NonInteractive",
                "-File",
                Path(__file__).with_name("Protect-Directory.ps1"),
                "-LiteralPath",
                path,
            ],
            check=True,
            env=child_env(),
            creationflags=NO_WINDOW,
            stdout=subprocess.DEVNULL,
        )
    else:
        path.chmod(0o700)
    return path


def write_private(path, text):
    temp = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with temp.open("x", encoding="utf-8", newline="\n") as handle:
            if not WIN:
                os.chmod(temp, 0o600)
            handle.write(text)
        os.replace(temp, path)
    finally:
        temp.unlink(missing_ok=True)


def execute(args, *, env=None, content=None, cwd=None):
    result = subprocess.run(
        args,
        input=content,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=child_env() if env is None else env,
        cwd=cwd,
        creationflags=NO_WINDOW,
    )
    if result.returncode:
        # Arguments, SQL errors and captured output may contain signed material or private paths.
        raise RuntimeError(f"{Path(args[0]).name} failed ({result.returncode}).")
    return result.stdout.strip()


def label(name):
    if not re.fullmatch(r"bv_m2_[a-z][a-z0-9_]{0,44}", name):
        raise ValueError("Choose a foundation database name beginning bv_m2_.")
    return name


def lan_endpoint(origin, expected_port=None):
    """Accept a canonical HTTPS DNS origin suitable for a Caddy site address."""
    label = r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?"
    match = re.fullmatch(
        rf"https://((?:{label}\.)+[a-z](?:[a-z0-9-]{{0,61}}[a-z0-9])?):([1-9][0-9]{{0,4}})",
        origin if isinstance(origin, str) else "",
    )
    if not match:
        raise ValueError("Use a lowercase HTTPS DNS origin with an explicit port.")
    hostname, port = match[1], int(match[2])
    if (
        len(hostname) > 253
        or hostname.endswith(".localhost")
        or not 1024 <= port <= 65533
        or expected_port not in (None, port)
    ):
        raise ValueError(
            "The LAN DNS origin must use the instance's unprivileged HTTPS port."
        )
    return hostname, port


class Instance:
    def __init__(self, directory):
        self.path = Path(directory).resolve()
        self.config = json.loads(
            (self.path / "instance.json").read_text(encoding="utf-8")
        )
        if self.config["format"] != 1:
            raise ValueError("Unsupported foundation configuration format.")
        self.port = self.config["port"]
        if "lanOrigin" in self.config:
            lan_endpoint(self.config["lanOrigin"], self.port)
        self.database = label(self.config["database"])
        self.cluster = self.path / "pgdata"

    @property
    def origin(self):
        return self.config.get("lanOrigin", f"https://localhost:{self.port}")

    @property
    def gateway_bind(self):
        return "0.0.0.0" if "lanOrigin" in self.config else "127.0.0.1"

    @property
    def ca(self):
        return self.path / "caddy-store/pki/authorities/local/root.crt"

    def sql_env(self, role):
        return {
            **child_env(),
            "PGHOST": "127.0.0.1",
            "PGHOSTADDR": "127.0.0.1",
            "PGPORT": str(self.port + 2),
            "PGUSER": role,
            "PGPASSWORD": self.config["roles"][role],
            "PGCONNECT_TIMEOUT": "5",
            "PGCLIENTENCODING": "UTF8",
            "PGSSLMODE": "disable",
            "PGOPTIONS": "-c statement_timeout=60000",
        }

    def sql(self, text, database=None, role="bv_cluster_admin"):
        return execute(
            [
                PG_BIN / ("psql" + EXT),
                "-X",
                "-qAt",
                "--set",
                "ON_ERROR_STOP=1",
                "-d",
                database or self.database,
            ],
            env=self.sql_env(role),
            content=text,
        )

    def app_env(self, role, database=None):
        environment = {
            **child_env(),
            "DOTNET_ROOT": str(DOTNET.parent),
            "ASPNETCORE_ENVIRONMENT": "Development",
            "DOTNET_ENVIRONMENT": "Development",
            "BV_PROFILE": "foundation",
            "BV_PUBLIC_ORIGIN": self.origin,
            "BV_HTTP_PORT": str(self.port + 1),
            "BV_DB": "Host=127.0.0.1;Port="
            + str(self.port + 2)
            + ";Database="
            + label(database or self.database)
            + ";Username="
            + role
            + ";Password="
            + self.config["roles"][role]
            + ";Timeout=5;Command Timeout=20;Include Error Detail=false",
            "DOTNET_NOLOGO": "1",
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        }
        if role == "bv_api":
            environment.update(
                BV_WORKSPACE_ID=str(
                    uuid.UUID(self.config.get("workspaceId", self.config["id"]))
                ),
                BV_GATEWAY_TOKEN=self.config["gateway"],
                BV_SESSION_KEY=self.config["session"],
            )
        return environment

    def pg_start(self):
        cmd = PG_BIN / ("pg_ctl" + EXT)
        if subprocess.run(
            [cmd, "status", "-D", self.cluster],
            env=child_env(),
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        ).returncode:
            # On Windows the postmaster inherits pg_ctl's standard handles for its lifetime.
            subprocess.run(
                [
                    cmd,
                    "start",
                    "-D",
                    self.cluster,
                    "-l",
                    self.path / "postgres.log",
                    "-w",
                    "-t",
                    "40",
                ],
                env=child_env(),
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=True,
                creationflags=NO_WINDOW,
            )

    def pg_stop(self):
        cmd = PG_BIN / ("pg_ctl" + EXT)
        if (
            subprocess.run(
                [cmd, "status", "-D", self.cluster],
                env=child_env(),
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            ).returncode
            == 0
        ):
            execute([cmd, "stop", "-D", self.cluster, "-m", "fast", "-w"])

    def create_db(self, name):
        name = label(name)
        self.sql(
            f"CREATE DATABASE {name} OWNER bv_migrator TEMPLATE template0;", "postgres"
        )
        self.sql(
            f"REVOKE ALL ON DATABASE {name} FROM PUBLIC; GRANT CONNECT ON DATABASE {name} TO bv_api,bv_worker; REVOKE ALL ON SCHEMA public FROM PUBLIC;",
            name,
        )

    def migrate(self, database=None, to=None):
        args = [DOTNET, self.path / "release/BrickVault.Api.dll", "migrate"]
        if to is not None:
            args += ["--to", str(to)]
        execute(
            args, env=self.app_env("bv_migrator", database), cwd=self.path / "release"
        )

    def grants(self, database=None):
        self.sql(
            """
            BEGIN;
            GRANT USAGE ON SCHEMA brickvault TO bv_api, bv_worker;
            GRANT SELECT ON ALL TABLES IN SCHEMA brickvault TO bv_api;
            GRANT INSERT,UPDATE ON brickvault.catalog,brickvault.locations TO bv_api;
            GRANT INSERT ON brickvault.commands,brickvault.audit,brickvault.jobs TO bv_api;
            GRANT INSERT,DELETE ON brickvault.sessions TO bv_api;
            GRANT SELECT ON brickvault.schema_version,brickvault.jobs,brickvault.job_effects,brickvault.worker_health TO bv_worker;
            GRANT UPDATE ON brickvault.jobs TO bv_worker;
            GRANT INSERT ON brickvault.job_effects TO bv_worker;
            GRANT INSERT,UPDATE ON brickvault.worker_health TO bv_worker;
            GRANT SELECT(expires_at),DELETE ON brickvault.sessions TO bv_worker;

            DROP POLICY IF EXISTS sessions_api ON brickvault.sessions;
            CREATE POLICY sessions_api ON brickvault.sessions TO bv_api
                USING (true) WITH CHECK (true);
            DROP POLICY IF EXISTS sessions_worker_read ON brickvault.sessions;
            CREATE POLICY sessions_worker_read ON brickvault.sessions FOR SELECT TO bv_worker
                USING (expires_at <= statement_timestamp());
            DROP POLICY IF EXISTS sessions_worker_delete ON brickvault.sessions;
            CREATE POLICY sessions_worker_delete ON brickvault.sessions FOR DELETE TO bv_worker
                USING (expires_at <= statement_timestamp());
            ALTER TABLE brickvault.sessions ENABLE ROW LEVEL SECURITY;
            DO $grants$
            BEGIN
                IF (SELECT max(version) FROM brickvault.schema_version) >= 3 THEN
                    GRANT INSERT ON brickvault.purchases, brickvault.deliveries,
                        brickvault.placements, brickvault.offers, brickvault.offer_backing TO bv_api;
                    GRANT UPDATE (revision) ON brickvault.purchases TO bv_api;
                END IF;
                IF (SELECT max(version) FROM brickvault.schema_version) >= 4 THEN
                    GRANT INSERT ON brickvault.placement_transfers,brickvault.placement_transfer_backing TO bv_api;
                END IF;
                IF (SELECT max(version) FROM brickvault.schema_version) >= 5 THEN
                    GRANT INSERT (id,command_id,purchase_id,position_id,location_id,location_code,
                        expected_purchase_revision,counted_quantity,reason)
                        ON brickvault.stock_count_observations TO bv_api;
                END IF;
                IF (SELECT max(version) FROM brickvault.schema_version) >= 6 THEN
                    GRANT INSERT ON brickvault.stock_build_holds, brickvault.stock_build_hold_releases TO bv_api;
                END IF;
            END $grants$;
            COMMIT;
        """,
            database,
            role="bv_migrator",
        )

    def save(self):
        write_private(
            self.path / "instance.json", json.dumps(self.config, indent=2) + "\n"
        )


def password_hash(password):
    return execute([BIN / ("caddy" + EXT), "hash-password"], content=password + "\n")


def probe_listener(host, port):
    # Match server bind semantics. On POSIX a closed reusable socket may still be in TIME_WAIT.
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        option = socket.SO_EXCLUSIVEADDRUSE if WIN else socket.SO_REUSEADDR
        probe.setsockopt(socket.SOL_SOCKET, option, 1)
        probe.bind((host, port))
        probe.listen(1)


def init(
    directory, recovery, port, *, release=RELEASE, lan_origin=None, recipient=None
):
    directory = Path(directory).resolve()
    if (recipient is None) == (recovery is None):
        raise ValueError(
            "Choose a recovery identity directory or an external public age recipient."
        )
    if directory.exists() and any(directory.iterdir()):
        raise ValueError("New instance requires an empty dedicated directory.")
    if not 1024 <= port <= 65533:
        raise ValueError("Choose three available consecutive unprivileged ports.")
    if lan_origin is not None:
        lan_endpoint(lan_origin, port)
    for number in range(port, port + 3):
        host = "0.0.0.0" if number == port and lan_origin else "127.0.0.1"
        probe_listener(host, number)
    release = Path(release).resolve()
    if not (release / "BrickVault.Api.dll").is_file():
        raise ValueError("Build the release before initializing an instance.")
    uid = uuid.uuid4().hex
    if recipient is not None:
        if not re.fullmatch(r"age1[a-z0-9]{58}", recipient):
            raise ValueError("Supply a public X25519 age recipient.")
        with tempfile.TemporaryDirectory() as scratch:
            probe = private_dir(scratch) / "probe.age"
            execute([BIN / ("age" + EXT), "-r", recipient, "-o", probe], content="")
        private_dir(directory)
    else:
        recovery = Path(recovery).resolve()
        if (
            directory == recovery
            or directory in recovery.parents
            or recovery in directory.parents
        ):
            raise ValueError(
                "Use separate, non-overlapping directories for runtime and independent recovery."
            )
        if recovery.exists() and any(
            not (entry.is_file() and re.fullmatch(r"[0-9a-f]{32}\.agekey", entry.name))
            for entry in recovery.iterdir()
        ):
            raise ValueError(
                "Use a dedicated key directory with only foundation recovery identities."
            )
        private_dir(directory)
        private_dir(recovery)
        keyfile = recovery / (uid + ".agekey")
        execute([BIN / ("age-keygen" + EXT), "-o", keyfile])
        recipient = execute([BIN / ("age-keygen" + EXT), "-y", keyfile])
    password = secrets.token_urlsafe(24)
    config = {
        "format": 1,
        "id": uid,
        "port": port,
        "database": PRIMARY,
        "recipient": recipient,
        "gateway": secrets.token_urlsafe(40),
        "session": base64.b64encode(os.urandom(32)).decode(),
        "owner_hash": password_hash(password),
        "roles": {
            r: secrets.token_urlsafe(32)
            for r in ("bv_cluster_admin", "bv_migrator", "bv_api", "bv_worker")
        },
    }
    if lan_origin is not None:
        config["lanOrigin"] = lan_origin
    write_private(directory / "instance.json", json.dumps(config, indent=2) + "\n")
    write_private(directory / "owner-login.txt", "operator\n" + password + "\n")
    shutil.copytree(release, directory / "release")
    inst = Instance(directory)
    pw = directory / "initdb.pw"
    write_private(pw, config["roles"]["bv_cluster_admin"] + "\n")
    try:
        execute(
            [
                PG_BIN / ("initdb" + EXT),
                "-D",
                inst.cluster,
                "-U",
                "bv_cluster_admin",
                "--pwfile",
                pw,
                "--auth=scram-sha-256",
                "--encoding=UTF8",
                "--locale=C",
            ]
        )
    finally:
        pw.unlink(missing_ok=True)
    with (inst.cluster / "postgresql.conf").open("a", encoding="utf-8") as pg:
        pg.write(
            f"\nlisten_addresses='127.0.0.1'\nport={port+2}\nunix_socket_directories=''\nlog_min_error_statement=panic\n"
        )
    inst.pg_start()
    try:
        for role, pwd in config["roles"].items():
            if role != "bv_cluster_admin":
                inst.sql(
                    f"CREATE ROLE {role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '{pwd}';",
                    "postgres",
                )
        inst.sql(
            "REVOKE CONNECT,TEMP ON DATABASE postgres FROM PUBLIC; REVOKE CONNECT,TEMP ON DATABASE template1 FROM PUBLIC;",
            "postgres",
        )
        inst.create_db(PRIMARY)
        inst.migrate()
        inst.grants()
    finally:
        inst.pg_stop()
    return inst
