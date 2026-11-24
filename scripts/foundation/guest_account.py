"""Install or check the persistent Workbench service identity on stock Ubuntu 24.04."""

import argparse
import grp
import os
from pathlib import Path
import pwd
import stat
import subprocess

NAME = "brickvault"
HOME = Path("/var/lib/brickvault-v2")


def check_identity(account, group):
    # Ubuntu 24.04 systemd reserves 1 through 999 for non-root system identities.
    if not (1 <= account.pw_uid < 1000 and 1 <= account.pw_gid < 1000):
        raise ValueError(
            "Use a non-root system UID and GID for brickvault. See the hosted account migration procedure."
        )
    if group.gr_gid != account.pw_gid or Path(account.pw_dir) != HOME:
        raise ValueError(
            "Use the brickvault primary group and /var/lib/brickvault-v2 home."
        )
    shell = Path(account.pw_shell)
    if shell.resolve() not in (
        Path("/bin/sh").resolve(),
        Path("/bin/bash").resolve(),
    ) or not os.access(shell, os.X_OK):
        raise ValueError(
            "Use an executable sh or bash shell for the restricted SSH capture command."
        )


def check_home(account):
    home = HOME.lstat()
    if (
        not stat.S_ISDIR(home.st_mode)
        or home.st_uid != account.pw_uid
        or home.st_gid != account.pw_gid
        or stat.S_IMODE(home.st_mode) != 0o700
    ):
        raise ValueError(
            "The private home must be a brickvault-owned directory with mode 0700."
        )


def install():
    if os.geteuid() != 0:
        raise ValueError("Install the service account as root.")
    try:
        account = pwd.getpwnam(NAME)
    except KeyError:
        if os.path.lexists(HOME):
            raise ValueError(
                "Existing service data requires the hosted account migration procedure."
            )
        subprocess.run(
            [
                "/usr/sbin/useradd",
                "--system",
                "--user-group",
                "--create-home",
                "--home-dir",
                HOME,
                "--shell",
                "/bin/sh",
                "--password",
                "!",
                "-K",
                "HOME_MODE=0700",
                NAME,
            ],
            check=True,
        )
        account = pwd.getpwnam(NAME)
    check_identity(account, grp.getgrnam(NAME))
    check_home(account)
    status = subprocess.check_output(
        ["/usr/bin/passwd", "--status", NAME],
        env={**os.environ, "LC_ALL": "C"},
        text=True,
    ).split()
    if status[:2] != [NAME, "L"]:
        raise ValueError(
            "Lock the brickvault password before commissioning the capture key."
        )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["install", "check"])
    args = parser.parse_args()
    try:
        if args.action == "install":
            install()
        else:
            account = pwd.getpwnam(NAME)
            check_identity(account, grp.getgrnam(NAME))
            if (os.geteuid(), os.getegid()) != (account.pw_uid, account.pw_gid):
                raise ValueError(
                    "The Workbench unit must run as brickvault:brickvault."
                )
            check_home(account)
    except (KeyError, ValueError, OSError, subprocess.CalledProcessError) as exc:
        parser.exit(1, f"BrickVault service account check failed: {exc}\n")


if __name__ == "__main__":
    main()
