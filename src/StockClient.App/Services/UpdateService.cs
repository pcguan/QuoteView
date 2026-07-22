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

    /// <summary>The resolved release from whichever source answered, or null.</summary>
    public ReleaseInfo? Release { get; init; }

    /// <summary>True when a newer release with a download URL exists.</summary>
    public bool HasUpdate => Release is not null && Release.Version > Current;
}

/// <summary>
/// Online version check + self-update. Checks the domestic mirror (NAS) first and
/// falls back to GitHub, so it's fast in China but still works if the NAS is down.
///
/// The swap uses the Windows trick of renaming the *running* exe (allowed — the
/// image is held by handle) to a `.old` name, dropping the freshly downloaded exe
/// into the original path, then restarting. Leftover `.old` files are swept next launch.
/// </summary>
public sealed class UpdateService
{
    private readonly HttpClient _http;
    private readonly DomesticReleaseClient _domestic;
    private readonly GithubReleaseClient _github;

    public UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QuoteView-Updater");
        _domestic = new DomesticReleaseClient(_http);
        _github = new GithubReleaseClient(_http);
    }

    /// <summary>The running app's version (from the assembly).</summary>
    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build)
            : new Version(0, 0, 0);

    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        // Domestic first, with a short timeout so a dead NAS doesn't stall the fallback.
        var release = await TryAsync(ct => _domestic.GetLatestAsync(ct), TimeSpan.FromSeconds(8), cancellationToken)
                      ?? await TryAsync(ct => _github.GetLatestAsync(ct), TimeSpan.FromSeconds(15), cancellationToken);

        return new UpdateCheck { Current = Current, Release = release };
    }

    private static async Task<ReleaseInfo?> TryAsync(
        Func<CancellationToken, Task<ReleaseInfo?>> op, TimeSpan timeout, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout);
        try
        {
            return await op(cts.Token);
        }
        catch
        {
            return null; // unreachable / bad response — let the caller fall through
        }
    }

    /// <summary>
    /// Downloads the release's exe and swaps it in, then restarts. On success the
    /// process exits. Throws on failure, leaving the current install untouched.
    /// </summary>
    public async Task DownloadAndApplyAsync(
        ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("无法定位当前程序路径");
        var newPath = exe + ".new";

        await DownloadAsync(release.DownloadUrl, newPath, progress, cancellationToken);

        var old = $"{exe}.{DateTime.Now:yyyyMMddHHmmss}.old";
        File.Move(exe, old);
        try
        {
            File.Move(newPath, exe);
        }
        catch
        {
            File.Move(old, exe); // roll back so the app still starts
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
