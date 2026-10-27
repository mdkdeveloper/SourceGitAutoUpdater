[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
try {
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        $parts = $name.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
        $fileName = if ($parts.Length) { $parts[-1] } else { '' }
        if ($parts -contains 'SourceGit' -or $fileName -in @('SourceGit.exe', 'SourceGit.dll', 'SourceGit.deps.json', 'SourceGit.runtimeconfig.json', 'sourcegit-managed-files.txt', 'installed.json') -or $fileName -match '^sourcegit.*\.(zip|7z|tar|gz|tgz)$') {
            throw "Bootstrap archive contains SourceGit payload or installed state: $name"
        }
    }
    $requiredEntries = @(
        'SourceGitAutoUpdater.exe', 'SourceGitAutoUpdater.deps.json', 'SourceGitAutoUpdater.runtimeconfig.json', 'coreclr.dll',
        'LICENSE', 'THIRD_PARTY_NOTICES.txt', 'README.md', 'package-metadata.json',
        'licenses/JetBrainsMono-OFL.txt', 'licenses/Inter-OFL.txt',
        'licenses/dotnet-runtime-LICENSE.txt', 'licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt'
    )
    foreach ($required in $requiredEntries) {
        if (@($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ceq $required }).Count -ne 1) { throw "Required bootstrap entry missing or duplicated: $required" }
    }
    $metadataEntry = $archive.Entries | Where-Object FullName -CEQ 'package-metadata.json'
    $reader = [IO.StreamReader]::new($metadataEntry.Open())
    try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable } finally { $reader.Dispose() }
    if ($metadata.launcher_version -cne $Version -or $metadata.runtime -cne $RuntimeIdentifier -or $metadata.download_on_first_launch -ne $true -or $metadata.sourcegit_repository -cne 'https://github.com/sourcegit-scm/sourcegit') { throw 'Bootstrap package metadata does not match the requested release.' }
    foreach ($obsolete in @('sourcegit_tag', 'sourcegit_asset', 'sourcegit_sha256', 'sourcegit_digest_verified')) {
        if ($metadata.ContainsKey($obsolete)) { throw "Bootstrap metadata incorrectly describes a bundled release: $obsolete" }
    }
    Write-Host "PASS $RuntimeIdentifier bootstrap ZIP: no SourceGit payload; self-contained runtime, licenses and first-launch metadata present."
} finally { $archive.Dispose() }

