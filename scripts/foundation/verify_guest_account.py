"""Check the hosted service identity boundary and refusal to adopt ordinary accounts."""

from types import SimpleNamespace
import unittest
from unittest.mock import patch

import guest_account as service


class GuestAccountTests(unittest.TestCase):
    def account(self, uid, gid):
        return SimpleNamespace(
            pw_uid=uid, pw_gid=gid, pw_dir=str(service.HOME), pw_shell="/bin/sh"
        )

    def test_reserved_ids_and_forced_command_shell(self):
        group = SimpleNamespace(gr_gid=999)
        service.check_identity(self.account(999, 999), group)
        for uid, gid in [(0, 999), (1000, 999), (999, 0), (999, 1000), (999, 998)]:
            with self.subTest(uid=uid, gid=gid), self.assertRaises(ValueError):
                service.check_identity(self.account(uid, gid), group)
        account = self.account(999, 999)
        account.pw_shell = "/usr/sbin/nologin"
        with self.assertRaises(ValueError):
            service.check_identity(account, group)

    def test_installer_rejects_existing_regular_account_before_any_change(self):
        with (
            patch.object(service.os, "geteuid", return_value=0),
            patch.object(
                service.pwd, "getpwnam", return_value=self.account(1001, 1001)
            ),
            patch.object(
                service.grp, "getgrnam", return_value=SimpleNamespace(gr_gid=1001)
            ),
            patch.object(service.subprocess, "run") as command,
        ):
            with self.assertRaisesRegex(ValueError, "system UID"):
                service.install()
            command.assert_not_called()


if __name__ == "__main__":
    unittest.main()
