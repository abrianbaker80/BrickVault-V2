"""Exercise the normal HTTPS service, worker, migrations and encrypted restore in an isolated lab."""

import base64
import http.cookiejar
import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

from instance import BIN, DOTNET, EXT, ROOT, UI, execute, init
from recovery import activate, backup, restore
from runtime import rotate, start, stop


def require(ok, reason):
    if not ok:
        raise AssertionError(reason)


class Browser:
    def __init__(self, s):
        self.s = s
        self.token = None
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
    s.sql("DROP TABLE brickvault.job_effects;", db, "bv_migrator")
    s.migrate(db)
    s.grants(db)
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


def verify(port):
    with tempfile.TemporaryDirectory(prefix="bv-m2-qualification-") as tmp:
        lab = Path(tmp)
        s = init(lab / "runtime", lab / "recovery", port)
        try:
            start(s)
            password = (s.path / "owner-login.txt").read_text().splitlines()[1]
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
                "name": "Studio reference brick",
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
                    "BV_PUBLIC_ORIGIN": s.origin,
                },
                check=True,
            )
            rotate(s)
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
            stop(s)
            rescue = init(lab / "second-runtime", lab / "second-recovery", port + 6)
            target = "bv_m2_restore_qualified"
            try:
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
                stop(rescue)
            for path in [
                s.path / "api.log",
                s.path / "worker.log",
                s.path / "gateway.log",
                s.path / "postgres.log",
            ]:
                log = path.read_text(encoding="utf-8", errors="replace")
                for sensitive in [
                    password,
                    s.config["gateway"],
                    s.config["session"],
                    *s.config["roles"].values(),
                    known["name"],
                ]:
                    require(
                        sensitive not in log,
                        "Private credential or catalog text appeared in service logs.",
                    )
        finally:
            stop(s)
