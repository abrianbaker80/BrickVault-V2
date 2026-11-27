param(
    [Parameter(Mandatory)][string]$PgCtl,
    [Parameter(Mandatory)][string]$Cluster,
    [Parameter(Mandatory)][string]$LogFile
)
$ErrorActionPreference = 'Stop'

# pg_ctl -w can return after the PID file disappears while its cmd.exe still owns postgres.log.
# Pin process handles before requesting shutdown. A later PID lookup could identify a different process.
$pidFile = Get-Content -LiteralPath (Join-Path $Cluster 'postmaster.pid') -TotalCount 2
if ($pidFile.Count -ne 2 -or $pidFile[1].Replace('\', '/') -ine $Cluster.Replace('\', '/')) {
    throw 'The PostgreSQL PID file does not identify this cluster.'
}
$server = Get-Process -Id ([int]$pidFile[0])
$parent = $null
try {
    $null = $server.Handle
    $dataPath = [regex]::Escape($Cluster.Replace('\', '/'))
    $logPath = [regex]::Escape($LogFile.Replace('\', '/'))
    if ($server.ProcessName -ine 'postgres') {
        throw 'The Windows process does not match this PostgreSQL instance.'
    }
    $parent = $server.Parent
    $managedShell = $false
    if ($null -ne $parent -and $parent.ProcessName -ieq 'cmd' -and $parent.StartTime -le $server.StartTime) {
        $null = $parent.Handle
        # Only join the single-command launcher for this exact executable, data directory, and logfile.
        $binary = [regex]::Escape($server.Path.Replace('\', '/'))
        $shell = [regex]::Escape($parent.Path.Replace('\', '/'))
        # pg_ctl may append PostgreSQL options. Exclude shell operators outside quoted arguments.
        $arguments = '(?:\s+(?:"[^"]*"|[^\s&|<>()^"]+))*'
        $pattern = '^"?{0}"?\s+(?:/[DS]\s+)*/C\s+"?"?{1}"?\s+-D\s+"?{2}"?{4}\s*<\s*"?nul"?\s*>>\s*"?{3}"?\s+2>&1"?\s*$'
        $command = [string]$parent.CommandLine
        $managedShell = $command.Replace('\', '/') -match ($pattern -f $shell, $binary, $dataPath, $logPath, $arguments)
    }

    $clock = [Diagnostics.Stopwatch]::StartNew()
    & $PgCtl stop -D $Cluster -m fast -w -t 60
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $processes = if ($managedShell) { $server, $parent } else { $server }
    foreach ($process in $processes) {
        $remaining = [int][Math]::Max(0, 60000L - $clock.ElapsedMilliseconds)
        if (-not $process.WaitForExit($remaining)) {
            throw 'PostgreSQL stopped accepting connections but its Windows process or log launcher did not exit.'
        }
    }
} finally {
    if ($null -ne $parent) { $parent.Dispose() }
    $server.Dispose()
}
