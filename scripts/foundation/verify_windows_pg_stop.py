"""Windows-only shutdown boundary for a disposable qualification cluster."""
import ctypes
from concurrent.futures import ThreadPoolExecutor, TimeoutError
from contextlib import ExitStack
import json
from pathlib import Path
import tempfile
import _winapi

from instance import execute


def qualify_windows_pg_stop(instance):
    instance.pg_start()
    with ExitStack() as cleanup:
        cleanup.callback(instance.pg_stop)
        server_id = int((instance.cluster / "postmaster.pid").read_text().splitlines()[0])
        server = _winapi.OpenProcess(0x100000, False, server_id)  # SYNCHRONIZE
        cleanup.callback(_winapi.CloseHandle, server)
        info = json.loads(execute(
            ["pwsh", "-NoProfile", "-NonInteractive", "-Command", """
                $ErrorActionPreference='Stop'
                $scope=[Console]::In.ReadToEnd() | ConvertFrom-Json
                $server=Get-Process -Id $scope.server
                $shell=$server.Parent
                if ($server.ProcessName -ine 'postgres' -or $shell.ProcessName -ine 'cmd' -or
                    -not $shell.CommandLine.Replace('\\','/').Contains($scope.log, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Expected the dedicated launcher of this qualification cluster.'
                }
                @{shell=$shell.Id; threads=@($shell.Threads.Id)} | ConvertTo-Json -Compress
            """], content=json.dumps({"server": server_id, "log": (instance.path / "postgres.log").as_posix()})))
        shell = _winapi.OpenProcess(0x100000, False, info["shell"])
        cleanup.callback(_winapi.CloseHandle, shell)

        # Hold only threads of this real pg_ctl launcher, which is waiting for its own postmaster.
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenThread.restype = ctypes.c_void_p
        kernel.OpenThread.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
        for name in ("GetProcessIdOfThread", "SuspendThread", "ResumeThread"):
            fn = getattr(kernel, name)
            fn.restype = ctypes.c_uint32
            fn.argtypes = [ctypes.c_void_p]
        with ThreadPoolExecutor(max_workers=1) as executor:
            with ExitStack() as suspended:
                assert info["threads"]
                for tid in info["threads"]:
                    # QUERY_LIMITED_INFORMATION | SUSPEND_RESUME
                    thread = kernel.OpenThread(0x802, False, tid)
                    if not thread:
                        raise ctypes.WinError(ctypes.get_last_error())
                    suspended.callback(_winapi.CloseHandle, thread)
                    assert kernel.GetProcessIdOfThread(thread) == info["shell"]
                    if kernel.SuspendThread(thread) == 0xffffffff:
                        raise ctypes.WinError(ctypes.get_last_error())
                    suspended.callback(kernel.ResumeThread, thread)
                stopping = executor.submit(instance.pg_stop)
                assert _winapi.WaitForSingleObject(server, 20000) == 0
                assert not (instance.cluster / "postmaster.pid").exists()
                try:
                    stopping.result(timeout=1)
                except TimeoutError:
                    pass
                else:
                    raise AssertionError("Stop returned while its log-owning launcher was held.")
            stopping.result(timeout=10)
        assert _winapi.WaitForSingleObject(shell, 0) == 0

        with tempfile.TemporaryDirectory(dir=instance.path.parent) as target:
            promoted = Path(target) / "instance"
            # A separate reader's non-delete-sharing handle must still surface a real promotion failure.
            with (instance.path / "postgres.log").open("rb"):
                try:
                    instance.path.rename(promoted)
                except PermissionError as err:
                    assert err.winerror == 5
                else:
                    promoted.rename(instance.path)
                    raise AssertionError("An external file handle unexpectedly permitted promotion.")
            instance.path.rename(promoted)
            promoted.rename(instance.path)
    print("PostgreSQL and its Windows log launcher exited before promotion. External access denial remained visible.", flush=True)
