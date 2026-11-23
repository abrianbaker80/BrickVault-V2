#Requires -Version 7.6
param([Parameter(Mandatory)][string]$LiteralPath)
$ErrorActionPreference = 'Stop'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$directory = [IO.DirectoryInfo]::new($LiteralPath)
$acl = [IO.FileSystemAclExtensions]::GetAccessControl($directory, [Security.AccessControl.AccessControlSections]::Access)
$acl.SetAccessRuleProtection($true, $false)
foreach ($entry in $acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier])) {
    $acl.RemoveAccessRuleSpecific($entry)
}
$inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inherit, 'None', 'Allow'))
[IO.FileSystemAclExtensions]::SetAccessControl($directory, $acl)
$effective = Get-Acl -LiteralPath $LiteralPath
$rules = @($effective.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
if (-not $effective.AreAccessRulesProtected -or $rules.Count -ne 1 -or $rules[0].IdentityReference -ne $sid -or
    $rules[0].FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl) {
    throw 'Private directory ACL verification failed.'
}
