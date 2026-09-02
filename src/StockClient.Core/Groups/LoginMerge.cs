namespace StockClient.Core.Groups;

/// <summary>
/// What to do with the LOCAL groups when an account signs in, given who owned
/// the local copy and what the server holds. This is the single riskiest
/// decision in the whole sync path — the wrong branch either inherits a
/// previous user's groups or wipes the current one's — so it is a pure,
/// unit-tested function instead of a chain of ifs buried in the window.
/// </summary>
public static class LoginMerge
{
    public enum Action
    {
        /// <summary>Server has groups → replace the local copy with them.</summary>
        AdoptRemote,

        /// <summary>Server empty AND the local copy belonged to someone else →
        /// clear it; never inherit another account's groups.</summary>
        ClearLocal,

        /// <summary>Server empty, local unowned → keep local, stamp this owner
        /// (the periodic upload will republish it to the server).</summary>
        StampOwnerKeepLocal,

        /// <summary>Server empty, local already owned by this user → leave it.</summary>
        NoChange,
    }

    public static Action Decide(string? localOwner, string loginUser, int remoteGroupCount)
    {
        if (remoteGroupCount > 0) return Action.AdoptRemote;

        var otherOwner = localOwner is not null
            && !string.Equals(localOwner, loginUser, System.StringComparison.OrdinalIgnoreCase);
        if (otherOwner) return Action.ClearLocal;

        return localOwner is null ? Action.StampOwnerKeepLocal : Action.NoChange;
    }
}
