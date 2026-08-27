namespace StockClient.Core.Updates;

/// <summary>
/// A resolved release from whichever source answered — the domestic mirror or
/// GitHub. Reduced to what the updater needs: a version, a direct exe URL, and
/// display text.
/// </summary>
public sealed record ReleaseInfo
{
    /// <summary>Parsed version (tag/manifest version, "v" stripped).</summary>
    public required Version Version { get; init; }

    /// <summary>Direct download URL of the QuoteView.exe for this version.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>Shown in the prompt, e.g. "v1.1.0".</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Release notes / changelog, may be empty.</summary>
    public string Notes { get; init; } = "";

    /// <summary>Where it came from, e.g. "国内(NAS)" / "GitHub".</summary>
    public string Source { get; init; } = "";

    /// <summary>Exact exe size in bytes when the source reports it, else 0.
    /// The updater refuses a download whose length disagrees.</summary>
    public long Size { get; init; }
}
