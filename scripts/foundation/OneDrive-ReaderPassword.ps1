#Requires -Version 7.6
param([Parameter(Mandatory)][string]$ProtectedFile, [switch]$Initialize)
$ErrorActionPreference = 'Stop'
$scope = [Security.Cryptography.DataProtectionScope]::CurrentUser
if ($Initialize) {
    $random = [Security.Cryptography.RandomNumberGenerator]::GetBytes(48)
    try {
        $sealed = [Security.Cryptography.ProtectedData]::Protect($random, $null, $scope)
        $file = [IO.File]::Open($ProtectedFile, [IO.FileMode]::CreateNew)
        try { $file.Write($sealed); $file.Flush($true) } finally { $file.Dispose() }
    } finally { [Array]::Clear($random) }
} else {
    # rclone invokes this loader directly through --password-command.
    $random = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($ProtectedFile), $null, $scope)
    try { [Console]::Out.WriteLine([Convert]::ToBase64String($random)) } finally { [Array]::Clear($random) }
}
