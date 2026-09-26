[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipGitCheck,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'src\Kytto.App\Kytto.App.csproj'
$gatewayProject = Join-Path $repoRoot 'src\Kytto.Gateway\Kytto.Gateway.csproj'
$dist = Join-Path $repoRoot 'dist'
$stage = Join-Path $dist "Kytto-$Runtime"

if (-not $SkipGitCheck) {
    $inside = (& git -C $repoRoot rev-parse --is-inside-work-tree 2>$null) -eq 'true'
    if (-not $inside) {
        throw 'Release builds require a Git checkout. Use -SkipGitCheck only for local script validation.'
    }
    if (& git -C $repoRoot status --porcelain) {
        throw 'The Git worktree is not clean. Commit the exact release sources before building.'
    }
}

if (Test-Path -LiteralPath $dist) {
    $resolvedDist = [System.IO.Path]::GetFullPath($dist)
    if (-not $resolvedDist.StartsWith($repoRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear release output outside the repository: $resolvedDist"
    }
    Remove-Item -LiteralPath $resolvedDist -Recurse -Force
}
New-Item -ItemType Directory -Path $stage | Out-Null

[xml]$projectXml = [System.IO.File]::ReadAllText($project, [System.Text.Encoding]::UTF8)
$version = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'Kytto.App.csproj has no Version property.'
}

& dotnet publish $project `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $stage `
    -p:ContinuousIntegrationBuild=true `
    -p:Deterministic=true `
    -p:DebugSymbols=false `
    -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

& dotnet publish $gatewayProject `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $stage `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:ContinuousIntegrationBuild=true `
    -p:Deterministic=true `
    -p:DebugSymbols=false `
    -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw "gateway publish failed with exit code $LASTEXITCODE." }

$thumbprint = $env:KYTTO_SIGNING_CERT_THUMBPRINT
if (-not [string]::IsNullOrWhiteSpace($thumbprint)) {
    $signTool = Get-Command signtool.exe -ErrorAction Stop
    $timestamp = if ([string]::IsNullOrWhiteSpace($env:KYTTO_TIMESTAMP_URL)) {
        'http://timestamp.digicert.com'
    } else {
        $env:KYTTO_TIMESTAMP_URL
    }
    $signTargets = @(
        (Join-Path $stage 'Kytto.exe'),
        (Join-Path $stage 'kytto-mcp-proxy.exe'),
        (Join-Path $stage 'Kytto.dll'),
        (Join-Path $stage 'Kytto.Core.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ }

    foreach ($target in $signTargets) {
        & $signTool.Source sign /sha1 $thumbprint /fd SHA256 /tr $timestamp /td SHA256 $target
        if ($LASTEXITCODE -ne 0) { throw "Signing failed for $target." }
        & $signTool.Source verify /pa /all $target
        if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $target." }
    }
} else {
    Write-Warning 'KYTTO_SIGNING_CERT_THUMBPRINT is not set; producing an unsigned beta artifact.'
}

function Write-Checksum {
    param([string]$Path)
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        "$Path.sha256",
        "$hash *$([System.IO.Path]::GetFileName($Path))`n",
        [System.Text.UTF8Encoding]::new($false))
    return $hash
}

$artifact = Join-Path $dist "Kytto-Windows-$Runtime-$version.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $artifact -CompressionLevel Optimal
$hash = Write-Checksum -Path $artifact

Write-Host "Artifact: $artifact"
Write-Host "SHA-256: $hash"

# The installer carries the version the binaries were built with, passed in rather
# than repeated in installer.iss — a setup labelled 1.0.1 wrapping 1.0.2 binaries
# is the kind of mistake nobody finds until a bug report cites the wrong build.
if ($SkipInstaller) {
    Write-Host 'Installer: skipped (-SkipInstaller).'
    return
}

$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    # The per-user path first: that is where `winget install JRSoftware.InnoSetup`
    # puts it, and it is not on PATH.
    foreach ($candidate in @(
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
        if (Test-Path -LiteralPath $candidate) {
            $iscc = Get-Command $candidate
            break
        }
    }
}
if (-not $iscc) {
    throw 'ISCC.exe (Inno Setup 6) was not found. Install it, or pass -SkipInstaller to produce only the zip.'
}

& $iscc.Source `
    "/DAppVersionSlug=$version" `
    "/DAppRuntimeSlug=$Runtime" `
    (Join-Path $PSScriptRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

$setup = Join-Path $dist "Kytto-Setup-$Runtime-$version.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "ISCC reported success but $setup is missing." }

if (-not [string]::IsNullOrWhiteSpace($thumbprint)) {
    & $signTool.Source sign /sha1 $thumbprint /fd SHA256 /tr $timestamp /td SHA256 $setup
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $setup." }
    & $signTool.Source verify /pa /all $setup
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $setup." }
}

# Hashed after signing: the bytes a user downloads are the signed ones.
$setupHash = Write-Checksum -Path $setup
Write-Host "Installer: $setup"
Write-Host "SHA-256: $setupHash"
