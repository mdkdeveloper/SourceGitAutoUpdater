# End-to-end startup checks against the real published launcher and a test-only SourceGit fixture.
# This test never launches official SourceGit, opens a fixture window, or writes its AppData preferences.
[CmdletBinding()]
param([string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Launcher acceptance requires Windows.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) { $PublishDirectory = Join-Path $repository 'artifacts\publish\win-x64' }
$publish = [IO.Path]::GetFullPath($PublishDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $publish 'SourceGitAutoUpdater.exe'))) { throw "Publish the win-x64 launcher before running this test: $publish" }
$projectPath = Join-Path $repository 'src\SourceGitAutoUpdater\SourceGitAutoUpdater.csproj'
[xml]$projectDefinition = [IO.File]::ReadAllText($projectPath)
$expectedVersionText = @($projectDefinition.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
$expectedVersion = [version]$expectedVersionText
$expectedVersion = [version]::new($expectedVersion.Major, $expectedVersion.Minor, [Math]::Max($expectedVersion.Build, 0), [Math]::Max($expectedVersion.Revision, 0))
$actualVersion = [version][Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'SourceGitAutoUpdater.exe')).FileVersion
if ($actualVersion -ne $expectedVersion) { throw "The published launcher is stale: $actualVersion; expected $expectedVersion. Republish win-x64 before acceptance." }
Write-Host "Testing published updater version $actualVersion"
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$runRoot = [IO.Path]::GetFullPath((Join-Path $artifacts ('e2e-' + [Guid]::NewGuid().ToString('N'))))
if (-not $runRoot.StartsWith($artifacts.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Acceptance output escaped artifacts.' }
New-Item -ItemType Directory -Path $runRoot | Out-Null
$appRoot = Join-Path $runRoot 'portable app'
Copy-Item -LiteralPath $publish -Destination $appRoot -Recurse
if (Test-Path -LiteralPath (Join-Path $appRoot 'installed.json')) { throw 'Acceptance requires raw portable publish output, not an installed package.' }
$fixtureRoot = Join-Path $runRoot 'fixture source'
$sourceGitRoot = Join-Path $appRoot 'SourceGit'
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
New-Item -ItemType Directory -Path $sourceGitRoot -Force | Out-Null
@"
<Project Sdk="Microsoft.NET.Sdk">
 <PropertyGroup>
  <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
  <AssemblyName>SourceGit</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
  <Version>9999.0.0.0</Version><AssemblyVersion>9999.0.0.0</AssemblyVersion><FileVersion>9999.0.0.0</FileVersion>
 </PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $fixtureRoot 'Fixture.csproj') -Encoding utf8
@"
using System.Text.Json;
var result = Environment.GetEnvironmentVariable("E2E_RESULT") ?? throw new InvalidOperationException("Fixture must only run from acceptance tests.");
var delay = int.TryParse(Environment.GetEnvironmentVariable("E2E_DELAY_MS"), out var milliseconds) ? Math.Clamp(milliseconds, 0, 10000) : 0;
var started = DateTimeOffset.UtcNow;
if (delay > 0) Thread.Sleep(delay);
File.WriteAllText(result, JsonSerializer.Serialize(new { Arguments = args, WorkingDirectory = Environment.CurrentDirectory,
    ProcessId = Environment.ProcessId, StartedAtUtc = started, CompletedAtUtc = DateTimeOffset.UtcNow }));
return 42;
"@ | Set-Content -LiteralPath (Join-Path $fixtureRoot 'Program.cs') -Encoding utf8
& dotnet publish (Join-Path $fixtureRoot 'Fixture.csproj') -c Release -r win-x64 --self-contained false -o $sourceGitRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'Test-only SourceGit fixture failed to publish.' }
$fixture = Join-Path $sourceGitRoot 'SourceGit.exe'
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($fixture).FileVersion -ne '9999.0.0.0') { throw 'Fixture version must suppress any real release offer.' }
$data = Join-Path $sourceGitRoot 'data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$preference = Join-Path $data 'preference.json'
$working = Join-Path $runRoot 'working directory Україна with spaces'
New-Item -ItemType Directory -Path $working -Force | Out-Null
$launcher = Join-Path $appRoot 'SourceGitAutoUpdater.exe'
$appDataPreferences = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'SourceGit\preference.json'
$appDataHashBefore = if (Test-Path -LiteralPath $appDataPreferences) { (Get-FileHash -LiteralPath $appDataPreferences -Algorithm SHA256).Hash } else { $null }
function Assert-Test([bool]$Condition, [string]$Label) {
 if (-not $Condition) { throw "FAIL $Label. Acceptance files: $runRoot" }
 Write-Host "PASS $Label"
}
function Seed-Preferences([string]$Label) {
 $document = [ordered]@{ Check4UpdatesOnStartup = $true; IgnoreUpdateTag = 'v7777.01'; Theme = 'Dark'; Locale = 'en_US'; Zoom = 1;
  FutureUnknownField = [ordered]@{ Label = $Label; Preserve = @('українська', '', 'a"b'); Number = 137 } }
 $text = $document | ConvertTo-Json -Depth 8
 [IO.File]::WriteAllText($preference, $text)
 return $text
}
function Invoke-Launcher([string]$Label, [string[]]$Arguments, [int]$Delay = 1200) {
 $result = Join-Path $runRoot ($Label + '.json')
 $info = [Diagnostics.ProcessStartInfo]::new($launcher)
 $info.UseShellExecute = $false
 $info.CreateNoWindow = $true
 $info.WorkingDirectory = $working
 $info.Environment['E2E_RESULT'] = $result
 $info.Environment['E2E_DELAY_MS'] = [string]$Delay
 # Prevent an ambient Git Askpass invocation from changing which startup path is tested.
 [void]$info.Environment.Remove('SOURCEGIT_LAUNCH_AS_ASKPASS')
 foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
 $watch = [Diagnostics.Stopwatch]::StartNew()
 $process = [Diagnostics.Process]::Start($info)
 try {
  if (-not $process.WaitForExit(20000)) {
   # Terminate only the dedicated test launcher tree, never other SourceGit or updater processes.
   $process.Kill($true)
   $process.WaitForExit()
   throw "Acceptance launcher timed out: $Label. Files: $runRoot"
  }
  $watch.Stop()
  Assert-Test ($process.ExitCode -eq 42) "$Label propagates child exit code 42"
  Assert-Test ($watch.ElapsedMilliseconds -ge ($Delay - 100)) "$Label waits for child completion"
  Assert-Test (Test-Path -LiteralPath $result) "$Label executes the fixture"
  $record = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
  Assert-Test ($record.Arguments.Count -eq $Arguments.Count) "$Label preserves argument count including empty values"
  for ($index = 0; $index -lt $Arguments.Count; ++$index) {
   Assert-Test ([string]::Equals([string]$record.Arguments[$index], $Arguments[$index], [StringComparison]::Ordinal)) "$Label preserves argument $index"
  }
  Assert-Test ([string]::Equals($record.WorkingDirectory, $working, [StringComparison]::OrdinalIgnoreCase)) "$Label preserves Unicode working directory"
  return $record
 }
 finally { $process.Dispose() }
}
$normalBefore = Seed-Preferences 'normal launch'
$normalArguments = @('repository with spaces', '', 'quoted"value', 'українська', "line`nvalue")
$normal = Invoke-Launcher 'normal' $normalArguments
$afterNormal = Get-Content -LiteralPath $preference -Raw | ConvertFrom-Json
Assert-Test ($afterNormal.Check4UpdatesOnStartup -eq $false) 'normal launch disables the built-in startup check'
Assert-Test ($afterNormal.IgnoreUpdateTag -eq 'v7777.01') 'normal launch retains the skipped tag'
Assert-Test ($afterNormal.FutureUnknownField.Label -eq 'normal launch' -and $afterNormal.FutureUnknownField.Number -eq 137 -and
 $afterNormal.FutureUnknownField.Preserve[0] -eq 'українська' -and $afterNormal.FutureUnknownField.Preserve[1] -eq '' -and $afterNormal.FutureUnknownField.Preserve[2] -eq 'a"b') 'normal launch preserves unknown nested settings'
Assert-Test (Test-Path -LiteralPath ($preference + '.bak')) 'normal launch creates a preferences backup'
Assert-Test ([IO.File]::ReadAllText($preference + '.bak') -eq $normalBefore) 'preferences backup matches the original bytes'
foreach ($mode in @('--core-editor', '--history')) {
 $label = $mode.TrimStart('-')
 $before = Seed-Preferences $label
 $backupBefore = (Get-FileHash -LiteralPath ($preference + '.bak') -Algorithm SHA256).Hash
 $arguments = @($mode, 'file path Україна.txt', '', 'quote"value')
 $record = Invoke-Launcher $label $arguments 900
 Assert-Test ([IO.File]::ReadAllText($preference) -eq $before) "$label bypasses all settings mutations"
 Assert-Test ((Get-FileHash -LiteralPath ($preference + '.bak') -Algorithm SHA256).Hash -eq $backupBefore) "$label leaves the preferences backup unchanged"
}
$canonical = [IO.Path]::GetFullPath($appRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar).ToUpperInvariant()
$identity = [Environment]::UserName + '|' + $canonical
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).Substring(0, 24)
$instanceDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SourceGitAutoUpdater\instances'
foreach ($extension in @('.lock', '.gate')) {
 $lockPath = Join-Path $instanceDirectory ($hash + $extension)
 $ownership = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
 $ownership.Dispose()
 Assert-Test $true "launcher releases $extension ownership after exit"
}
$appDataHashAfter = if (Test-Path -LiteralPath $appDataPreferences) { (Get-FileHash -LiteralPath $appDataPreferences -Algorithm SHA256).Hash } else { $null }
Assert-Test ($appDataHashAfter -eq $appDataHashBefore) 'real SourceGit AppData preferences remain unchanged'
Write-Host "All launcher acceptance scenarios passed. Test-only fixture and outputs: $runRoot"
