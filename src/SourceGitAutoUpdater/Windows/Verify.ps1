# Runs native Windows checks without opening visible application windows or changing SourceGit settings.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'These probes require Windows.' }
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) ('SourceGitAutoUpdater.WindowsProbe_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeRoot | Out-Null
$escapedSources = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot '*.cs'))
$project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><BuiltInComInteropSupport>true</BuiltInComInteropSupport></PropertyGroup><ItemGroup><Compile Include="' + $escapedSources + '" /></ItemGroup></Project>'
[IO.File]::WriteAllText((Join-Path $probeRoot 'Probe.csproj'), $project)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Verification\Program.cs.txt') -Destination (Join-Path $probeRoot 'Program.cs')
& dotnet run --project (Join-Path $probeRoot 'Probe.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "Windows integration probes failed with exit code $LASTEXITCODE. Probe files: $probeRoot" }
Write-Host "Probe files: $probeRoot"
