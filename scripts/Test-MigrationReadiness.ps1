# Builds the current app and runs the existing Core suite without activating the UI.
# Migration acceptance itself is recorded separately in docs/common-spec-acceptance.md.
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')][string]$Platform = 'x64',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$UseConfiguredPackageSources
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runName = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$artifactRoot = Join-Path $repoRoot "ui-test-artifacts\migration-readiness\$runName"
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$steps = [Collections.Generic.List[object]]::new()
$lockPath = Join-Path $repoRoot 'ContinuousPlayer\packages.lock.json'
$lockHashBefore = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash
$runtime = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$savedEnvironment = @{}
$environmentOverrides = @{
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_NOLOGO = '1'
    DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    MSBUILDDISABLENODEREUSE = '1'
    DOTNET_CLI_HOME = (Join-Path $repoRoot '.tools\dotnet-migration-home')
}

function Invoke-ReadinessStep([string]$Name, [string]$Executable, [string[]]$Arguments) {
    $logPath = Join-Path $artifactRoot ($Name + '.log')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $code = -1
    try {
        $messages = @(& $Executable @Arguments 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    } catch {
        $messages = @($_.ToString())
    }
    $messages | Set-Content -LiteralPath $logPath -Encoding utf8
    $status = if ($code -eq 0) { 'PASS' } else { 'FAIL' }
    $steps.Add([ordered]@{
        name = $Name; status = $status; exitCode = $code
        executable = $Executable; arguments = $Arguments
        seconds = [math]::Round($watch.Elapsed.TotalSeconds, 2); log = $logPath
    })
    Write-Host "$status $Name"
    if ($code -ne 0) { $messages | Select-Object -Last 12 | ForEach-Object { Write-Host $_ } }
    return ($code -eq 0)
}

function Add-SkippedStep([string]$Name, [string]$Reason) {
    $steps.Add([ordered]@{ name = $Name; status = 'SKIP'; reason = $Reason })
    Write-Host "SKIP $Name ($Reason)"
}

$fatalError = $null
$lockHashAfter = $null
Push-Location -LiteralPath $repoRoot
try {
    foreach ($name in $environmentOverrides.Keys) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environmentOverrides[$name], 'Process')
    }
    $gitHead = (@(& git rev-parse HEAD) -join '').Trim()
    $gitStatus = @(& git status --short)
    $sdkAvailable = Invoke-ReadinessStep 'dotnet-version' 'dotnet' @('--version')
    $winappAvailable = Invoke-ReadinessStep 'winapp-version' 'winapp' @('--version')
    if ($winappAvailable) {
        $versionText = (Get-Content -LiteralPath (Join-Path $artifactRoot 'winapp-version.log') -Raw).Trim()
        if ($versionText -notmatch '^(\d+)\.(\d+)\.(\d+)' -or [version]$Matches[0] -lt [version]'0.7.0') {
            throw "WinApp CLI 0.7+ is required; found '$versionText'."
        }
    }
    if (!$sdkAvailable) { throw 'The project .NET SDK is not available. See dotnet-version.log.' }

    # Each project gets an ignored lock file under its own obj folder. Preserve the
    # user's tracked lock file, including pre-existing edits and its architecture.
    $restoreProperties = @('-p:NuGetLockFilePath=obj/migration-readiness.lock.json', '-p:NuGetAudit=false')
    $sourceArguments = @()
    if (!$UseConfiguredPackageSources) {
        $cachePath = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
        $assetsPath = Join-Path $repoRoot 'ContinuousPlayer\obj\project.assets.json'
        if (Test-Path -LiteralPath $assetsPath) {
            $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
            $cachePath = $assets.project.restore.packagesPath
        }
        if (!(Test-Path -LiteralPath $cachePath)) { throw "Local NuGet package cache is missing: $cachePath" }
        $sourceArguments = @('--source', $cachePath)
        $userCache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
        if ((Test-Path -LiteralPath $userCache) -and [IO.Path]::GetFullPath($userCache).TrimEnd('\') -ne [IO.Path]::GetFullPath($cachePath).TrimEnd('\')) {
            $sourceArguments += @('--source', $userCache)
        }
    }
    $buildOptions = @('--no-restore', '--disable-build-servers', '-p:UseSharedCompilation=false', '-nologo')
    $appProject = 'ContinuousPlayer/ContinuousPlayer.csproj'
    $testProject = 'tests/ContinuousPlayer.Core.Tests/ContinuousPlayer.Core.Tests.csproj'

    $appRestored = Invoke-ReadinessStep 'app-restore' 'dotnet' (
        @('restore', $appProject, '-r', $runtime, "-p:Platform=$Platform", "-p:Configuration=$Configuration") + $restoreProperties + $sourceArguments)
    if ($appRestored) {
        $null = Invoke-ReadinessStep 'app-build' 'dotnet' (
            @('build', $appProject, '-c', $Configuration, "-p:Platform=$Platform") + $buildOptions)
    } else { Add-SkippedStep 'app-build' 'App restore failed.' }

    $testsRestored = Invoke-ReadinessStep 'core-restore' 'dotnet' (
        @('restore', $testProject, "-p:Configuration=$Configuration") + $restoreProperties + $sourceArguments)
    if ($testsRestored) {
        $testsBuilt = Invoke-ReadinessStep 'core-build' 'dotnet' (
            @('build', $testProject, '-c', $Configuration) + $buildOptions)
        if ($testsBuilt) {
            $null = Invoke-ReadinessStep 'core-tests' 'dotnet' @(
                "tests/ContinuousPlayer.Core.Tests/bin/$Configuration/net10.0/ContinuousPlayer.Core.Tests.dll")
        } else { Add-SkippedStep 'core-tests' 'Core test build failed.' }
    } else {
        Add-SkippedStep 'core-build' 'Core restore failed.'
        Add-SkippedStep 'core-tests' 'Core restore failed.'
    }
} catch {
    $fatalError = $_.ToString()
    Write-Host "FAIL preparation: $fatalError"
} finally {
    $lockHashAfter = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    Pop-Location
}

$success = !$fatalError -and $lockHashBefore -eq $lockHashAfter -and
    @($steps | Where-Object { $_.status -ne 'PASS' }).Count -eq 0
$report = [ordered]@{
    schemaVersion = 1; generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    kind = 'build-and-core-baseline'; migrationAcceptanceVerified = $false
    success = $success; repository = $repoRoot; gitHead = $gitHead; gitStatusBefore = $gitStatus
    platform = $Platform; configuration = $Configuration; runtimeIdentifier = $runtime
    packageSources = $(if ($UseConfiguredPackageSources) { 'configured' } else { 'local-cache-only' })
    trackedLockUnchanged = ($lockHashBefore -eq $lockHashAfter)
    lockSha256Before = $lockHashBefore; lockSha256After = $lockHashAfter
    error = $fatalError; steps = @($steps.ToArray())
}
$reportPath = Join-Path $artifactRoot 'results.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Host "Baseline success: $success; Report: $reportPath"
if (!$success) { exit 1 }
