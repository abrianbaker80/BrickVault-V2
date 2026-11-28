using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed record OfferSourceCommand([property: JsonRequired] Guid PurchaseId,
    [property: JsonRequired] int ExpectedRevision, [property: JsonRequired] BackingRow[] Backing);

internal sealed record PooledOfferCommand : OfferCommand
{
    public required int ExpectedCatalogRevision { get; init; }
    public required OfferSourceCommand[] AdditionalSources { get; init; }

    internal IEnumerable<OfferSourceCommand> Sources =>
        AdditionalSources.Prepend(new OfferSourceCommand(PurchaseId, ExpectedRevision, Backing));

    public override byte[] Digest() => PooledOfferConfirmation.Digest(this);

    protected override bool Valid()
    {
        if (!base.Valid() || Currency is null || ExpectedCatalogRevision < 1 ||
            AdditionalSources is not { Length: >= 1 and < 64 } ||
            AdditionalSources.Any(source => source is null || source.PurchaseId == Guid.Empty || source.ExpectedRevision < 1 ||
                source.Backing is not { Length: >= 1 and <= 64 } ||
                source.Backing.Any(row => row is null || row.PlacementId == Guid.Empty || !ValidQuantity(row.Quantity))))
            return false;
        var sources = Sources.ToList();
        var lines = sources.SelectMany(source => source.Backing).ToList();
        return sources.Select(source => source.PurchaseId).Distinct().Count() == sources.Count &&
            lines.Count <= 64 && lines.Select(line => line.PlacementId).Distinct().Count() == lines.Count;
    }
}

internal sealed record OfferSource(Guid PurchaseId, IReadOnlyList<BackingRow> Backing, int BackedQuantity);
internal sealed record OfferPool(Guid OriginPurchaseId, int TotalBackedQuantity, IReadOnlyList<OfferSource> Sources);

internal static class PooledOffers
{
    internal static bool Supported(HttpRequest request, bool receiving = false) =>
        request.Query["pooledOffers"] == "true" && (!receiving || (request.Query["losses"] == "true" &&
            request.Query["quarantine"] == "true" && request.Query["recoveries"] == "true" && request.Query["partOuts"] == "true"));

    internal static RouteHandlerBuilder WithPoolProof(this RouteHandlerBuilder route, bool receiving = false) =>
        route.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            if (Supported(context.HttpContext.Request, receiving))
                context.HttpContext.Response.Headers["X-BrickVault-Pooled-Offers"] = "1";
            return result;
        });

    internal static Fault Upgrade() => new(426, "pooled_offer_client_upgrade_required",
        "Update Workbench to review the purchases and quantities backing this offer.");

    internal static async Task<int> AdvanceSources(NpgsqlConnection connection, NpgsqlTransaction tx,
        PooledOfferCommand offer, CancellationToken token)
    {
        // Every writer that changes stock fences its purchase. Lock the complete set in PostgreSQL
        // UUID order before advancing any revision, even when another operator starts from a different purchase.
        var sources = offer.Sources.ToDictionary(source => source.PurchaseId);
        var locked = await Rows(connection, tx, """
            SELECT id,revision FROM brickvault.purchases WHERE id=ANY($1) ORDER BY id FOR UPDATE
            """, row => (Id: row.GetGuid(0), Revision: row.GetInt32(1)), token, sources.Keys.ToArray());
        if (locked.Count != sources.Count || locked.Any(row => sources[row.Id].ExpectedRevision != row.Revision))
            throw new Fault(409, "revision_conflict", "A selected purchase changed. Review all source purchases and quantities.");
        await using var update = Query(connection, tx,
            "UPDATE brickvault.purchases SET revision=revision+1 WHERE id=ANY($1)", sources.Keys.ToArray());
        await update.ExecuteNonQueryAsync(token);
        return checked(offer.ExpectedRevision + 1);
    }
}
