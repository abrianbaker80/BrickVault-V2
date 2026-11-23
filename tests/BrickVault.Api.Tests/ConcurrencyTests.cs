using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

public sealed class ConcurrencyTests
{
    // The qualification runner creates and grants this disposable database independently.
    // Setting BV_DB for a normal foundation or a restored owner workspace cannot opt it into these tests.
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task ConcurrentReplaysAndRevisionsCommitExactlyOnce()
    {
        var cs = Environment.GetEnvironmentVariable("BV_TEST_DB");
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "BV_TEST_DB with a dedicated bv_m2_test_ database is required.");
        var settings = new NpgsqlConnectionStringBuilder(cs);
        Assert.True(settings.Host is "127.0.0.1" or "::1" && settings.Username == "bv_api" &&
            settings.Database?.StartsWith("bv_m2_test_", StringComparison.Ordinal) == true,
            "The concurrency test requires an isolated loopback test database and application role.");
        var token = TestContext.Current.CancellationToken;
        await using var db = NpgsqlDataSource.Create(cs!);
        await Migration.Check(db, token);
        var references = new References(db);
        var create = new LocationCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Code = "T-" + Guid.NewGuid().ToString("N"), Name = "Shelf"
        };
        var receipts = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => references.Save(create, Guid.NewGuid(), token)));
        var first = Assert.Single(receipts.Distinct());
        var different = await Assert.ThrowsAsync<Fault>(() => references.Save(create with { Name = "Other" }, Guid.NewGuid(), token));
        Assert.Equal("command_mismatch", different.Code);
        async Task<Receipt?> Compete(string name)
        {
            var command = create with { CommandId = Guid.NewGuid(), Id = first.EntityId, ExpectedRevision = 1, Name = name };
            try { return await references.Save(command, Guid.NewGuid(), token); }
            catch (Fault e) when (e.Code == "revision_conflict") { return null; }
        }
        var results = await Task.WhenAll(Compete("Upper"), Compete("Lower"));
        Assert.Equal(2, Assert.Single(results.OfType<Receipt>()).Revision);
        var workspace = await references.Workspace(token);
        Assert.Equal(2, Assert.Single(workspace.Locations, row => row.Id == first.EntityId).Revision);
        Assert.Equal(2, workspace.Audit.Count(row => row.EntityId == first.EntityId));
    }
}
