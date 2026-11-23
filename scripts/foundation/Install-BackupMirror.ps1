#Requires -Version 7.6
param(
    [Parameter(Mandatory)][string]$ConfigPath,
    [Parameter(Mandatory)][string]$ClientPath,
    [string]$PythonPath = (Get-Command pythonw.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source)
)

$ErrorActionPreference = 'Stop'
$taskName = 'BrickVault V2 Backup Catch-up'
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$description = 'BrickVault V2 verifies missing encrypted recovery points in the private workstation mirror.'
$python = (Resolve-Path -LiteralPath $PythonPath).Path
$client = (Resolve-Path -LiteralPath $ClientPath).Path
$config = (Resolve-Path -LiteralPath $ConfigPath).Path
if ((Split-Path -Leaf $client) -ne 'backup_mirror.py') { throw 'Select the installed V2 mirror client.' }
if ((Split-Path -Leaf $python) -ne 'pythonw.exe') { throw 'Select the Python windowless interpreter.' }
$existing = Get-ScheduledTask -TaskPath '\' -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing -and $existing.Description -ne $description) { throw 'Another task already owns the V2 task name.' }

$action = New-ScheduledTaskAction -Execute $python -Argument ('"{0}" sync --config "{1}"' -f $client, $config)
$daily = New-ScheduledTaskTrigger -Daily -At '03:05'
$logon = New-ScheduledTaskTrigger -AtLogOn -User $owner
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 4) `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 15)
$principal = New-ScheduledTaskPrincipal -UserId $owner -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskPath '\' -TaskName $taskName -Description $description -Action $action -Trigger @($daily, $logon) `
    -Settings $settings -Principal $principal -Force | Select-Object TaskName, State
