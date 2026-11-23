"""Exercise the normal HTTPS service, worker, migrations and encrypted restore in an isolated lab."""

import base64
import errno
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import patch
import http.cookiejar
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

from browser import main as open_browser, remote_connection
from instance import (
    BIN,
    DOTNET,
    EXT,
    ROOT,
    UI,
    WIN,
    Instance,
    child_env,
    execute,
    init,
    lan_endpoint,
    probe_listener,
)
from recovery import activate, backup, restore
from runtime import (
    configure_lan,
    end,
    ready,
    readiness_token,
    rotate,
    start,
    start_gateway,
    stop,
)


def require(ok, reason):
    if not ok:
        raise AssertionError(reason)


class Browser:
    def __init__(self, s):
        self.s = s
        self.token = None
        self.lineage = None
        self.client = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
            urllib.request.HTTPSHandler(
                context=ssl.create_default_context(cafile=str(s.ca))
            ),
        )

    def call(self, path, method="GET", data=None, headers=None, expected=200):
        h = {"Accept": "application/json"}
        if method != "GET":
            h["Origin"] = self.s.origin
        if self.token:
            h["X-CSRF-Token"] = self.token
            h["X-Workspace-Id"] = self.lineage
        if data is not None:
            h["Content-Type"] = "application/json"
        req = urllib.request.Request(
            self.s.origin + "/api/v1/" + path,
            data=json.dumps(data).encode() if data is not None else None,
            method=method,
            headers={**h, **(headers or {})},
        )
        try:
            reply = self.client.open(req, timeout=10)
        except urllib.error.HTTPError as err:
            reply = err
        with reply:
            require(
                reply.status == expected
                or (isinstance(expected, tuple) and reply.status in expected),
                f"{method} {path} returned {reply.status}; expected {expected}",
            )
            body = reply.read()
            return (
                json.loads(body)
                if body and reply.headers.get_content_type() == "application/json"
                else None
            )

    def login(self, password):
        basic = base64.b64encode(("operator:" + password).encode()).decode()
        data = self.call("session", "POST", headers={"Authorization": "Basic " + basic})
        self.token = data["csrfToken"]
        self.lineage = str(uuid.UUID(data["workspaceId"]))
        require(data["actor"] == "operator", "Unexpected sign-in identity.")


def poll(fn, check, secs=20):
    limit = time.monotonic() + secs
    while True:
        value = fn()
        if check(value):
            return value
        if time.monotonic() >= limit:
            raise AssertionError("Timed out waiting for the expected foundation state.")
        time.sleep(0.3)


def expected_failure(action):
    try:
        action()
    except (RuntimeError, ValueError, subprocess.CalledProcessError):
        return
    raise AssertionError("Operation unexpectedly succeeded.")


def new_id():
    return str(uuid.uuid4())


def migration_test(s):
    db = "bv_m2_test_upgrade"
    s.create_db(db)
    s.migrate(db, to=1)
    fixture_id = new_id()
    s.sql(
        f"INSERT INTO brickvault.locations (id,revision,code,name) VALUES ('{fixture_id}',2,'A-17','Before upgrade')",
        db,
        "bv_migrator",
    )
    inspect = f"SELECT id::text, revision, code, name FROM brickvault.locations WHERE id='{fixture_id}'"
    expected = f"{fixture_id}|2|A-17|Before upgrade"
    s.sql("CREATE TABLE brickvault.job_effects(test boolean);", db, "bv_migrator")
    expected_failure(lambda: s.migrate(db))
    require(
        s.sql(
            "SELECT max(version),to_regclass('brickvault.jobs') IS NULL FROM brickvault.schema_version;",
            db,
        )
        == "1|t",
        "Interrupted upgrade partially committed.",
    )
    require(
        s.sql(inspect, db) == expected,
        "Rolled-back upgrade changed an existing record.",
    )
    s.sql("DROP TABLE brickvault.job_effects;", db, "bv_migrator")
    s.migrate(db)
    s.grants(db)
    require(
        s.sql(inspect, db, "bv_api") == expected,
        "Retried upgrade lost the original record.",
    )
    s.sql(
        "UPDATE brickvault.schema_version SET checksum='incorrect' WHERE version=2",
        db,
        "bv_migrator",
    )
    expected_failure(lambda: s.migrate(db))
    require(
        s.sql("SELECT count(*) FROM brickvault.schema_version", db) == "2",
        "Version ledger changed unexpectedly.",
    )
    print(
        "Migration v1 to v2, interrupted migration rollback, checksum rejection: passed.",
        flush=True,
    )


def qualify_runtime_start(s):
    """Observe the real environment passed to pg_ctl and the background supervisor."""
    launch = subprocess.Popen
    observed = set()

    def inspect_launch(args, **kwargs):
        name = Path(args[0]).stem.lower()
        if name == "pg_ctl" or "supervise" in args:
            env = kwargs.get("env")
            require(
                env is not None and "BV_ENV_SENTINEL" not in env,
                "Runtime child inherited ambient settings.",
            )
            observed.add("supervisor" if "supervise" in args else name)
        return launch(args, **kwargs)

    with (
        patch.dict(os.environ, {"BV_ENV_SENTINEL": "parent-only"}),
        patch("subprocess.Popen", side_effect=inspect_launch),
    ):
        s.pg_start()
        start(s)
    require(
        observed == {"pg_ctl", "supervisor"},
        "Expected runtime launches were not inspected.",
    )


def qualify_gateway_error(s):
    """Exercise a normal readiness check with the backend stopped and inspect its actual log."""
    gateway = start_gateway(s)
    try:
        ctx = ssl.create_default_context(cafile=str(s.ca))
        client = urllib.request.build_opener(
            urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=ctx)
        )
        request = urllib.request.Request(
            s.origin + "/internal/ready",
            headers={"Authorization": "Bearer " + readiness_token(s)},
        )

        def outcome():
            try:
                with client.open(request, timeout=2) as response:
                    return response.status
            except urllib.error.HTTPError as err:
                err.close()
                return err.code
            except OSError:
                return None

        poll(outcome, lambda status: status == 502)
    finally:
        end(gateway)
    log = (s.path / "gateway.log").read_text(encoding="utf-8", errors="replace")
    require(
        readiness_token(s) not in log and s.config["gateway"] not in log,
        "Private probe leaked credentials through a gateway failure.",
    )


def qualify_guest(s, lab):
    hostname = "workbench.example.test"
    origin = f"https://{hostname}:{s.port}"
    original = s.origin
    require(
        s.gateway_bind == "127.0.0.1" and original == f"https://localhost:{s.port}",
        "Default instance did not use a loopback gateway.",
    )
    for bad in (
        origin + "/",
        origin + "?a=b",
        origin.replace(hostname, "127.0.0.1"),
        origin.replace(hostname, "owner@" + hostname),
        origin.replace("https:", "http:"),
    ):
        expected_failure(lambda: lan_endpoint(bad, s.port))
    configure_lan(s, origin)
    require(
        s.gateway_bind == "0.0.0.0",
        "The explicit LAN profile did not bind the guest gateway.",
    )
    descriptor = lab / "connection.json"
    lookup = socket.getaddrinfo

    def resolve(host, *args, **kwargs):
        # Only this qualification process maps the fictional client DNS name to the synthetic guest.
        return lookup("127.0.0.1" if host == hostname else host, *args, **kwargs)

    try:
        start(s)
        require(
            ready(s),
            "The supervisor did not verify its canonical certificate on loopback.",
        )
        config = json.loads(
            execute(
                [
                    BIN / ("caddy" + EXT),
                    "adapt",
                    "--config",
                    s.path / "Caddyfile",
                    "--adapter",
                    "caddyfile",
                ],
                env={
                    **child_env(),
                    "BV_GATEWAY_TOKEN": "fixture",
                    "BV_READY_TOKEN": "fixture",
                    "BV_OWNER_HASH": "fixture",
                },
            )
        )
        (server,) = config["apps"]["http"]["servers"].values()
        routes = server["routes"][0]["handle"][0]["routes"]
        require(
            routes[0]["handle"][0].get("abort")
            and "127.0.0.1/8" in routes[0]["match"][0]["not"][0]["remote_ip"]["ranges"],
            "The gateway did not reject external peers before forwarding requests.",
        )
        ca = s.ca.read_text(encoding="ascii")
        pin = hashlib.sha256(ssl.PEM_cert_to_DER_cert(ca)).hexdigest()
        route = {"origin": origin, "ca": str(s.ca), "caSha256": "0" * 64}
        descriptor.write_text(json.dumps(route), encoding="utf-8")
        expected_failure(lambda: remote_connection(descriptor))
        route["caSha256"] = pin
        descriptor.write_text(json.dumps(route), encoding="utf-8")
        with (
            patch("socket.getaddrinfo", resolve),
            patch.dict(os.environ, {"no_proxy": "*", "NO_PROXY": "*"}),
        ):
            checked_origin, profile_id, trusted_ca = remote_connection(descriptor)
            require(
                checked_origin == origin
                and ssl.PEM_cert_to_DER_cert(trusted_ca)
                == ssl.PEM_cert_to_DER_cert(ca),
                "The workstation selected a different endpoint or root.",
            )
            firefox, profile = lab / "firefox", lab / "browser" / profile_id
            with (
                patch(
                    "sys.argv",
                    [
                        "browser.py",
                        "remote",
                        "--connection",
                        str(descriptor),
                        "--firefox",
                        str(firefox),
                    ],
                ),
                patch(
                    "browser.start",
                    side_effect=AssertionError("Started a workstation backend."),
                ),
                patch("browser.trusted_profile", return_value=profile) as provision,
                patch("browser.subprocess.Popen") as launch,
            ):
                open_browser()
            provision.assert_called_once_with(
                lab / "browser", profile_id, trusted_ca, firefox
            )
            require(
                launch.call_args.args[0]
                == [firefox, "--no-remote", "--profile", profile, origin],
                "The workstation launched a different profile or origin.",
            )
            client = Browser(s)
            client.login((s.path / "owner-login.txt").read_text().splitlines()[1])
            client.call("session", "DELETE", headers={"Origin": original}, expected=403)
            client.call(
                "session", "DELETE", headers={"X-CSRF-Token": "invalid"}, expected=403
            )
            client.call("workspace")
    finally:
        stop(s)


def qualify_listener_probe():
    with socket.socket() as listener:
        if not WIN:
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        port = listener.getsockname()[1]
        for host in ("127.0.0.1", "0.0.0.0"):
            try:
                probe_listener(host, port)
            except OSError as exc:
                if exc.errno != errno.EADDRINUSE and not (
                    WIN and exc.errno == errno.EACCES
                ):
                    raise
            else:
                raise AssertionError("A live TCP listener passed the port probe.")
        if not WIN:
            with socket.create_connection(("127.0.0.1", port), timeout=3) as client:
                accepted, _ = listener.accept()
                accepted.close()  # Server closes first and retains the TIME_WAIT connection.
                require(
                    client.recv(1) == b"",
                    "Server did not close the qualification connection.",
                )
    probe_listener("127.0.0.1", port)


def verify(port):
    if not 1024 <= port <= 65527:
        raise ValueError("Qualification needs nine consecutive unprivileged ports.")
    qualify_listener_probe()
    with tempfile.TemporaryDirectory(prefix="bv-m2-qualification-") as tmp:
        lab = Path(tmp)
        runtime_dir = lab / "runtime"
        runtime_dir.mkdir()
        if WIN:
            # Pre-existing explicit read permissions must be removed before writing any secret.
            subprocess.run(
                ["icacls", str(runtime_dir), "/grant", "*S-1-5-32-545:(OI)(CI)R"],
                check=True,
                stdout=subprocess.DEVNULL,
            )
        s = init(runtime_dir, lab / "recovery", port)
        with patch.dict(
            os.environ,
            {"PGHOSTADDR": "192.0.2.2", "BV_ENV_SENTINEL": "must-stay-with-parent"},
        ):
            require(
                s.sql_env("bv_worker")["PGHOSTADDR"] == "127.0.0.1"
                and "BV_ENV_SENTINEL" not in s.sql_env("bv_api")
                and "BV_ENV_SENTINEL" not in s.app_env("bv_worker"),
                "Ambient process configuration escaped isolation.",
            )
        try:
            qualify_runtime_start(s)
            password = (s.path / "owner-login.txt").read_text().splitlines()[1]
            original_keys = [s.config["gateway"], s.config["session"], password]
            s.pg_stop()
            require(
                not ready(s), "Readiness reported success while PostgreSQL was stopped."
            )
            s.pg_start()
            poll(lambda: ready(s), bool)
            first = Browser(s)
            first.call("workspace", expected=401)
            first.login(password)
            other = Browser(s)
            other.login(password)
            require(
                other.call("workspace")["catalog"] == [],
                "New instance has unexpected catalog records.",
            )
            first.call("export", expected=400)
            first.call(
                "session", "DELETE", headers={"X-CSRF-Token": "invalid"}, expected=403
            )
            require(
                first.call("contract")["openapi"].startswith("3."),
                "Versioned API contract missing.",
            )
            catalog = {
                "commandId": new_id(),
                "expectedRevision": 0,
                "itemType": "part",
                "itemNumber": "0017",
                "variant": "a",
                "name": "Studio\u2028reference brick",
                "colorId": 0,
                "colorState": "known",
                "condition": "new",
                "completeness": "complete",
                "source": "Operator-created foundation qualification",
                "license": "operator-owned",
                "mapping": "confirmed",
            }
            rec = first.call("catalog", "POST", catalog, expected=(200, 201))
            first.call(
                "catalog",
                "POST",
                {**catalog, "name": "different request"},
                expected=409,
            )
            unknown = {
                **catalog,
                "commandId": new_id(),
                "variant": "b",
                "colorId": None,
                "colorState": "unknown",
                "license": "unknown",
                "mapping": "ambiguous",
            }
            first.call("catalog", "POST", unknown, expected=(200, 201))
            loc = {
                "commandId": new_id(),
                "expectedRevision": 0,
                "code": "Q-01",
                "name": "Qualification shelf",
            }
            # Commit a command from an independent client and replay after restart without its original response.
            other.call("locations", "POST", loc, expected=(200, 201))
            stop(s)
            start(s)
            replay = first.call("locations", "POST", loc, expected=(200, 201))
            require(
                first.call("catalog", "POST", catalog, expected=(200, 201)) == rec,
                "Replay lost receipt identity.",
            )
            ws = other.call("workspace")
            require(
                len(ws["locations"]) == 1
                and len(ws["audit"]) == 3
                and len(ws["catalog"]) == 2,
                "Unexpected data after restart/replay.",
            )
            known = next(r for r in ws["catalog"] if r["variant"] == "a")
            held = next(r for r in ws["catalog"] if r["variant"] == "b")
            require(
                known["itemNumber"] == "0017"
                and known["colorId"] == 0
                and held["colorId"] is None
                and not held["integrationEligible"],
                "Catalog identity or eligibility was lost.",
            )
            exported = first.call("export?scope=reference")
            require(
                len(exported["catalog"]) == 1
                and not ({"audit", "sessions"} & set(exported)),
                "Export exceeded the reference scope.",
            )
            print(
                "TLS trust, two owner sessions, access denial, source rights, replay across restart and scoped export: passed.",
                flush=True,
            )
            (s.path / "pause-worker").touch()
            poll(
                lambda: first.call("diagnostics"),
                lambda r: r["workerState"] == "stale",
                secs=30,
            )
            name = {
                **loc,
                "id": replay["entityId"],
                "commandId": new_id(),
                "expectedRevision": 1,
                "name": "Checked shelf",
            }
            changed = first.call("locations", "POST", name, expected=(200, 201))
            first.call(
                "locations", "POST", {**name, "commandId": new_id()}, expected=409
            )
            diag = "diagnostics/" + changed["correlationId"]
            require(
                first.call(diag)["jobState"] == "pending",
                "Paused worker reported a completed effect.",
            )
            blocked = first.call(
                "locations",
                "POST",
                {**loc, "commandId": new_id(), "code": "Q-02"},
                expected=(200, 201),
            )
            # An unsupported message must be visible and have no effect on source records.
            s.sql(
                f"UPDATE brickvault.jobs SET payload_version=99 WHERE command_id='{blocked['commandId']}';",
                role="bv_migrator",
            )
            (s.path / "pause-worker").unlink()
            poll(lambda: first.call(diag), lambda r: r["jobState"] == "done")
            poll(
                lambda: first.call("diagnostics/" + blocked["correlationId"]),
                lambda r: r["jobState"] == "blocked",
            )
            require(
                s.sql(
                    f"SELECT count(*) FROM brickvault.job_effects WHERE command_id='{changed['commandId']}';"
                )
                == "1",
                "Duplicate worker effect.",
            )
            require(
                s.sql(
                    f"SELECT count(*) FROM brickvault.job_effects WHERE command_id='{blocked['commandId']}';"
                )
                == "0",
                "Incompatible message applied.",
            )
            expected_failure(
                lambda: s.sql(
                    "CREATE TABLE brickvault.disallowed(i int)", role="bv_api"
                )
            )
            expected_failure(
                lambda: s.sql("SELECT name FROM brickvault.catalog", role="bv_worker")
            )
            print(
                "Worker stopped, stale heartbeat visible, restart and single durable effect, version guard and database permissions: passed.",
                flush=True,
            )
            migration_test(s)
            testdb = "bv_m2_test_contract"
            s.create_db(testdb)
            s.migrate(testdb)
            s.grants(testdb)
            testenv = s.app_env("bv_api", testdb)
            testenv["BV_TEST_DB"] = testenv["BV_DB"]
            project = ROOT / "tests/BrickVault.Api.Tests"
            subprocess.run(
                [DOTNET, "restore", project, "--locked-mode"],
                env=testenv,
                cwd=ROOT,
                check=True,
            )
            subprocess.run(
                [DOTNET, "test", "--project", project, "--no-restore", "-c", "Release"],
                env=testenv,
                cwd=ROOT,
                check=True,
            )
            npm = shutil.which("npm.cmd" if os.name == "nt" else "npm")
            subprocess.run(
                [npm, "test"],
                cwd=UI,
                env={
                    **os.environ,
                    "BV_OWNER_PASSWORD": password,
                    "BV_LAB_CA": str(s.ca),
                    "NODE_EXTRA_CA_CERTS": str(s.ca),
                    "BV_PUBLIC_ORIGIN": s.origin,
                },
                check=True,
            )
            with ThreadPoolExecutor(max_workers=2) as pool:
                list(pool.map(lambda _: rotate(Instance(s.path)), range(2)))
            # Recreate the state immediately after the committed config flip and before login publication.
            stop(s)
            staged = s.path / "owner-login.next"
            os.replace(s.path / "owner-login.txt", staged)
            start(s)
            require(
                not staged.exists() and (s.path / "owner-login.txt").exists(),
                "Committed rotation did not recover.",
            )
            other.call("workspace", expected=401)
            basic = base64.b64encode(("operator:" + password).encode()).decode()
            other.call(
                "session",
                "POST",
                headers={"Authorization": "Basic " + basic},
                expected=401,
            )
            first = Browser(s)
            first.login((s.path / "owner-login.txt").read_text().splitlines()[1])
            poll(lambda: first.call("diagnostics"), lambda r: r["pendingJobs"] == 0)
            before = first.call("workspace")
            archive = lab / "recovery" / "qualification.age"
            proof = backup(s, archive)
            require(
                proof["facts"]["catalog"]["rows"] == len(before["catalog"]),
                "Unicode changed the backup row count.",
            )
            key = lab / "recovery" / (s.config["id"] + ".agekey")
            corrupt = lab / "recovery" / "damaged.age"
            data = archive.read_bytes()
            corrupt.write_bytes(data[:-32])
            expected_failure(lambda: restore(s, corrupt, key, "bv_m2_broken"))
            require(
                s.sql(
                    "SELECT count(*) FROM pg_database WHERE datname='bv_m2_broken';",
                    "postgres",
                )
                == "0",
                "Corrupt backup created a restore target.",
            )
            wrong = lab / "recovery" / "different.agekey"
            execute([BIN / ("age-keygen" + EXT), "-o", wrong])
            expected_failure(lambda: restore(s, archive, wrong, "bv_m2_broken"))
            from verify_offhost import verify_offhost

            verify_offhost(s, lab, port + 3, before, Browser)
            qualify_gateway_error(s)
            recipient = s.config["recipient"]
            bad_recipient = recipient[:-1] + ("p" if recipient[-1] != "p" else "q")
            expected_failure(
                lambda: init(
                    lab / "rejected-guest", None, port + 6, recipient=bad_recipient
                )
            )
            require(
                not (lab / "rejected-guest").exists(),
                "An invalid recipient provisioned guest state.",
            )
            rescue = init(lab / "second-runtime", None, port + 6, recipient=recipient)
            require(
                rescue.config["recipient"] == recipient
                and not any(rescue.path.rglob("*.agekey")),
                "The instance did not retain exclusive external recovery key custody.",
            )
            target = "bv_m2_restore_qualified"
            try:
                start(rescue)
                unrelated = Browser(rescue)
                unrelated.login(
                    (rescue.path / "owner-login.txt").read_text().splitlines()[1]
                )
                require(
                    unrelated.lineage != first.lineage,
                    "Fresh instance inherited the source lineage.",
                )
                unrelated.call(
                    "catalog",
                    "POST",
                    catalog,
                    headers={"X-Workspace-Id": first.lineage},
                    expected=409,
                )
                require(
                    unrelated.call("workspace")["catalog"] == [],
                    "Command crossed workspace lineage.",
                )
                stop(rescue)
                rescue.pg_start()
                began = time.monotonic()
                restore(rescue, archive, key, target)
                elapsed = time.monotonic() - began
                expected_failure(lambda: restore(rescue, archive, key, target))
                require(
                    rescue.sql("SELECT count(*) FROM brickvault.sessions;", target)
                    == "0",
                    "Restore revived an owner session.",
                )
                activate(rescue, target)
                start(rescue)
                recovered = Browser(rescue)
                recovered.call("workspace", expected=401)
                recovered.login(
                    (rescue.path / "owner-login.txt").read_text().splitlines()[1]
                )
                require(
                    recovered.lineage == first.lineage,
                    "Faithful restore lost workspace identity.",
                )
                after = recovered.call("workspace")
                require(
                    all(
                        after[k] == before[k] for k in ["locations", "catalog", "audit"]
                    ),
                    "Restored API records differed from source.",
                )
                require(
                    recovered.call("catalog", "POST", catalog, expected=(200, 201))
                    == rec,
                    "Restored command replay differed.",
                )

            finally:
                stop(rescue)
            qualify_guest(rescue, lab)
            for host in (s, rescue):
                sensitive_values = [
                    *original_keys,
                    host.config["gateway"],
                    host.config["session"],
                    (host.path / "owner-login.txt").read_text().splitlines()[1],
                    *host.config["roles"].values(),
                    known["name"],
                ]
                for name in (
                    "api.log",
                    "worker.log",
                    "gateway.log",
                    "postgres.log",
                    "supervisor.log",
                ):
                    log = (host.path / name).read_text(
                        encoding="utf-8", errors="replace"
                    )
                    require(
                        all(value not in log for value in sensitive_values),
                        "Private credential or catalog text appeared in service logs.",
                    )
            print(
                json.dumps(
                    {
                        "qualification": "passed",
                        "restoreSeconds": round(elapsed, 2),
                        "restoredRows": {
                            table: v["rows"] for table, v in proof["facts"].items()
                        },
                        "sourceStopped": True,
                        "separateCluster": True,
                        "signaturesMatched": True,
                        "redacted": True,
                    }
                ),
                flush=True,
            )
        finally:
            stop(s)
