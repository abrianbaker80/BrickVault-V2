"""BrickVault isolated foundation operator commands. Run with --help."""

import argparse
import json
import os
from pathlib import Path

import setup
from instance import Instance, init
from recovery import activate, backup, restore
from runtime import build, operation, ready, rotate, running, start, stop, supervise


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
        ],
    )
    p.add_argument("--state", type=Path, default=default / "foundation")
    p.add_argument("--recovery", type=Path, default=default / "recovery")
    p.add_argument("--port", type=int, default=49420)
    p.add_argument("--file", type=Path)
    p.add_argument("--identity", type=Path)
    p.add_argument("--target")
    args = p.parse_args()
    if args.action == "setup":
        setup.install()
    elif args.action == "build":
        build()
    elif args.action == "init":
        init(args.state, args.recovery, args.port)
        print(
            "Foundation initialized. The initial operator sign-in is in the private owner-login.txt file."
        )
    elif args.action == "verify":
        from verify import verify

        verify(args.port)
    else:
        s = Instance(args.state)
        if args.action == "start":
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
