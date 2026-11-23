using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

public sealed class ContractTests
{
    private const string StableId = "4aa585b5-8599-4590-803a-a14fa87f140b";

    [Fact]
    public void EquivalentWireCommandsRetainReplayIdentity()
    {
        var first = ReadLocation($$"""{"commandId":"{{StableId}}","expectedRevision":0,"code":"A1","name":"Shelf"}""");
        var reordered = ReadLocation($$"""{"name":"Shelf","id":null,"code":"A1","expectedRevision":0,"commandId":"{{StableId}}"}""");
        Assert.Equal(first.Digest("location"), reordered.Digest("location"));
        Assert.NotEqual(first.Digest("location"), (first with { Name = "Other" }).Digest("location"));
        Assert.NotEqual(first.Digest("location"), first.Digest("catalog"));
    }

    [Theory]
    [InlineData("\"code\":\"A1\",\"code\":\"B2\",\"name\":\"Shelf\",\"expectedRevision\":0")]
    [InlineData("\"code\":\"A1\",\"name\":\"Shelf\",\"expectedRevision\":\"0\"")]
    [InlineData("\"code\":\"A1\",\"name\":\"Shelf\",\"expectedRevision\":0,\"futureField\":true")]
    [InlineData("\"code\":\"A1\",\"name\":\"Shelf\"")]
    public void RejectsAmbiguousOrIncompatibleWireRequests(string fields)
    {
        var body = "{\"commandId\":\"" + StableId + "\"," + fields + "}";
        Assert.Throws<JsonException>(() => ReadLocation(body));
    }

    [Fact]
    public void IdentityAndUnknownColorRemainDistinct()
    {
        var catalog = new CatalogCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, ItemType = "part", ItemNumber = "001a",
            Variant = "b", ColorId = 0, ColorState = "known", Name = "Reference sample", Condition = "used",
            Completeness = "not-applicable", Source = "operator", License = "operator-owned", Mapping = "confirmed"
        };
        catalog.Validate();
        var unknown = catalog with { ColorId = null, ColorState = "unknown" };
        unknown.Validate();
        Assert.NotEqual(catalog.Digest("catalog"), unknown.Digest("catalog"));
        Assert.NotEqual(catalog.Digest("catalog"), (catalog with { ItemNumber = "1a" }).Digest("catalog"));
        Assert.Throws<Fault>(() => (unknown with { ColorId = 0 }).Validate());
        Assert.Throws<Fault>(() => (catalog with { ExpectedRevision = 1 }).Validate());
    }

    [Fact]
    public void GatewayAndOriginRequireAnExactSingleValue()
    {
        using var db = NpgsqlDataSource.Create("Host=127.0.0.1");
        var origin = "https://localhost:443";
        var gateway = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var sessions = new Sessions(db, new GatewaySettings(origin, gateway, new byte[32],
            new IPEndPoint(IPAddress.Loopback, 0), Guid.NewGuid()));
        var ctx = new DefaultHttpContext();
        Assert.False(sessions.TrustedGateway(ctx));
        ctx.Request.Headers["X-BrickVault-Gateway"] = gateway;
        Assert.True(sessions.TrustedGateway(ctx));
        Assert.False(sessions.TrustedReadiness(ctx));
        ctx.Request.Headers["X-BrickVault-Readiness"] = "wrong-proof";
        Assert.False(sessions.TrustedReadiness(ctx));
        ctx.Request.Headers["X-BrickVault-Readiness"] = gateway;
        Assert.True(sessions.TrustedReadiness(ctx));
        ctx.Request.Headers.Append("X-BrickVault-Readiness", gateway);
        Assert.False(sessions.TrustedReadiness(ctx));
        ctx.Request.Headers.Append("X-BrickVault-Gateway", gateway);
        Assert.False(sessions.TrustedGateway(ctx));
        ctx.Request.Headers.Origin = origin;
        sessions.RequireOrigin(ctx);
        ctx.Request.Headers.Append("Origin","https://example.invalid");
        Assert.Equal("origin_denied", Assert.Throws<Fault>(() => sessions.RequireOrigin(ctx)).Code);
    }

    [Fact]
    public void ReferenceCommandsRequireTheirSingleAuthenticatedWorkspaceId()
    {
        var id = Guid.NewGuid();
        var session = new Session("operator", "csrf", id);
        var headers = new HeaderDictionary();
        Assert.Equal("workspace_mismatch", Assert.Throws<Fault>(() => session.RequireWorkspace(headers)).Code);
        headers["X-Workspace-Id"] = "invalid";
        Assert.Throws<Fault>(() => session.RequireWorkspace(headers));
        headers["X-Workspace-Id"] = Guid.NewGuid().ToString("D");
        Assert.Equal(409, Assert.Throws<Fault>(() => session.RequireWorkspace(headers)).Status);
        headers["X-Workspace-Id"] = id.ToString("D");
        session.RequireWorkspace(headers);
        headers.Append("X-Workspace-Id", id.ToString("D"));
        Assert.Throws<Fault>(() => session.RequireWorkspace(headers));
    }

    private static LocationCommand ReadLocation(string json) => JsonSerializer.Deserialize<LocationCommand>(json, Wire.Json)!;
}
