"""Check Caddy's actual gateway log encoder with disposable upstream responses."""

import http.client
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import secrets
import ssl
from threading import Event, Thread
import time
from types import SimpleNamespace
import urllib.error
import urllib.request

from instance import password_hash, private_dir, probe_listener
from runtime import end, start_gateway


def qualify_gateway_log_privacy(lab, port):
    """Exercise successful, interrupted, and unavailable loopback upstream responses."""
    probe_listener("127.0.0.1", port)
    probe_listener("127.0.0.1", port + 1)
    gateway_marker, request_marker, response_marker, query_marker = (
        "fixture-" + secrets.token_hex(12) for _ in range(4)
    )
    folder = private_dir(lab / "gateway-log-fixture")
    fixture = SimpleNamespace(
        path=folder,
        port=port,
        origin=f"https://localhost:{port}",
        gateway_bind="127.0.0.1",
        config={"owner_hash": password_hash("fictional owner"), "gateway": gateway_marker},
        ca=folder / "caddy-store/pki/authorities/local/root.crt",
    )
    observed_interruption = Event()
    forwarded = Event()

    class Backend(BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass

        def do_GET(self):
            # The fixture sends one short response to exercise the gateway's stream warning.
            if self.path.startswith("/interrupted?"):
                if self.headers.get("X-BrickVault-Gateway") == gateway_marker and self.headers.get(
                    "X-Fixture-Request"
                ) == request_marker:
                    forwarded.set()
                self.send_response(200)
                self.send_header("Content-Length", "4096")
                self.send_header("X-Fixture-Response", response_marker)
                self.end_headers()
                self.wfile.write(b"short")
                self.close_connection = True
                observed_interruption.set()
                return
            self.send_response(204)
            self.end_headers()

    gateway = None
    try:
        with HTTPServer(("127.0.0.1", port + 1), Backend) as backend:
            worker = Thread(target=backend.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True)
            worker.start()
            try:
                gateway = start_gateway(fixture)
                deadline = time.monotonic() + 15
                while True:
                    try:
                        context = ssl.create_default_context(cafile=str(fixture.ca))
                        client = urllib.request.build_opener(
                            urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context)
                        )
                        with client.open(fixture.origin + "/healthy", timeout=1) as response:
                            if response.status != 204:
                                raise AssertionError("The synthetic gateway upstream did not return its healthy response.")
                        break
                    except OSError:
                        if gateway.poll() is not None or time.monotonic() >= deadline:
                            raise AssertionError("The private gateway fixture did not become ready.") from None
                        time.sleep(0.1)

                request = urllib.request.Request(
                    fixture.origin + "/interrupted?q=" + query_marker,
                    headers={"X-Fixture-Request": request_marker},
                )
                try:
                    with client.open(request, timeout=3) as response:
                        response.read()
                except (http.client.IncompleteRead, http.client.RemoteDisconnected, ConnectionResetError):
                    pass
                except OSError:
                    raise AssertionError("The synthetic interrupted upstream request failed unexpectedly.") from None
                if not observed_interruption.wait(3) or not forwarded.is_set():
                    raise AssertionError("The synthetic request did not reach the interrupted upstream.")
            finally:
                backend.shutdown()
                worker.join()

        try:
            with client.open(fixture.origin + "/unavailable", timeout=3):
                raise AssertionError("The stopped upstream unexpectedly responded.")
        except urllib.error.HTTPError as fault:
            with fault:
                if fault.code != 502:
                    raise AssertionError("The unavailable upstream did not produce a gateway error.") from None
    finally:
        end(gateway)

    # Classify the observed records without reproducing their contents on failure.
    log = (folder / "gateway.log").read_text(encoding="utf-8")
    if any(marker in log for marker in (gateway_marker, request_marker, response_marker, query_marker)):
        raise AssertionError("The gateway log contains private synthetic fixture data.")
    entries = [json.loads(line) for line in log.splitlines() if line.strip()]
    if not any(
        row.get("logger") == "http.handlers.reverse_proxy"
        and row.get("msg") == "aborting with incomplete response"
        and row.get("level") == "warn"
        and row.get("error")
        and row.get("request", {}).get("method") == "GET"
        for row in entries
    ):
        raise AssertionError("The gateway did not retain the structured interrupted-response warning.")
    if not any(
        row.get("logger", "").startswith("http.log.error")
        and row.get("status") == 502 and row.get("msg")
        for row in entries
    ):
        raise AssertionError("The gateway did not retain the structured upstream error status.")
    print("Gateway log privacy and operational diagnostics: passed.", flush=True)
