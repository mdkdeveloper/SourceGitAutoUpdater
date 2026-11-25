# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows-only launcher/updater for [SourceGit](https://github.com/sourcegit-scm/sourcegit) (C#, .NET 10, Avalonia 11.3.22). Release packages contain **only the launcher and its self-contained runtime**; the official, unmodified SourceGit release is downloaded from GitHub on first launch and on later updates. SourceGit binaries are never patched, bundled, or embedded. Packaging and verification scripts actively reject a publish/archive that contains a SourceGit payload, so do not add one.

## Commands

Requires Windows and .NET SDK 10. Run from the repository root (PowerShell):

```powershell
dotnet build src/SourceGitAutoUpdater/SourceGitAutoUpdater.csproj -c Release
dotnet run --project tests/SourceGitAutoUpdater.Core.Tests/SourceGitAutoUpdater.Core.Tests.csproj -c Release
dotnet run --project tests/SourceGitAutoUpdater.UI.Tests/SourceGitAutoUpdater.UI.Tests.csproj -c Release
pwsh -NoProfile -File src/SourceGitAutoUpdater/Windows/Verify.ps1
dotnet publish src/SourceGitAutoUpdater/SourceGitAutoUpdater.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
pwsh -NoProfile -File tests/verify-launcher.ps1 -PublishDirectory artifacts/publish/win-x64
```

CI (`.github/workflows/build.yml`) runs exactly this sequence plus a PowerShell parse check of `build/*.ps1`.

- **Tests are plain console executables, not xUnit/NUnit.** Each `Program.cs` registers cases via `Test(...)`/`AsyncTest(...)`, prints `PASS`/`FAIL` per case, and exits non-zero on any failure. There is no filter flag: to run one case, temporarily comment out others or run the whole suite (it is fast). New cases go in the same file using the existing `Assert`/`Throws`/`Fixture()` helpers. Core fixtures live under `%TEMP%\SourceGitAutoUpdater.Tests\<guid>` and use fake `HttpMessageHandler`s, so no network access.
- UI tests use `Avalonia.Headless` and write Skia PNG screenshots plus a report to `artifacts/ui-tests*` (optional first arg = output directory). They cover light/dark × 100/150/200% scaling across the bootstrap-check, bootstrap-download, and update-offer states.
- `Windows/Verify.ps1` compiles `Windows/*.cs` together with `Windows/Verification/Program.cs.txt` into a temporary probe project (the `.txt` extension keeps it out of the main build) and checks hidden-window properties, shortcuts, and IPC.
- `tests/verify-launcher.ps1` runs the published launcher against a fake SourceGit fixture (arguments, cwd, exit codes, settings preservation). It never launches real SourceGit.
- Manual UI preview without installing anything: `SourceGitAutoUpdater.exe --preview-update --theme=Dark --locale=en_US --scale=1.5`. `--check-updates` forces a check, ignoring Skip.

Packaging (requires Inno Setup 6/7; CI pins 6.7.2): `./build/package.ps1 -RuntimeIdentifier win-x64|win-arm64 -Version X.Y.Z [-SkipInstaller] [-PublishDirectory ...]`. Verify with `build/verify-package.ps1` and `build/verify-installer.ps1`. Output goes to `artifacts/`. Pushing a `vX.Y.Z` tag triggers `release.yml`, which builds both architectures and publishes a GitHub Release. The version lives in `src/SourceGitAutoUpdater/SourceGitAutoUpdater.csproj` (`<Version>`) and must match the `-Version` passed to packaging.

## Architecture

Two projects:

- **`SourceGitAutoUpdater.Core`**: UI-free logic, covered by the core tests.
  - `AppPaths`: the layout. **Portable vs installed mode is decided by whether `installed.json` exists next to the exe** (the Inno installer writes it). Portable mode keeps SourceGit settings in `SourceGit/data` and updater state in `.updater/`. Installed mode uses `%APPDATA%\SourceGit` and `%LOCALAPPDATA%\SourceGitAutoUpdater`. SourceGit itself is always at `<root>/SourceGit/SourceGit.exe`.
  - `PreferencesStore`: edits SourceGit's `preference.json` as a `JsonObject` so unknown fields survive. It sets `Check4UpdatesOnStartup=false` (the launcher owns updates) and `IgnoreUpdateTag` (Skip). Writes are atomic via `AtomicFile` (temp file + `File.Replace` with a `.bak` backup).
  - `SourceGitLease`: an exclusive handle on SourceGit's own `data/process.lock`. Callers must hold it before mutating preferences or installing.
  - `ReleaseService` (`Releases.cs`): queries the GitHub `releases/latest` API, parses numeric tags (rejects prereleases), and picks the architecture asset.
  - `UpdaterEngine`: a transactional install. It downloads to `.sourcegit-update/`, extracts with zip-slip and size guards, validates the staged `SourceGit.exe` version, then moves old entries to `backup/` and new entries in. A `journal.json` phase (`prepared` → `backing-up` → `installing` → `committed`, with `restoring`/`rolled-back` for recovery) makes `Recover()` able to roll back after a crash at any step. **`SourceGit/data` is never moved**, so the lock and settings stay in place. The optional `checkpoint` callback exists so tests can inject failures and cancellation mid-transaction. Download URLs must pass `IsOfficialAssetUrl`.
  - `BootstrapInstaller`: the first-run install of the latest release. It ignores Skip and never touches preferences.
- **`SourceGitAutoUpdater`**: the Avalonia app plus Windows integration.
  - `Program.Main` dispatches in this order. **Special modes** (`--history`, `--blame`, `--core-editor`, `--rebase-*-editor`, or env `SOURCEGIT_LAUNCH_AS_ASKPASS=TRUE`) skip the UI and update check and proxy directly to SourceGit via `SpecialModeHost` under a shared `InstallationGate`. Otherwise `LauncherInstanceBroker` (named-pipe IPC) makes a secondary launcher forward its args/cwd to the primary and exit. The primary starts Avalonia. Unhandled errors go to `<state>/errors.log`.
  - `LauncherController`: the main flow. It forwards launches to an already running SourceGit, takes the lease, recovers interrupted installs, applies SourceGit's theme/locale/zoom from preferences, optionally imports `%APPDATA%\SourceGit` settings into portable mode, bootstraps or checks for updates, shows `UpdateWindow`, launches SourceGit, then stays resident for taskbar integration and queued forwarded launches.
  - `Windows/`: the native boundary (see `Windows/README.md`). `TaskbarIntegration` sets the AppUserModelID and relaunch properties on SourceGit's windows without injection or reparenting. Call `Start`/`Refresh`/`Dispose` on the UI thread, and dispatch broker events to the UI thread. `InstallationGate` is a per-installation shared/exclusive file lease: exclusive for recovery, settings changes, imports, and the install commit (nonblocking), shared for special-mode children. Keep network downloads and dialogs **outside** the exclusive lease. The gate only coordinates launcher-mediated processes, so also check `ProcessHost.HasManagedProcess` and `SourceGitLease` before mutating.
  - UI resources (`Resources/Themes.axaml`, `Styles.axaml`, `Icons.axaml`, fonts, `Locales/*.axaml`) are adapted from SourceGit and must keep SourceGit's MIT notice in `THIRD_PARTY_NOTICES.txt`. Localized strings come from the locale dictionaries, with `en_US` always loaded as the fallback. `Texts.T(english, ukrainian)` provides the few inline code strings, which are only translated to Ukrainian.

`Directory.Build.props` enables nullable, implicit usings, latest LangVersion, and deterministic builds for all projects.
