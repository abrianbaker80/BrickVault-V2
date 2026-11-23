#Requires -Version 7.6

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$repo = Split-Path -Parent $PSScriptRoot
$toolchain = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'toolchain.json') -Raw | ConvertFrom-Json
$gitleaks = $toolchain.gitleaks

if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'M0 setup supports x64 Windows and Linux.'
}

if ($IsWindows) {
    $asset = $gitleaks.windows
}
elseif ($IsLinux) {
    $asset = $gitleaks.linux
}
else {
    throw 'M0 setup supports x64 Windows and Linux.'
}

git --version
Write-Host "PowerShell $($PSVersionTable.PSVersion)"

$toolDirectory = Join-Path $repo ".tools/gitleaks-$($gitleaks.version)"
$executable = Join-Path $toolDirectory $asset.executable
if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or
    (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $asset.executableSha256) {
    $download = New-TemporaryFile
    $extracted = "$($download.FullName).expanded"
    $extractedBinary = Join-Path $extracted $asset.executable
    try {
        $uri = "https://github.com/gitleaks/gitleaks/releases/download/v$($gitleaks.version)/$($asset.asset)"
        Invoke-WebRequest -Uri $uri -OutFile $download.FullName
        $actualHash = (Get-FileHash -LiteralPath $download.FullName -Algorithm SHA256).Hash
        if ($actualHash -ne $asset.sha256) {
            throw "SHA-256 mismatch for $($asset.asset)."
        }

        New-Item -ItemType Directory -Path $extracted | Out-Null
        if ($IsWindows) {
            $archive = [IO.Compression.ZipFile]::OpenRead($download.FullName)
            try {
                $entry = $archive.GetEntry($asset.executable)
                if ($null -eq $entry) {
                    throw "Missing $($asset.executable) in $($asset.asset)."
                }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $extractedBinary)
            }
            finally {
                $archive.Dispose()
            }
        }
        else {
            tar -xzf $download.FullName -C $extracted $asset.executable
        }

        if ((Get-FileHash -LiteralPath $extractedBinary -Algorithm SHA256).Hash -ne $asset.executableSha256) {
            throw "Executable SHA-256 mismatch for $($asset.asset)."
        }
        New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
        Move-Item -LiteralPath $extractedBinary -Destination $executable -Force
    }
    finally {
        Remove-Item -LiteralPath $download.FullName -Force
        if (Test-Path -LiteralPath $extractedBinary) {
            Remove-Item -LiteralPath $extractedBinary -Force
        }
        if (Test-Path -LiteralPath $extracted) {
            Remove-Item -LiteralPath $extracted -Force
        }
    }
}

if ((& $executable version) -ne $gitleaks.version) {
    throw "Unexpected Gitleaks version at $executable."
}
Write-Host "Gitleaks $($gitleaks.version)"
