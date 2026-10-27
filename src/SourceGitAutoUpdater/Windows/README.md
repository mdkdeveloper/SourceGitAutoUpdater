# Windows integration boundary

The helper applies an explicit, versionless taskbar identity and relaunch metadata to windows whose owning process executable matches the managed SourceGit path. It never injects code, changes window parents, patches executable files, or pins shortcuts automatically.

Call `TaskbarIntegration.Start`, `Refresh`, and `Dispose` on the same UI/message-loop thread. `LauncherInstanceBroker.LaunchRequested` runs on its listener task and must be dispatched to the UI coordinator. Subscribe before calling `Start`. Keep the broker alive for the full managed SourceGit session.

Run `pwsh -File src/SourceGitAutoUpdater/Windows/Verify.ps1` from the repository root for native hidden-window, shortcut and IPC probes. The probe changes only its own process/window properties and creates temporary probe artifacts and a per-installation broker lock file under the current user's LocalAppData.

Real taskbar behavior must also be checked against an actual SourceGit release: cold start, existing-instance handoff, history/blame windows, pinning a running window, relaunch after all processes close, update at the stable executable path, and Explorer restart. The helper intentionally permits brief regrouping lag after window creation. Same-user, same-integrity execution is the supported baseline; independently elevated SourceGit cannot be reliably relabelled by a normal helper.

The helper preserves and restores original window properties when detaching or receiving HIDE. Native out-of-context DESTROY events arrive after destruction; an externally monitored process does not provide a guaranteed pre-destruction cleanup callback. Properties on independently destroyed windows therefore cannot always be cleared before closure. No seamless grouping or complete pre-close cleanup guarantee is claimed for this external integration approach.

`InstallationGate` is a separate shared/exclusive file lease per installation. Direct editor/history/blame invocations hold a shared lease for the child lifetime. Recovery, settings mutations, imports and the short installation commit take an exclusive lease; network downloads and confirmation dialogs must remain outside it. Exclusive acquisition is nonblocking; shared acquisition waits with cancellation. This gate coordinates launcher-mediated invocations only: processes launched by executing SourceGit.exe directly do not participate, so also inspect managed processes and SourceGit's own `process.lock` before mutation.
