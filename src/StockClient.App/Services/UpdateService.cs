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

    /// <summary>
    /// True when a newer release with a download URL exists — or when the
    /// manifest is forcing a specific version (a rollback), in which case any
    /// difference from the running version counts, downgrades included.
    /// </summary>
    public bool HasUpdate => Release is not null
        && (Release.Force ? Release.Version != Current : Release.Version > Current);
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
        // Proxy off (see DirectHttp): the NAS source is domestic-direct, and a
        // stale process-cached proxy must not silently kill the update loop.
        // GitHub might genuinely benefit from a proxy, but it is only the
        // fallback — reachable or not, the NAS source carries every release.
        _http = DirectHttp.Create(TimeSpan.FromMinutes(3));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QuoteView-Updater");
        _domestic = new DomesticReleaseClient(_http);
        _github = new GithubReleaseClient(_http);
    }

    /// <summary>The running app's version (from the assembly).</summary>
    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build)
            : new Version(0, 0, 0);

    private DateTime _lastGithubTry = DateTime.MinValue;

    /// <param name="force">
    /// True for a manual check: always allowed to hit the GitHub fallback. Auto
    /// checks throttle it to once per 5 minutes — at 30s polling with the NAS
    /// down, an unthrottled fallback would burn GitHub's 60/hr anonymous quota.
    /// </param>
    public async Task<UpdateCheck> CheckAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        // Domestic first; 10s cap per source so a dead one is skipped quickly.
        var release = await TryAsync(ct => _domestic.GetLatestAsync(ct), TimeSpan.FromSeconds(10), cancellationToken);

        if (release is null &&
            (force || DateTime.UtcNow - _lastGithubTry >= TimeSpan.FromMinutes(5)))
        {
            _lastGithubTry = DateTime.UtcNow;
            release = await TryAsync(ct => _github.GetLatestAsync(ct), TimeSpan.FromSeconds(10), cancellationToken);
        }

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
    /// <param name="background">Relaunch without surfacing the main window —
    /// the update happened while the app was minimized or in stealth mode, and
    /// finishing it must not steal the desktop.</param>
    public async Task DownloadAndApplyAsync(
        ReleaseInfo release, IProgress<double>? progress, bool background = false,
        CancellationToken cancellationToken = default)
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("无法定位当前程序路径");

        // Unique per attempt: a fixed ".new" path meant one failed download's
        // leftover (often held briefly by the antivirus scanning it) made every
        // following attempt die within seconds on "file in use".
        var newPath = $"{exe}.{DateTime.Now:yyyyMMddHHmmssfff}.new";
        var old = $"{exe}.{DateTime.Now:yyyyMMddHHmmss}.old";

        try
        {
            await DownloadAsync(release.DownloadUrl, newPath, progress, cancellationToken);
            Verify(newPath, release);

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
        }
        catch
        {
            // Leave no partial download behind — CleanupOld sweeps stragglers
            // at the next launch as a second line of defence.
            try { if (File.Exists(newPath)) File.Delete(newPath); } catch { /* locked */ }
            throw;
        }

        // Past this point the swap has already happened: a failure here is not
        // "the download failed", the new exe IS the installed program. A freshly
        // written unsigned exe being blocked or quarantined by antivirus is
        // routine, and without the restore below the original path can be left
        // with no runnable program at all.
        try
        {
            // --updated: we're still alive for a beat after Process.Start, and the
            // single-instance mutex would make the new copy yield to us and exit.
            // The flag tells it to wait for the mutex instead.
            var args = background ? "--updated --background" : "--updated";
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            var restored = false;
            try
            {
                // Only when the new exe is gone (quarantined): if it survived,
                // it is the newer program and must stay.
                if (!File.Exists(exe) && File.Exists(old))
                {
                    File.Move(old, exe);
                    restored = true;
                }
            }
            catch
            {
                // Nothing left to try — the message below tells the user the truth.
            }

            throw new InvalidOperationException(
                "新版本已写入磁盘，但启动被拦截（多半是杀毒软件）"
                + (restored ? "，已恢复到原版本；" : "；")
                + "请手动重新打开程序，或把 QuoteView 加入杀软白名单。", ex);
        }

        Application.Current.Shutdown();
    }

    /// <summary>
    /// Thin wrapper over the Core verifier: opens the downloaded file and lets
    /// the byte-level checks decide. Failure here means the file never becomes
    /// the exe.
    /// </summary>
    private static void Verify(string path, ReleaseInfo release)
    {
        using var fs = File.OpenRead(path);
        UpdateVerifier.Verify(fs, release.Size, release.Sha256);
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

            foreach (var f in Directory.EnumerateFiles(dir, "*.old")
                         .Concat(Directory.EnumerateFiles(dir, "*.new")))
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
