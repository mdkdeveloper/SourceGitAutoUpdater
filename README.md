# SourceGitAutoUpdater

A Windows launcher and updater for [SourceGit](https://github.com/sourcegit-scm/sourcegit), built with C#, .NET 10 and Avalonia 11.3.22. It downloads unmodified official SourceGit releases and uses UI components, themes, fonts and icons adapted from SourceGit.

## Installation and first launch

Release formats are **portable.zip** and **setup.exe**, each available for **win-x64** and **win-arm64**. Local release builds are written to `artifacts/`; published builds are listed on the [Releases page](https://github.com/mdkdeveloper/SourceGitAutoUpdater/releases).

**Both formats contain only the updater and its self-contained .NET runtime. SourceGit is downloaded from its official GitHub Releases on first launch.** The first launch requires an internet connection. A working installed copy can subsequently launch offline; downloading updates requires a connection. SourceGit also requires Git to be installed separately.

- **Portable ZIP:** extract to a writable folder and run `SourceGitAutoUpdater.exe`. SourceGit is installed in the adjacent `SourceGit/` directory. Its settings are created under `SourceGit/data` at runtime and travel with the folder. On first setup, the launcher can import existing settings from `%APPDATA%\SourceGit`.
- **Installer:** installs for the current user under `%LOCALAPPDATA%\Programs\SourceGitAutoUpdater`, without administrator rights, and adds a Start menu shortcut. SourceGit is downloaded into the application's `SourceGit/` directory and uses the existing `%APPDATA%\SourceGit` settings. Reinstalling the updater preserves an already downloaded SourceGit copy. Uninstall removes only files in the runtime-generated managed-file list; user data, AppData and unknown files are preserved.

When SourceGit is missing, the launcher downloads the latest stable official Windows release for the machine's architecture, validates it and installs it before launching. A failed first download leaves the updater available to retry rather than pretending a working SourceGit installation exists.

After initial setup, normal launches show the updater window only for a new, unskipped release. **Skip this version** stores that exact `IgnoreUpdateTag`; a subsequent release is offered again. Closing an update offer launches the current copy without changing Skip. The launcher disables SourceGit's built-in startup update check to avoid duplicate notifications, preserving all other settings, including unknown fields.

If the managed SourceGit is already running, launch requests are forwarded and file replacement is deferred. Updates use staged downloads, validation, a transaction journal and recovery; portable settings are preserved. SourceGit source code and executable files are never patched.

## Taskbar integration

Pin the **SourceGitAutoUpdater** shortcut so launches continue through the updater. The helper remains in the background to associate managed SourceGit windows with the same Windows taskbar identity. A second button can briefly appear during startup. Moving a portable installation requires recreating and repinning its shortcut.

Integration uses Windows window properties without injection or window reparenting. Same-user, same-integrity execution is the supported baseline. Grouping an elevated SourceGit process from an ordinary launcher is not guaranteed. Actual taskbar pinning, Explorer restart behavior and native monitor DPI still require verification on the target Windows system; API probes and headless rendering do not establish those visual results.

## Commands

```powershell
.\SourceGitAutoUpdater.exe --check-updates
.\SourceGitAutoUpdater.exe --preview-update --theme=Dark --locale=en_US --scale=1.5
```

`--check-updates` performs an explicit check, ignoring Skip. `--preview-update` previews the update dialog without installing anything; themes are `Light`/`Dark`, with content scales `1`, `1.5` and `2`. Other arguments and the original working directory are forwarded to SourceGit. Editor/history/blame modes bypass the ordinary update check when a usable installation exists.

## Build and verification

Development requires Windows and [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0). A sibling SourceGit checkout is not required.

```powershell
dotnet build src/SourceGitAutoUpdater/SourceGitAutoUpdater.csproj -c Release
dotnet run --project tests/SourceGitAutoUpdater.Core.Tests/SourceGitAutoUpdater.Core.Tests.csproj -c Release
dotnet run --project tests/SourceGitAutoUpdater.UI.Tests/SourceGitAutoUpdater.UI.Tests.csproj -c Release
dotnet publish src/SourceGitAutoUpdater/SourceGitAutoUpdater.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
pwsh -NoProfile -File src/SourceGitAutoUpdater/Windows/Verify.ps1
pwsh -NoProfile -File tests/verify-launcher.ps1 -PublishDirectory artifacts/publish/win-x64
```

The executable core regression suite covers release selection, Skip, unknown preferences, archive validation and interrupted-install recovery. Headless UI tests exercise actions and layout across the initial release-check, initial download and normal update-offer states. The current suite passes 19 grouped checks and saves 18 software-rendered Skia PNGs with a report under `artifacts/ui-tests-bootstrap/`, covering light/dark themes and 100%, 150%, 200% content scaling. This content scaling does not simulate native monitor DPI.

Native Windows probes check hidden-window properties, shortcuts and IPC. Published-launcher acceptance uses an isolated test fixture in place of official SourceGit to verify arguments, working directory, exit codes and settings preservation. It does not launch official SourceGit or write its real AppData preferences. ARM64 release files are built and checked for the correct PE architecture; execution on an ARM64 device must be verified separately.

A development publish also starts without SourceGit and bootstraps the official release on first launch. Use a writable output directory and a connection for that first run.

## Packaging and releases

Installer compilation requires [Inno Setup 6 or 7](https://jrsoftware.org/isinfo.php). Release CI pins and verifies Inno Setup 6.7.2. Packaging publishes the self-contained launcher, includes its licenses and writes bootstrap metadata; it does **not** resolve, download or embed an upstream SourceGit release.

```powershell
./build/package.ps1 -RuntimeIdentifier win-x64 -Version 0.1.1
./build/package.ps1 -RuntimeIdentifier win-arm64 -Version 0.1.1
```

`-SkipInstaller` creates only the ZIP. `-PublishDirectory` accepts an existing clean self-contained launcher publish of the matching architecture and version. A publish containing SourceGit binaries, archives, an installed-mode marker or updater state is rejected. `-SourceGitTag` and `-SourceGitZip` are no longer packaging arguments.

Each package includes `package-metadata.json` with the launcher version, runtime, official SourceGit repository and `download_on_first_launch: true`; it contains no bundled SourceGit tag or archive hash. Artifacts, SHA-256 files and metadata sidecars are written to `artifacts/`. Intermediate payloads remain under `artifacts/.work/` for inspection. The installer's `installed.json` marker selects AppData-based settings; portable archives do not include that marker or a precreated SourceGit data folder.

Verify a portable archive and an x64 installer:

```powershell
./build/verify-package.ps1 -ArchivePath artifacts/SourceGitAutoUpdater_0.1.1_win-x64_portable.zip -RuntimeIdentifier win-x64 -Version 0.1.1
./build/verify-installer.ps1 -InstallerPath artifacts/SourceGitAutoUpdater_0.1.1_win-x64_setup.exe
```

Archive verification rejects any bundled SourceGit payload and checks the self-contained runtime, legal files and bootstrap metadata. Installer acceptance runs inside a fresh `artifacts/` directory, refuses to replace an existing registered installation, and never starts a first download. It first asserts an empty bootstrap installation, then seeds dummy managed files to simulate a completed download, testing reinstall preservation and safe uninstall behavior against traversal and junction entries. Temporary installation registration is removed afterward; logs and preserved sentinels remain for review.

GitHub Actions runs build, core/UI, native Windows and launcher acceptance checks on pushes/PRs. Pushing a tag such as `v0.1.1` builds portable ZIPs and installers for both architectures and publishes a GitHub Release. CI packages no upstream SourceGit version: first launch resolves the latest stable release. Standard GitHub-hosted runners are free for public repositories under the [GitHub Actions billing rules](https://docs.github.com/en/billing/concepts/product-billing/github-actions).

The updater itself is upgraded using a newer ZIP or installer. Automatic updater self-updates are outside this version; SourceGit updates are independent of updater releases.

## Acknowledgements

Thank you to the [SourceGit maintainers and contributors](https://github.com/sourcegit-scm/sourcegit/graphs/contributors) for creating SourceGit and making its source and UI available under the MIT license. This project adapts SourceGit's update dialog, themes, fonts, icons and window styling, and downloads its official releases from [sourcegit-scm/sourcegit](https://github.com/sourcegit-scm/sourcegit).

Thanks also to [Avalonia](https://github.com/AvaloniaUI/Avalonia), [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [.NET](https://github.com/dotnet/runtime), [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) and [Inter](https://github.com/rsms/inter) for the frameworks, controls and fonts used here.

## Licenses

This project is MIT-licensed; see [LICENSE](LICENSE). Original SourceGit copyright and MIT terms are retained in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt), alongside font attribution. Full font licenses are in [licenses/](licenses/). Self-contained release packages retain the redistributed .NET runtime's license and third-party notices there as well. These UI/resource notices remain included even though the official SourceGit application is downloaded separately at runtime.

