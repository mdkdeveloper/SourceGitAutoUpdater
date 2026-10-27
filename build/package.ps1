[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')][string]$Version,
    [string]$PublishDirectory,
    [string]$OutputDirectory,
    [string]$IsccPath,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$workRoot = Join-Path $OutputDirectory '.work'
$workDir = Join-Path $workRoot ($RuntimeIdentifier + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $OutputDirectory,$workDir -Force | Out-Null

function Assert-NativeExecutable([string]$Path, [string]$Runtime, [Version]$ExpectedVersion) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Invalid DOS header: $Path" }
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt ($stream.Length - 24)) { throw 'Invalid PE offset.' }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550) { throw 'Invalid PE signature.' }
        $expectedMachine = if ($Runtime -eq 'win-x64') { 0x8664 } else { 0xAA64 }
        if ($reader.ReadUInt16() -ne $expectedMachine) { throw "Wrong executable architecture for $Runtime" }
    } finally { $reader.Dispose(); $stream.Dispose() }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $actual = [Version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
    $buildPart = [Math]::Max(0, $ExpectedVersion.Build)
    $revisionPart = [Math]::Max(0, $ExpectedVersion.Revision)
    $expected = [Version]::new($ExpectedVersion.Major, $ExpectedVersion.Minor, $buildPart, $revisionPart)
    if ($actual -ne $expected) { throw "Executable version $actual does not match release $expected" }
}

# Packaging contains only the launcher. Official SourceGit is resolved/downloaded at first launch.
if (!$PublishDirectory) {
    $PublishDirectory = Join-Path $workDir 'publish'
    $appProject = Join-Path $projectRoot 'src/SourceGitAutoUpdater/SourceGitAutoUpdater.csproj'
    & dotnet publish $appProject -c Release -r $RuntimeIdentifier --self-contained true -p:Version=$Version -p:PublishSingleFile=false -o $PublishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
if (!(Test-Path -LiteralPath (Join-Path $PublishDirectory 'SourceGitAutoUpdater.exe'))) { throw 'Published launcher executable is missing.' }
foreach ($item in Get-ChildItem -LiteralPath $PublishDirectory -Recurse -Force) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Publish directory contains a reparse point: $($item.FullName)" }
    if (($item.PSIsContainer -and $item.Name -in @('SourceGit', '.updater', '.git')) -or
        (!$item.PSIsContainer -and ($item.Name -in @('SourceGit.exe', 'SourceGit.dll', 'SourceGit.deps.json', 'SourceGit.runtimeconfig.json', 'sourcegit-managed-files.txt', 'installed.json') -or $item.Name -match '^sourcegit.*\.(zip|7z|tar|gz|tgz)$'))) {
        throw "Publish directory is contaminated by installation state or SourceGit payload: $($item.FullName)"
    }
}
Assert-NativeExecutable (Join-Path $PublishDirectory 'SourceGitAutoUpdater.exe') $RuntimeIdentifier ([Version]$Version)

$portable = Join-Path $workDir 'portable'
New-Item -ItemType Directory -Path $portable -Force | Out-Null
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $portable -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $portable
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.txt') -Destination $portable
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $portable
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $portable -Recurse -Force
$dependencies = Get-Content -LiteralPath (Join-Path $PublishDirectory 'SourceGitAutoUpdater.deps.json') -Raw | ConvertFrom-Json -AsHashtable
$runtimePack = @($dependencies.libraries.Keys | Where-Object { $_.StartsWith('runtimepack.Microsoft.NETCore.App.Runtime.', [StringComparison]::Ordinal) })
if ($runtimePack.Count -ne 1) { throw 'Launcher publish must be self-contained with exactly one .NET runtime pack.' }
$runtimeParts = $runtimePack[0].Split('/')
$runtimePackageName = $runtimeParts[0].Substring('runtimepack.'.Length).ToLowerInvariant()
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$runtimePackageDirectory = Join-Path (Join-Path $nugetRoot $runtimePackageName) $runtimeParts[1]
Copy-Item -LiteralPath (Join-Path $runtimePackageDirectory 'LICENSE.TXT') -Destination (Join-Path $portable 'licenses/dotnet-runtime-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $runtimePackageDirectory 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $portable 'licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt')
$metadata = [ordered]@{
    launcher_version = $Version
    runtime = $RuntimeIdentifier
    sourcegit_repository = 'https://github.com/sourcegit-scm/sourcegit'
    download_on_first_launch = $true
    packaged_at_utc = [DateTimeOffset]::UtcNow.ToString('o')
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $portable 'package-metadata.json') -Encoding utf8NoBOM
$installed = Join-Path $workDir 'installed'
New-Item -ItemType Directory -Path $installed -Force | Out-Null
Copy-Item -Path (Join-Path $portable '*') -Destination $installed -Recurse
'{"mode":"installed"}' | Set-Content -LiteralPath (Join-Path $installed 'installed.json') -Encoding utf8NoBOM
$baseName = "SourceGitAutoUpdater_${Version}_${RuntimeIdentifier}"
$portableArchive = Join-Path $OutputDirectory ($baseName + '_portable.zip')
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $portableArchive -CompressionLevel Optimal -Force
$checksum = (Get-FileHash -LiteralPath $portableArchive -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $([IO.Path]::GetFileName($portableArchive))" | Set-Content -LiteralPath ($portableArchive + '.sha256') -Encoding ascii
& (Join-Path $PSScriptRoot 'verify-package.ps1') -ArchivePath $portableArchive -RuntimeIdentifier $RuntimeIdentifier -Version $Version
if (!$SkipInstaller) {
    if (!$IsccPath) {
        $candidate = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($candidate) { $IsccPath = $candidate.Source }
        else {
            $candidates = @(
                (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
                (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7/ISCC.exe'),
                (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe')
            )
            $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        }
    }
    if (!$IsccPath -or !(Test-Path -LiteralPath $IsccPath)) { throw 'Inno Setup 6 or 7 ISCC.exe was not found. Pass -IsccPath or use -SkipInstaller for ZIP only.' }
    $installerScript = Join-Path $PSScriptRoot 'installer.iss'
    & $IsccPath /Qp "/DAppVersion=$Version" "/DRuntimeIdentifier=$RuntimeIdentifier" "/DPackageDir=$installed" "/DOutputDir=$OutputDirectory" $installerScript
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
    $setupPath = Join-Path $OutputDirectory ($baseName + '_setup.exe')
    $setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$setupHash  $([IO.Path]::GetFileName($setupPath))" | Set-Content -LiteralPath ($setupPath + '.sha256') -Encoding ascii
}
Copy-Item -LiteralPath (Join-Path $portable 'package-metadata.json') -Destination (Join-Path $OutputDirectory ($baseName + '_metadata.json'))
Write-Host "Launcher-only packages saved to $OutputDirectory. SourceGit downloads on first launch."
Write-Host "Staging retained at $workDir for inspection."

