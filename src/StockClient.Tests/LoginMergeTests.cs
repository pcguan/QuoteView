using StockClient.Core.Groups;
using Xunit;

namespace StockClient.Tests;

public class LoginMergeTests
{
    [Fact]
    public void Server_has_groups_always_adopts_remote()
    {
        Assert.Equal(LoginMerge.Action.AdoptRemote, LoginMerge.Decide(null, "alice", 3));
        Assert.Equal(LoginMerge.Action.AdoptRemote, LoginMerge.Decide("bob", "alice", 1));
    }

    [Fact]
    public void Empty_server_and_someone_elses_local_clears_it()
    {
        // Signing in as alice on a machine whose local groups were bob's, with
        // nothing on the server, must NOT keep bob's groups.
        Assert.Equal(LoginMerge.Action.ClearLocal, LoginMerge.Decide("bob", "alice", 0));
    }

    [Fact]
    public void Empty_server_and_unowned_local_keeps_it_and_stamps_owner()
    {
        // Legacy/offline groups with no owner: adopt them into this account and
        // let the periodic upload publish them.
        Assert.Equal(LoginMerge.Action.StampOwnerKeepLocal, LoginMerge.Decide(null, "alice", 0));
    }

    [Fact]
    public void Empty_server_and_own_local_is_a_noop()
    {
        Assert.Equal(LoginMerge.Action.NoChange, LoginMerge.Decide("alice", "alice", 0));
        Assert.Equal(LoginMerge.Action.NoChange, LoginMerge.Decide("ALICE", "alice", 0));  // case-insensitive
    }
}
