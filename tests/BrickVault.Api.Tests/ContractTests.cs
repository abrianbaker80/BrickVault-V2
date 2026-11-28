using System.Net;
using System.Security.Cryptography;
using System.Text;
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

    [Fact]
    public void OmittedOfferCurrencyRetainsOriginalWireDigestAndExplicitCurrencyBindsThePrice()
    {
        var id = Guid.Parse(StableId);
        // v15 serialized the derived properties first, followed by the purchase and command identities.
        var prior = new { CatalogId = id, UnitPriceMinor = 175, Backing = new[] { new BackingRow(id, 1) },
            PurchaseId = id, CommandId = id, ExpectedRevision = 3 };
        var json = JsonSerializer.Serialize(prior, Wire.Json);
        var legacy = JsonSerializer.Deserialize<OfferCommand>(json, Wire.Json)!;
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes("offer\n" + json)), legacy.Digest());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OfferCommand>(json[..^1] + ",\"currency\":null}", Wire.Json));
        foreach (var invalid in new[] { "", "eur", "EU", "EURO", "USD ", "ÄBC" })
            Assert.Equal("invalid_receiving", Assert.Throws<Fault>((legacy with { Currency = invalid }).Validate).Code);
        Assert.NotEqual(legacy.Digest(), (legacy with { Currency = "USD" }).Digest());
        Assert.NotEqual((legacy with { Currency = "USD" }).Digest(), (legacy with { Currency = "JPY" }).Digest());
    }

    [Fact]
    public void OnlyOneRecognizedCapabilityAllowsAmbiguousOfferScales()
    {
        var headers = new HeaderDictionary();
        Assert.False(OfferPriceScale.Supported(headers));
        headers[OfferPriceScale.Header] = "2";
        Assert.False(OfferPriceScale.Supported(headers));
        headers[OfferPriceScale.Header] = "1";
        Assert.True(OfferPriceScale.Supported(headers));
        headers.Append(OfferPriceScale.Header, "1");
        Assert.False(OfferPriceScale.Supported(headers));
        foreach (var currency in new[] { "USD", "EUR", "JPY", "KWD" })
        {
            OfferPriceScale.CheckNewOffer(currency, explicitlyPriced: false, supported: false);
            Assert.Null(OfferPriceScale.Verified(currency, recorded: true));
        }
        foreach (var currency in new[] { "HUF", "IQD", "MGA", "BYR", "XAU", "ZZZ" })
        {
            Assert.Equal(426, Assert.Throws<Fault>(() => OfferPriceScale.CheckNewOffer(currency, true, false)).Status);
            Assert.Equal("offer_price_reentry_required",
                Assert.Throws<Fault>(() => OfferPriceScale.CheckNewOffer(currency, false, true)).Code);
            OfferPriceScale.CheckNewOffer(currency, explicitlyPriced: true, supported: true);
            Assert.True(OfferPriceScale.Verified(currency, recorded: true));
            Assert.Null(OfferPriceScale.Verified(currency, recorded: false));
        }
    }

    [Fact]
    public void StockLossUnknownAttributionRetainsLegacyDigestAndZeroIsAffirmative()
    {
        var stable = Guid.Parse(StableId);
        var prior = new
        {
            CommandId = stable, PurchaseId = stable, ExpectedRevision = 7, PositionId = stable, LocationId = stable,
            ConfirmedLocationCode = "A1", CaseId = stable, ExpectedObservationId = stable, Reason = "Witnessed fixture",
            OfferReductions = new[] { new TransferBacking(stable, 1) }
        };
        var body = JsonSerializer.Serialize(prior, Wire.Json);
        var legacyDigest = SHA256.HashData(Encoding.UTF8.GetBytes("stock-loss\n" + body));
        var omitted = JsonSerializer.Deserialize<StockLossCommand>(body, Wire.Json)!;
        var explicitNull = JsonSerializer.Deserialize<StockLossCommand>(body[..^1] + ",\"quarantineAttributions\":null}", Wire.Json)!;
        var affirmativeZero = JsonSerializer.Deserialize<StockLossCommand>(body[..^1] + ",\"quarantineAttributions\":[]}", Wire.Json)!;
        Assert.Equal(legacyDigest, omitted.Digest());
        Assert.Equal(legacyDigest, explicitNull.Digest());
        Assert.NotEqual(legacyDigest, affirmativeZero.Digest());
        Assert.Empty(affirmativeZero.QuarantineAttributions!);
        var tooMany = affirmativeZero with { QuarantineAttributions = [new(stable, 1_000_000)] };
        Assert.Equal("invalid_stock_loss", Assert.Throws<Fault>(tooMany.Validate).Code);
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
        var gateway = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
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

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("1", null, false)]
    [InlineData("3", null, false)]
    [InlineData("abc", null, false)]
    [InlineData("2, 2", null, false)]
    [InlineData("2", "2", false)]
    [InlineData("2", null, true)]
    public void CapacityRejectionsNegotiateTheStructuredWitness(string? first, string? second, bool current)
    {
        const string header = "X-BV-Location-Contract";
        var headers = new HeaderDictionary();
        if (first is not null) headers.Append(header, first);
        if (second is not null) headers.Append(header, second);
        var rejection = new LocationCapacityRejection(Guid.NewGuid(), 5, 4, 1, 2);
        var correlation = Guid.NewGuid();
        var fault = new Fault(409, "location_capacity_exceeded", "The bin has too little room.", rejection);
        var response = LocationCapacities.FormatError(fault, correlation, headers);
        var wire = JsonSerializer.SerializeToElement(response, Wire.Json);

        Assert.Equal(correlation, wire.GetProperty("correlationId").GetGuid());
        Assert.Equal(fault.Message, wire.GetProperty("message").GetString());
        if (current)
        {
            Assert.Equal("location_capacity_exceeded", wire.GetProperty("code").GetString());
            Assert.Equal(rejection, wire.GetProperty("capacity").Deserialize<LocationCapacityRejection>(Wire.Json));
        }
        else
        {
            Assert.Equal("quantity_unavailable", wire.GetProperty("code").GetString());
            Assert.Equal(3, wire.EnumerateObject().Count());
        }
    }

    private static LocationCommand ReadLocation(string json) => JsonSerializer.Deserialize<LocationCommand>(json, Wire.Json)!;
}
