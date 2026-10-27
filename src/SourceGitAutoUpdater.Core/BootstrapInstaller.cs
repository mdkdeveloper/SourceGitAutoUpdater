namespace SourceGitAutoUpdater.Core;

/// <summary>
/// Performs one first-run installation attempt. The caller owns SourceGitLease and
/// coordinates launcher mutations through the validated checkpoint. Failed calls may
/// be retried; every call re-queries the official latest release. Preferences are never
/// changed here, so a cancelled/failed bootstrap leaves native settings intact.
/// </summary>
public sealed class BootstrapInstaller : IDisposable
{
    private readonly AppPaths _paths;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Action<string>? _checkpoint;
    private readonly Action<ReleaseInfo>? _releaseResolved;

    public BootstrapInstaller(AppPaths paths, HttpClient? client = null, Action<string>? checkpoint = null, Action<ReleaseInfo>? releaseResolved = null)
    {
        _paths = paths;
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _checkpoint = checkpoint;
        _releaseResolved = releaseResolved;
    }

    public async Task<ReleaseInfo> InstallLatestAsync(IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new UpdateProgress("Checking", 0, null));
        var release = await new ReleaseService(_client).GetLatestAsync(cancellationToken)
            ?? throw new InvalidDataException("No stable official SourceGit release is available.");
        cancellationToken.ThrowIfCancellationRequested();
        _releaseResolved?.Invoke(release);
        // Bootstrap does not consult IgnoreUpdateTag: without SourceGit there is no
        // installed version to launch instead. Skip applies only to later updates.
        await new UpdaterEngine(_paths, _client, _checkpoint).InstallAsync(release, progress, cancellationToken);
        return release;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}

