"""Open the private Workbench with Firefox and a certificate store unique to this lab."""

import argparse
import ctypes as c
import hashlib
import http.client
import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import tempfile

from instance import Instance, child_env, lan_endpoint, private_dir
from runtime import start
from setup import WIN, fetch


def provision(profile, ca, firefox):
    """Create a new NSS SQL database. Never modify a preexisting browser profile."""
    profile = Path(profile).resolve()
    profile.mkdir(mode=0o700, parents=True, exist_ok=False)
    if not WIN:
        # Use the distribution's matched NSS binaries to build Firefox's standard SQL database.
        certutil = shutil.which("certutil")
        if not certutil:
            raise RuntimeError(
                "Install Ubuntu libnss3-tools through the foundation setup."
            )
        database = ["-d", "sql:" + str(profile)]
        subprocess.run(
            [certutil, "-N", "--empty-password", *database], env=child_env(), check=True
        )
        subprocess.run(
            [
                certutil,
                "-A",
                "-n",
                "BrickVault private workspace",
                "-t",
                "C,,",
                "-a",
                "-i",
                ca,
                *database,
            ],
            env=child_env(),
            check=True,
        )
        return
    binary = Path(firefox).resolve()
    library = binary.with_name("nss3.dll")
    der = ssl.PEM_cert_to_DER_cert(Path(ca).read_text(encoding="ascii"))

    # Use the Firefox distribution's own stable NSS API, also used by certutil.
    # Windows resolves NSS companions from the same Firefox installation.
    dll_dir = os.add_dll_directory(str(library.parent))
    try:
        nss = c.CDLL(str(library))
        ptr, integer, string = c.c_void_p, c.c_int, c.c_char_p

        class Item(c.Structure):
            _fields_ = [("type", integer), ("data", string), ("length", c.c_uint)]

        class Trust(c.Structure):
            _fields_ = [(name, c.c_uint) for name in ("ssl", "email", "object")]

        def bind(name, returns, *args):
            proc = getattr(nss, name)
            proc.restype = returns
            proc.argtypes = args
            return proc

        def checked(status):
            if status != 0:
                raise RuntimeError(
                    f"Firefox certificate database error {nss.PR_GetError()}."
                )

        init = bind("NSS_InitReadWrite", integer, string)
        shutdown = bind("NSS_Shutdown", integer)
        db = bind("CERT_GetDefaultCertDB", ptr)
        new_cert = bind(
            "CERT_NewTempCertificate",
            ptr,
            ptr,
            c.POINTER(Item),
            string,
            integer,
            integer,
        )
        destroy = bind("CERT_DestroyCertificate", None, ptr)
        get_slot = bind("PK11_GetInternalKeySlot", ptr)
        free_slot = bind("PK11_FreeSlot", None, ptr)
        need_pin = bind("PK11_NeedUserInit", integer, ptr)
        init_pin = bind("PK11_InitPin", integer, ptr, string, string)
        add = bind("PK11_ImportCert", integer, ptr, ptr, c.c_ulong, string, integer)
        decode = bind("CERT_DecodeTrustString", integer, c.POINTER(Trust), string)
        change = bind("CERT_ChangeCertTrust", integer, ptr, ptr, c.POINTER(Trust))
        checked(init(("sql:" + str(profile)).encode()))
        cert = slot = None
        try:
            handle = db()
            slot = get_slot()
            if not handle or not slot:
                raise RuntimeError(
                    "Firefox could not initialize its private certificate database."
                )
            if need_pin(slot):
                checked(init_pin(slot, None, b""))
            item = Item(0, der, len(der))
            cert = new_cert(handle, c.byref(item), None, 0, 1)
            if not cert:
                raise RuntimeError(
                    "Firefox could not read the instance CA certificate."
                )
            checked(add(slot, cert, 0, b"BrickVault private workspace", 0))
            trust = Trust()
            checked(decode(c.byref(trust), b"C,,"))  # TLS server CA only.
            checked(change(handle, cert, c.byref(trust)))
        finally:
            if cert:
                destroy(cert)
            if slot:
                free_slot(slot)
            checked(shutdown())
    finally:
        dll_dir.close()


def operator_firefox():
    if not WIN:
        found = shutil.which("firefox")
        if not found:
            raise RuntimeError(
                "Install Firefox or use --firefox with the installed executable."
            )
        return Path(found).resolve()
    dedicated = Path(os.environ["LOCALAPPDATA"]) / "BrickVault-V2/firefox/firefox.exe"
    for binary in [
        dedicated,
        Path(os.environ["ProgramFiles"]) / "Mozilla Firefox/firefox.exe",
    ]:
        if binary.is_file():
            return binary
    version = "160.0.1"
    installer = fetch(
        f"https://download-installer.cdn.mozilla.net/pub/firefox/releases/{version}/win64/en-US/Firefox%20Setup%20{version}.exe",
        "768e29619f3463c157f20c5ab1e4d480acfb0d84f1a39ee60ef3151fed91ceb2",
        "sha256",
    )
    subprocess.run(
        [
            installer,
            "/S",
            "/InstallDirectoryPath=" + str(dedicated.parent),
            "/MaintenanceService=false",
            "/DesktopShortcut=false",
            "/StartMenuShortcut=false",
            "/PrivateBrowsingShortcut=false",
            "/RegisterDefaultAgent=false",
            "/TaskbarShortcut=false",
        ],
        check=True,
        env=child_env(),
    )
    if not dedicated.is_file():
        raise RuntimeError(
            "The Firefox installer did not create the dedicated browser."
        )
    return dedicated


def trusted_profile(parent, profile_id, ca_pem, firefox):
    parent = private_dir(parent)
    profile = parent / profile_id
    if not profile.exists():
        with tempfile.TemporaryDirectory(dir=parent) as temp:
            stage = Path(temp) / "profile"
            pinned_ca = Path(temp) / "root.crt"
            pinned_ca.write_text(ca_pem, encoding="ascii")
            provision(stage, pinned_ca, firefox)
            # NSS writes an absolute module path. Firefox regenerates it at the final location.
            (stage / "pkcs11.txt").unlink(missing_ok=True)
            (stage / "user.js").write_text(
                'user_pref("browser.aboutwelcome.enabled", false);\n'
                'user_pref("browser.shell.checkDefaultBrowser", false);\n'
                'user_pref("browser.startup.homepage_override.mstone", "ignore");\n',
                encoding="utf-8",
            )
            # Complete profiles appear atomically. Concurrent launchers can use the winner.
            try:
                stage.rename(profile)
            except OSError:
                if not (profile / "cert9.db").is_file():
                    raise
    if not (profile / "cert9.db").is_file():
        raise RuntimeError("The dedicated Firefox profile is incomplete.")
    return profile


def operator_profile(instance, firefox):
    ca_pem = instance.ca.read_text(encoding="ascii")
    fingerprint = hashlib.sha256(ssl.PEM_cert_to_DER_cert(ca_pem)).hexdigest()
    return trusted_profile(instance.path / "browser", fingerprint, ca_pem, firefox)


def remote_connection(descriptor):
    descriptor = descriptor.resolve()
    route = json.loads(descriptor.read_text(encoding="utf-8-sig"))
    origin = route["origin"]
    hostname, port = lan_endpoint(origin)
    ca_pem = (descriptor.parent / route["ca"]).read_text(encoding="ascii")
    # Convert one certificate back to canonical PEM so extra certificates cannot be imported.
    der = ssl.PEM_cert_to_DER_cert(ca_pem)
    if (
        ca_pem.count("-----BEGIN CERTIFICATE-----") != 1
        or hashlib.sha256(der).hexdigest() != route["caSha256"]
    ):
        raise ValueError(
            "The guest CA does not match the trusted workstation connection."
        )
    ca_pem = ssl.DER_cert_to_PEM_cert(der)
    trust = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    trust.load_verify_locations(cadata=ca_pem)
    connection = http.client.HTTPSConnection(hostname, port, timeout=5, context=trust)
    try:
        connection.request("GET", "/api/v1/session")
        with connection.getresponse() as response:
            if response.status != 401:
                raise RuntimeError(
                    "The selected server did not return the Workbench sign-in boundary."
                )
    finally:
        connection.close()
    profile_id = hashlib.sha256(origin.encode("ascii") + b"\0" + der).hexdigest()
    return origin, profile_id, ca_pem


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    actions = parser.add_subparsers(dest="action", required=True)
    prepare = actions.add_parser(
        "prepare", help="Create a new disposable Firefox test profile."
    )
    prepare.add_argument("--profile", type=Path, required=True)
    prepare.add_argument("--ca", type=Path, required=True)
    prepare.add_argument("--firefox", type=Path, required=True)
    launch = actions.add_parser(
        "open",
        help="Start the initialized private Workbench and open its dedicated Firefox profile.",
    )
    default = Path(os.environ.get("LOCALAPPDATA") or Path.home() / ".local/state")
    launch.add_argument(
        "--state", type=Path, default=default / "BrickVault-V2/foundation"
    )
    launch.add_argument("--firefox", type=Path)
    remote = actions.add_parser(
        "remote", help="Open a server-hosted Workbench using its pinned private CA."
    )
    remote.add_argument("--connection", type=Path, required=True)
    remote.add_argument("--firefox", type=Path)
    args = parser.parse_args()
    if args.action == "prepare":
        provision(args.profile, args.ca, args.firefox)
        return
    if args.action == "remote":
        origin, profile_id, ca_pem = remote_connection(args.connection)
        firefox = args.firefox or operator_firefox()
        profile = trusted_profile(
            args.connection.resolve().parent / "browser", profile_id, ca_pem, firefox
        )
    else:
        instance = Instance(args.state)
        start(instance)
        origin = instance.origin
        firefox = args.firefox or operator_firefox()
        profile = operator_profile(instance, firefox)
    subprocess.Popen(
        [firefox, "--no-remote", "--profile", profile, origin],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        env=child_env(desktop=True),
    )
    print("Opened the private Workbench in its dedicated Firefox profile.")


if __name__ == "__main__":
    main()
