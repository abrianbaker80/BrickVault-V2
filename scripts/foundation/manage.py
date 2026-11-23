"""BrickVault isolated foundation operator commands. Run with --help."""

import argparse
import json
import os
from pathlib import Path

import setup
from instance import Instance, init
from recovery import activate, backup, restore
from runtime import (
    build,
    configure_lan,
    operation,
    ready,
    rotate,
    running,
    start,
    stop,
    supervise,
)


def main():
    default = (
        Path(os.environ.get("LOCALAPPDATA") or Path.home() / ".local/state")
        / "BrickVault-V2"
    )
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument(
        "action",
        choices=[
            "setup",
            "build",
            "init",
            "start",
            "stop",
            "status",
            "backup",
            "restore",
            "rotate",
            "supervise",
            "verify",
            "activate",
            "configure-lan",
        ],
    )
    p.add_argument("--state", type=Path, default=default / "foundation")
    keys = p.add_mutually_exclusive_group()
    keys.add_argument("--recovery", type=Path)
    keys.add_argument(
        "--recipient", help="External public X25519 age backup recipient."
    )
    p.add_argument("--port", type=int, default=49420)
    p.add_argument(
        "--lan-origin",
        help="Canonical HTTPS origin for the guest's private LAN gateway.",
    )
    p.add_argument("--file", type=Path)
    p.add_argument("--identity", type=Path)
    p.add_argument("--target")
    args = p.parse_args()
    if args.recipient is not None and args.action != "init":
        p.error("--recipient applies to init")
    if args.lan_origin is not None and args.action not in ("init", "configure-lan"):
        p.error("--lan-origin applies to init or configure-lan")
    if args.action == "setup":
        setup.install()
    elif args.action == "build":
        build()
    elif args.action == "init":
        recovery = (
            None
            if args.recipient is not None
            else args.recovery or default / "recovery"
        )
        init(
            args.state,
            recovery,
            args.port,
            lan_origin=args.lan_origin,
            recipient=args.recipient,
        )
        print(
            "Foundation initialized. The initial operator sign-in is in the private owner-login.txt file."
        )
    elif args.action == "verify":
        from verify import verify

        verify(args.port)
    else:
        s = Instance(args.state)
        if args.action == "configure-lan":
            if not args.lan_origin:
                p.error("configure-lan requires --lan-origin")
            configure_lan(s, args.lan_origin)
            print("Private LAN gateway configured. Start the instance to apply it.")
        elif args.action == "start":
            start(s)
            print("Private Workbench ready at " + s.origin)
        elif args.action == "stop":
            stop(s)
        elif args.action == "status":
            print(
                json.dumps(
                    {"origin": s.origin, "running": running(s), "ready": ready(s)}
                )
            )
        elif args.action == "rotate":
            rotate(s)
            print("Operator login and session credentials rotated.")
        elif args.action == "supervise":
            supervise(s)
        elif args.action == "backup":
            if not args.file:
                p.error("backup requires --file")
            with operation(s):
                backup(s, args.file)
            print("Encrypted backup written.")
        elif args.action == "activate":
            if not args.target:
                p.error("activate requires --target")
            activate(s, args.target)
            print("Verified recovery selected. Start this instance to sign in.")
        elif args.action == "restore":
            if not (args.file and args.target and args.identity):
                p.error("restore requires --file, --identity and --target")
            with operation(s):
                online = running(s)
                if not online:
                    s.pg_start()
                try:
                    restore(s, args.file, args.identity, args.target)
                finally:
                    if not online:
                        s.pg_stop()
            print("Isolated restore verified. Active database selection preserved.")


if __name__ == "__main__":
    main()
