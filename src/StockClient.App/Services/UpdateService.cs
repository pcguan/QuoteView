using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using StockClient.Core.Updates;

namespace StockClient.App.Services;

/// <summary>Outcome of an update check.</summary>
public sealed record UpdateCheck
{
    public required Version Current { get; init; }
    public GithubRelease? Release { get; init; }
    public Version? Latest { get; init; }

    /// <summary>True when a newer release with a downloadable exe exists.</summary>
    public bool HasUpdate =>
        Release is not null && Latest is not null && Latest > Current && Release.ExeUrl is not null;
}

/// <summary>
/// Online version check + self-update against GitHub Releases.
///
/// Update swap uses the Windows trick of renaming the *running* exe (allowed — the
/// image is held by handle, the directory entry is free to move) out to a `.old`
/// name, dropping the freshly downloaded exe into the original path, then
/// restarting. Leftover `.old` files are swept on the next launch.
/// </summary>
public sealed class UpdateService
{
    private readonly HttpClient _http;
    private readonly GithubReleaseClient _client;

    public UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QuoteView-Updater");
        _client = new GithubReleaseClient(_http);
    }

    /// <summary>The running app's version (from the assembly).</summary>
    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build)
            : new Version(0, 0, 0);

    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        var release = await _client.GetLatestAsync(cancellationToken);
        return new UpdateCheck
        {
            Current = Current,
            Release = release,
            Latest = release?.Version,
        };
    }

    /// <summary>
    /// Downloads the release's exe and swaps it in, then restarts. On success the
    /// process exits (the caller's UI thread won't return here). Throws on failure,
    /// leaving the current install untouched.
    /// </summary>
    public async Task DownloadAndApplyAsync(
        GithubRelease release, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        if (release.ExeUrl is null) throw new InvalidOperationException("该版本没有可下载的 exe");

        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("无法定位当前程序路径");
        var newPath = exe + ".new";

        await DownloadAsync(release.ExeUrl, newPath, progress, cancellationToken);

        // Swap: move the running exe aside (unique name so a locked leftover can't
        // block us), then the new exe into place.
        var old = $"{exe}.{DateTime.Now:yyyyMMddHHmmss}.old";
        File.Move(exe, old);
        try
        {
            File.Move(newPath, exe);
        }
        catch
        {
            // Roll back so the app still starts next time.
            File.Move(old, exe);
            throw;
        }

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    private async Task DownloadAsync(
        string url, string dest, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var src = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var dst = File.Create(dest);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }
    }

    /// <summary>Deletes leftover *.old files next to the exe. Safe to call at startup.</summary>
    public static void CleanupOld()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            var dir = Path.GetDirectoryName(exe);
            if (dir is null) return;

            foreach (var f in Directory.EnumerateFiles(dir, "*.old"))
            {
                try { File.Delete(f); } catch { /* still locked; next launch */ }
            }
        }
        catch
        {
            // Housekeeping only.
        }
    }
}
