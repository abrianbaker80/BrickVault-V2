"""Install pinned private foundation tools (Windows or Ubuntu x64)."""

import hashlib
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[2]
TOOLS = ROOT / ".tools"
BIN = TOOLS / "foundation" / "bin"
WIN = os.name == "nt"
EXT = ".exe" if WIN else ""
SDK = "10.0.404"
PG = "18.7"
CADDY = "2.11.9"
AGE = "1.3.2"


def fetch(url, checksum, algorithm):
    cache = TOOLS / "downloads"
    cache.mkdir(parents=True, exist_ok=True)
    target = cache / url.rsplit("/", 1)[1]

    def matches(path):
        with path.open("rb") as src:
            return hashlib.file_digest(src, algorithm).hexdigest() == checksum

    if target.is_file() and matches(target):
        return target
    with tempfile.NamedTemporaryFile(dir=cache, delete=False) as dest:
        temp = Path(dest.name)
    try:
        with urllib.request.urlopen(url, timeout=120) as response, temp.open(
            "wb"
        ) as dest:
            shutil.copyfileobj(response, dest)
        if not matches(temp):
            raise RuntimeError("Download checksum failed: " + target.name)
        os.replace(temp, target)
    finally:
        temp.unlink(missing_ok=True)
    return target


def extract_binaries(archive, names):
    BIN.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=BIN.parent) as tmp:
        if WIN:
            with zipfile.ZipFile(archive) as z:
                z.extractall(tmp)
        else:
            with tarfile.open(archive) as z:
                z.extractall(tmp, filter="data")
        for name in names:
            (source,) = (
                entry for entry in Path(tmp).rglob(name + EXT) if entry.is_file()
            )
            shutil.copy2(source, BIN / source.name)


def install_linux_pg():
    prefix = TOOLS / "foundation/pgsql"
    pg = prefix / "bin/postgres"
    packages = [
        "build-essential",
        "bison",
        "flex",
        "zlib1g-dev",
        "libssl-dev",
        "libnss3-tools",
    ]
    status = subprocess.run(
        ["dpkg-query", "-W", "-f=${db:Status-Status}\\n", *packages],
        capture_output=True,
        text=True,
    )
    if status.returncode or status.stdout.splitlines() != ["installed"] * len(packages):
        elevate = [] if os.geteuid() == 0 else ["sudo"]
        subprocess.run([*elevate, "apt-get", "update", "-qq"], check=True)
        subprocess.run(
            [
                *elevate,
                "apt-get",
                "install",
                "-y",
                "--no-install-recommends",
                *packages,
            ],
            check=True,
        )
    if pg.exists() and subprocess.check_output(
        [pg, "--version"], text=True
    ).strip().endswith(" " + PG):
        return
    archive = fetch(
        f"https://ftp.postgresql.org/pub/source/v{PG}/postgresql-{PG}.tar.bz2",
        "441dfae4280cae3e0425e5f876f80559fb0731c140f1aae22b3e5a54d0411bfd",
        "sha256",
    )
    TOOLS.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=TOOLS) as tmp:
        with tarfile.open(archive) as source:
            source.extractall(tmp, filter="data")
        cwd = Path(tmp) / ("postgresql-" + PG)
        subprocess.run(
            [
                "./configure",
                "--prefix=" + str(prefix),
                "--without-icu",
                "--without-readline",
                "--with-openssl",
            ],
            cwd=cwd,
            check=True,
        )
        subprocess.run(
            ["make", "-s", "-j", str(min(os.cpu_count() or 1, 6))], cwd=cwd, check=True
        )
        subprocess.run(["make", "-s", "install"], cwd=cwd, check=True)


def install():
    if platform.machine().lower() not in ("amd64", "x86_64") or not (
        WIN or sys.platform.startswith("linux")
    ):
        raise SystemExit(
            "This foundation qualification supports Windows x64 and Ubuntu x64."
        )
    suffix = "win-x64.zip" if WIN else "linux-x64.tar.gz"
    expected = (
        "330ddd2665b43cd2e428216a0d8ea1ac7bb19b5434fdc530e62010dc3b52823a620bf95489d628e1ed6f5122401793ee4887cca4a019122361a1231497f632ee"
        if WIN
        else "577d0eb17fec33d029104c5c63a1ab0accc3e28776274abef2218dbac5af18d2109b5406d26b31d76f5c245215e3ba0926a94c20246c672c41847c9e1eb1dc50"
    )
    dotnet = TOOLS / "dotnet" / ("dotnet" + EXT)
    if (
        not dotnet.exists()
        or subprocess.run(
            [dotnet, "--version"], cwd=ROOT, capture_output=True, text=True
        ).stdout.strip()
        != SDK
    ):
        archive = fetch(
            f"https://builds.dotnet.microsoft.com/dotnet/Sdk/{SDK}/dotnet-sdk-{SDK}-{suffix}",
            expected,
            "sha512",
        )
        dotnet.parent.mkdir(parents=True, exist_ok=True)
        if WIN:
            with zipfile.ZipFile(archive) as z:
                z.extractall(dotnet.parent)
        else:
            with tarfile.open(archive) as z:
                z.extractall(dotnet.parent, filter="data")
    if WIN:
        postgres = TOOLS / "foundation" / "pgsql" / "bin" / "postgres.exe"
        if not postgres.exists() or PG not in subprocess.check_output(
            [postgres, "--version"], text=True
        ):
            archive = fetch(
                "https://get.enterprisedb.com/postgresql/postgresql-18.7-1-windows-x64-binaries.zip",
                "ead0bbc27fb678fa24dcf86b738981323042abd073c744103bf7f3aed23f9a33",
                "sha256",
            )
            with zipfile.ZipFile(archive) as z:
                for entry in z.infolist():
                    if entry.filename.startswith(
                        ("pgsql/bin/", "pgsql/share/", "pgsql/lib/")
                    ):
                        z.extract(entry, TOOLS / "foundation")
    else:
        install_linux_pg()
    caddy_file = f"caddy_{CADDY}_" + (
        "windows_amd64.zip" if WIN else "linux_amd64.tar.gz"
    )
    extract_binaries(
        fetch(
            f"https://github.com/caddyserver/caddy/releases/download/v{CADDY}/{caddy_file}",
            (
                "ad93112d335ee687100c4c9f11bced16ca590377976b7fe7883882b9e797e472"
                if WIN
                else "5b8cb7faf8726a2028ad96a66e54e6e81e8f361bc6175e5d22bbd997cc5177fb"
            ),
            "sha256",
        ),
        ["caddy"],
    )
    age_file = f"age-v{AGE}-" + ("windows-amd64.zip" if WIN else "linux-amd64.tar.gz")
    extract_binaries(
        fetch(
            f"https://github.com/FiloSottile/age/releases/download/v{AGE}/{age_file}",
            (
                "f48d8f8f9ebe903ab5027ed067652f2cc1db94bc206976430133b905dcd8e8c7"
                if WIN
                else "cbe24006683f8eb669266162894b9a522a1af52f2665fbc63a4bb032ed26ac10"
            ),
            "sha256",
        ),
        ["age", "age-keygen"],
    )
    print("Foundation tools ready.")


if __name__ == "__main__":
    install()
