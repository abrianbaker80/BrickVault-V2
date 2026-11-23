"""Supervise the private TLS gateway, API and worker for a disposable or operator-owned lab."""

import base64
import hashlib
import hmac
import contextlib
import os
from pathlib import Path
import secrets
import shutil
import signal
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

from instance import (
    API,
    BIN,
    DOTNET,
    EXT,
    NO_WINDOW,
    Instance,
    RELEASE,
    UI,
    WIN,
    child_env,
    password_hash,
    write_private,
)


def build():
    env = {
        **os.environ,
        "DOTNET_ROOT": str(DOTNET.parent),
        "DOTNET_NOLOGO": "1",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
    }
    npm = shutil.which("npm.cmd" if WIN else "npm")
    if not npm:
        raise RuntimeError("Install Node 24 with npm.")
    subprocess.run([npm, "ci", "--no-fund"], cwd=UI, check=True)
    subprocess.run([npm, "run", "build"], cwd=UI, check=True)
    subprocess.run([DOTNET, "restore", "--locked-mode"], cwd=API, env=env, check=True)
    RELEASE.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=RELEASE.parent) as scratch:
        stage = Path(scratch) / "release"
        subprocess.run(
            [DOTNET, "publish", "-c", "Release", "--no-restore", "-o", stage],
            cwd=API,
            env=env,
            check=True,
        )
        shutil.copytree(UI / "dist", stage / "wwwroot", dirs_exist_ok=True)
        old = Path(scratch) / "previous"
        if RELEASE.exists():
            RELEASE.rename(old)
        try:
            stage.rename(RELEASE)
        except Exception:
            if old.exists():
                old.rename(RELEASE)
            raise


def caddyfile(s):
    storage = (s.path / "caddy-store").as_posix()
    if any(c in storage for c in ['"', "\n", "\r", "{", "}"]):
        raise ValueError("Unsupported characters in private instance path.")
    return f"""{{
    admin off
    auto_https disable_redirects
    skip_install_trust
    storage file_system {{
        root "{storage}"
    }}
    servers {{
        protocols h1 h2
    }}
}}
{s.origin} {{
    bind 127.0.0.1
    tls internal
    @ready {{
        method GET
        path /internal/ready
        header Authorization "Bearer {{$BV_READY_TOKEN}}"
    }}
    handle @ready {{
        reverse_proxy 127.0.0.1:{s.port+1} {{
            header_up -Authorization
            header_up -X-BrickVault-Actor
            header_up X-BrickVault-Gateway {{$BV_GATEWAY_TOKEN}}
            header_up X-BrickVault-Readiness {{$BV_GATEWAY_TOKEN}}
        }}
    }}
    @signin {{
        method POST
        path /api/v1/session
    }}
    handle @signin {{
        header -WWW-Authenticate
        basic_auth {{
            operator {{$BV_OWNER_HASH}}
        }}
        reverse_proxy 127.0.0.1:{s.port+1} {{
            header_up -Authorization
            header_up X-BrickVault-Actor {{http.auth.user.id}}
            header_up X-BrickVault-Gateway {{$BV_GATEWAY_TOKEN}}
        }}
    }}
    handle {{
        reverse_proxy 127.0.0.1:{s.port+1} {{
            header_up -Authorization
            header_up -X-BrickVault-Actor
            header_up -X-BrickVault-Readiness
            header_up X-BrickVault-Gateway {{$BV_GATEWAY_TOKEN}}
        }}
    }}
}}
"""


@contextlib.contextmanager
def exclusive(file):
    with file.open("a+b") as f:
        if f.tell() == 0:
            f.write(b"0")
            f.flush()
        f.seek(0)
        if WIN:
            import msvcrt

            msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl

            fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield


def running(s):
    try:
        with exclusive(s.path / "supervisor.lock"):
            return False
    except OSError:
        return True


def launch(args, cwd, env, log):
    with log.open("ab", buffering=0) as out:
        return subprocess.Popen(
            args,
            cwd=cwd,
            env=env,
            stdin=subprocess.DEVNULL,
            stdout=out,
            stderr=out,
            creationflags=NO_WINDOW,
            close_fds=True,
        )


def end(proc):
    if proc is not None and proc.poll() is None:
        proc.terminate()
        try:
            proc.wait(8)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(8)


def readiness_token(s):
    return hmac.digest(
        s.config["gateway"].encode("ascii"), b"foundation/readiness", "sha256"
    ).hex()


def start_gateway(s):
    config = s.path / "Caddyfile"
    write_private(config, caddyfile(s))
    gateway_env = {
        **child_env(),
        "BV_READY_TOKEN": readiness_token(s),
        "BV_OWNER_HASH": s.config["owner_hash"],
        "BV_GATEWAY_TOKEN": s.config["gateway"],
        "XDG_DATA_HOME": str(s.path / "caddy-data"),
        "XDG_CONFIG_HOME": str(s.path / "caddy-config"),
        "APPDATA": str(s.path / "caddy-config"),
    }
    return launch(
        [
            BIN / ("caddy" + EXT),
            "run",
            "--config",
            config,
            "--adapter",
            "caddyfile",
        ],
        s.path,
        gateway_env,
        s.path / "gateway.log",
    )


def supervise(s):
    with exclusive(s.path / "supervisor.lock"):
        stopfile = s.path / "stop.request"
        stopfile.unlink(missing_ok=True)

        def request_stop(*_):
            stopfile.touch()

        signal.signal(signal.SIGTERM, request_stop)
        signal.signal(signal.SIGINT, request_stop)
        api = worker = gateway = None
        try:
            s.pg_start()
            release = s.path / "release"
            dll = release / "BrickVault.Api.dll"
            api = launch(
                [DOTNET, dll, "serve"], release, s.app_env("bv_api"), s.path / "api.log"
            )

            def start_worker():
                return launch(
                    [DOTNET, dll, "worker"],
                    release,
                    s.app_env("bv_worker"),
                    s.path / "worker.log",
                )

            worker = start_worker()
            gateway = start_gateway(s)
            while not stopfile.exists():
                if api.poll() is not None or gateway.poll() is not None:
                    raise RuntimeError(
                        "An application or gateway process exited; inspect the private service logs."
                    )
                if (s.path / "pause-worker").exists():
                    end(worker)
                    worker = None
                elif not worker or worker.poll() is not None:
                    worker = start_worker()
                time.sleep(0.15)
        finally:
            end(gateway)
            end(api)
            end(worker)
            s.pg_stop()
            stopfile.unlink(missing_ok=True)


def ready(s):
    try:
        context = ssl.create_default_context(cafile=str(s.ca))
        request = urllib.request.Request(
            s.origin + "/internal/ready",
            headers={"Authorization": "Bearer " + readiness_token(s)},
        )
        opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}),
            urllib.request.HTTPSHandler(context=context),
        )
        with opener.open(request, timeout=3) as response:
            return response.status == 204
    except (OSError, ValueError):
        return False


def _start(s):
    proc = None
    if not running(s):
        with (s.path / "supervisor.log").open("ab") as out:
            proc = subprocess.Popen(
                [
                    sys.executable,
                    Path(__file__).with_name("manage.py"),
                    "supervise",
                    "--state",
                    s.path,
                ],
                env=child_env(),
                stdin=subprocess.DEVNULL,
                stdout=out,
                stderr=out,
                close_fds=True,
                creationflags=NO_WINDOW,
                start_new_session=not WIN,
            )
    for _ in range(160):
        if proc and proc.poll() is not None:
            raise RuntimeError(
                "Foundation failed to start. Inspect supervisor.log, api.log, worker.log and gateway.log in the private state directory."
            )
        if running(s) and ready(s):
            return
        time.sleep(0.25)
    raise RuntimeError("Private foundation did not become ready.")


def _stop(s):
    if running(s):
        (s.path / "stop.request").touch()
        for _ in range(160):
            if not running(s):
                break
            time.sleep(0.25)
        else:
            raise RuntimeError("Timed out waiting for private foundation to stop.")
    s.pg_stop()


@contextlib.contextmanager
def operation(s):
    """Serialize operator transitions and complete any committed login rotation."""
    deadline = time.monotonic() + 120
    with contextlib.ExitStack() as stack:
        while True:
            try:
                stack.enter_context(exclusive(s.path / "operator.lock"))
                break
            except (BlockingIOError, PermissionError):
                if time.monotonic() >= deadline:
                    raise RuntimeError(
                        "Another foundation operation is still in progress."
                    )
                time.sleep(0.1)
        current = Instance(s.path)
        s.config, s.database = current.config, current.database
        pending = s.path / "owner-login.next"
        if pending.exists():
            if hashlib.sha256(pending.read_bytes()).hexdigest() == s.config.get(
                "loginDigest"
            ):
                os.replace(pending, s.path / "owner-login.txt")
            else:
                pending.unlink()
        yield


def start(s):
    with operation(s):
        _start(s)


def stop(s):
    with operation(s):
        _stop(s)


def rotate(s):
    with operation(s):
        was_running = running(s)
        if was_running:
            _stop(s)
        owner = secrets.token_urlsafe(24)
        staged = s.path / "owner-login.next"
        write_private(staged, "operator\n" + owner + "\n")
        s.config["owner_hash"] = password_hash(owner)
        s.config["session"] = base64.b64encode(os.urandom(32)).decode()
        s.config["gateway"] = secrets.token_urlsafe(40)
        s.config["loginDigest"] = hashlib.sha256(staged.read_bytes()).hexdigest()
        s.save()
        os.replace(staged, s.path / "owner-login.txt")
        if was_running:
            _start(s)
