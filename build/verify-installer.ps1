[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [string]$TestRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (!$TestRoot) { $TestRoot = Join-Path $artifactsRoot ('installer-test-' + [Guid]::NewGuid().ToString('N')) }
$TestRoot = [IO.Path]::GetFullPath($TestRoot)
if (!$TestRoot.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer test directories must be inside this project artifacts directory.' }
if (Test-Path -LiteralPath $TestRoot) { throw 'Choose a new, empty installer test directory.' }
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{38547CC2-FB09-4F2D-AB4C-CF51E7CE0237}_is1'
if (Test-Path -LiteralPath $uninstallKey) { throw 'An existing installed SourceGitAutoUpdater was found. Refusing to replace its registration during testing.' }
$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
$installRoot = Join-Path $TestRoot 'app'
$outsideRoot = Join-Path $TestRoot 'outside'
New-Item -ItemType Directory -Path $TestRoot,$outsideRoot -Force | Out-Null
function Assert-Condition([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Invoke-SilentSetup([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer process failed with exit code $($process.ExitCode)." }
}
$setupArgs = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS', ('/DIR="' + $installRoot + '"'), ('/LOG="' + (Join-Path $TestRoot 'install.log') + '"'))
$uninstaller = Join-Path $installRoot 'unins000.exe'
try {
    Invoke-SilentSetup $InstallerPath $setupArgs
    Assert-Condition (Test-Path -LiteralPath (Join-Path $installRoot 'installed.json')) 'Installed-mode marker is missing.'
    $appRoot = Join-Path $installRoot 'SourceGit'
    $sourceGitExe = Join-Path $appRoot 'SourceGit.exe'
    Assert-Condition (!(Test-Path -LiteralPath $sourceGitExe)) 'Bootstrap installer must not bundle SourceGit.exe.'
    Assert-Condition (!(Test-Path -LiteralPath (Join-Path $appRoot 'data'))) 'Bootstrap installer must not include portable data.'
    Assert-Condition (!(Test-Path -LiteralPath (Join-Path $appRoot 'sourcegit-managed-files.txt'))) 'Bootstrap installer must not include a SourceGit managed-file manifest.'
    Assert-Condition (!(Test-Path -LiteralPath $appRoot)) 'Bootstrap installer must not create a SourceGit payload directory.'
    # Simulate files created by the runtime downloader without launching any program or network request.
    New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
    [IO.File]::WriteAllText($sourceGitExe, 'isolated downloaded-version fixture')
    'SourceGit.exe' | Set-Content -LiteralPath (Join-Path $appRoot 'sourcegit-managed-files.txt') -Encoding utf8NoBOM
    $before = (Get-FileHash -LiteralPath $sourceGitExe -Algorithm SHA256).Hash
    $dataRoot = Join-Path $appRoot 'data'
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    $dataSentinel = Join-Path $dataRoot 'preference.json'
    '{"preserve":true}' | Set-Content -LiteralPath $dataSentinel -Encoding utf8NoBOM
    $unknownFile = Join-Path $appRoot 'user-owned.txt'
    'keep me' | Set-Content -LiteralPath $unknownFile -Encoding utf8NoBOM
    $outsideSentinel = Join-Path $outsideRoot 'protected.txt'
    'outside installation' | Set-Content -LiteralPath $outsideSentinel -Encoding utf8NoBOM
    Invoke-SilentSetup $InstallerPath $setupArgs
    Assert-Condition ((Get-FileHash -LiteralPath $sourceGitExe -Algorithm SHA256).Hash -eq $before) 'Installer replaced the existing managed SourceGit version.'
    Assert-Condition ((Get-Content -LiteralPath $dataSentinel -Raw).Contains('preserve')) 'Installer overwrote portable user data.'
    $junction = Join-Path $appRoot 'linked'
    New-Item -ItemType Junction -Path $junction -Target $outsideRoot | Out-Null
    $manifest = Join-Path $appRoot 'sourcegit-managed-files.txt'
    @('data/preference.json','data./preference.json','../outside/protected.txt','../../outside/protected.txt','linked/protected.txt') | Add-Content -LiteralPath $manifest -Encoding utf8NoBOM
    Invoke-SilentSetup $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART', ('/LOG="' + (Join-Path $TestRoot 'uninstall.log') + '"'))
    Assert-Condition (!(Test-Path -LiteralPath (Join-Path $installRoot 'SourceGitAutoUpdater.exe'))) 'Uninstaller left the launcher executable.'
    Assert-Condition (!(Test-Path -LiteralPath $sourceGitExe)) 'Uninstaller left the managed SourceGit executable.'
    Assert-Condition (Test-Path -LiteralPath $dataSentinel) 'Uninstaller removed user data.'
    Assert-Condition (Test-Path -LiteralPath $unknownFile) 'Uninstaller removed an unknown user file.'
    Assert-Condition (Test-Path -LiteralPath $outsideSentinel) 'Uninstaller followed a traversal or junction manifest entry.'
    Write-Host 'PASS: bootstrap installer contains no SourceGit payload, existing downloaded version retained, managed files removed, data/unknown files preserved, traversal/junction rejected.'
    Write-Host "Logs and preserved test data: $TestRoot"
} finally {
    if (Test-Path -LiteralPath $uninstaller) {
        Invoke-SilentSetup $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
    }
}

