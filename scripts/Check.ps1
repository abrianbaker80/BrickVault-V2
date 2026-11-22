#Requires -Version 7.6

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$repo = Split-Path -Parent $PSScriptRoot
$toolchain = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'toolchain.json') -Raw | ConvertFrom-Json
$gitleaks = $toolchain.gitleaks
$asset = if ($IsWindows) { $gitleaks.windows } else { $gitleaks.linux }
$scanner = Join-Path $repo ".tools/gitleaks-$($gitleaks.version)/$($asset.executable)"

if (-not (Test-Path -LiteralPath $scanner -PathType Leaf) -or
    (Get-FileHash -LiteralPath $scanner -Algorithm SHA256).Hash -ne $asset.executableSha256) {
    throw 'Run scripts/Setup.ps1 to install the expected Gitleaks version.'
}

Push-Location -LiteralPath $repo
try {
    $requiredFiles = @(
        '.codex/config.toml'
        '.codex/agents/architect.toml'
        '.codex/agents/builder.toml'
        '.codex/agents/reviewer.toml'
        '.github/workflows/repository-checks.yml'
        'scripts/Setup.ps1'
        'scripts/Check.ps1'
        'scripts/toolchain.json'
        'docs/verification/codex-native-acceptance.md'
        'docs/verification/codex-native-fixture.md'
        'README.md'
        'CHANGELOG.md'
        'AGENTS.md'
        'docs/architecture/MASTER_PLAN.md'
        'docs/architecture/FEATURE_MATRIX.md'
        'docs/architecture/DECISIONS.md'
        'docs/architecture/Inventory-and-BrickLink.md'
        'docs/architecture/Legacy-assessment.md'
        'docs/architecture/Bricqer-guide-review.md'
    )
    $trackedPaths = @(git -c core.quotePath=false ls-files)
    $trackedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $trackedFiles.UnionWith([string[]]$trackedPaths)
    foreach ($path in $requiredFiles) {
        if (-not $trackedFiles.Contains($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing required tracked file: $path"
        }
    }

    $documents = $trackedPaths.Where({ [IO.Path]::GetExtension($_) -eq '.md' })
    $inlineLink = @'
\]\((?<destination><[^>\r\n]+>|[^\s)]+)(?:[ \t]+(?:"[^"\r\n]*"|'[^'\r\n]*'))?\)
'@
    foreach ($path in $documents) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing tracked document: $path"
        }

        $content = Get-Content -LiteralPath $path -Raw
        $directory = Split-Path -Parent (Join-Path $repo $path)
        foreach ($match in [regex]::Matches([string]$content, $inlineLink)) {
            $destination = $match.Groups['destination'].Value.Trim('<', '>')
            if ($destination -match '^(?:[A-Za-z][A-Za-z0-9+.-]*:|/|#)') {
                continue
            }

            $relativePath = [uri]::UnescapeDataString(($destination -split '[?#]', 2)[0])
            if ([IO.Path]::GetExtension($relativePath) -ne '.md') {
                continue
            }

            $target = Join-Path $directory $relativePath
            $trackedPath = [IO.Path]::GetRelativePath($repo, $target).Replace('\', '/')
            if (-not $trackedFiles.Contains($trackedPath) -or -not (Test-Path -LiteralPath $target -PathType Leaf)) {
                throw "Broken relative Markdown link in ${path}: $destination"
            }
        }
    }

    foreach ($path in $trackedPaths.Where({ [IO.Path]::GetExtension($_) -eq '.ps1' })) {
        $tokens = $null
        $parseErrors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repo $path), [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count) {
            throw "PowerShell parse error in ${path}: $($parseErrors[0].Message)"
        }
    }

    if ((git rev-parse --is-shallow-repository) -ne 'false') {
        throw 'The M0 check needs a full Git history.'
    }
    $base = git merge-base HEAD origin/main
    git diff --check $base HEAD
    git diff --cached --check $base
    git diff --check
    & $scanner git '--log-opts=HEAD' --redact=100 --no-banner .
    & $scanner git --staged --redact=100 --no-banner .
    Write-Host 'M0 repository checks passed.'
}
finally {
    Pop-Location
}
