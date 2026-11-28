using System.Collections.Frozen;
using System.Text.Json;

namespace BrickVault.Api;

internal static class OfferPriceScale
{
    internal const string Header = "X-BrickVault-Price-Scale";

    // Intersection of numeric ISO 4217 List One (2026-09-17) scales and the retained
    // formatter in Firefox 159, Chrome 157 and Edge 159. Unknown codes require a marked client.
    private static readonly FrozenSet<string> LegacyCurrencies = ReadCurrencies();
    private static FrozenSet<string> ReadCurrencies()
    {
        using var data = typeof(OfferPriceScale).Assembly.GetManifestResourceStream(
            "BrickVault.Api.legacy-offer-price-currencies.json")!;
        return JsonSerializer.Deserialize<string[]>(data)!.ToFrozenSet(StringComparer.Ordinal);
    }

    internal static bool Supported(IHeaderDictionary headers) =>
        headers[Header] is { Count: 1 } value && value[0] == "1";

    internal static bool? Verified(string currency, bool recorded) =>
        recorded && !LegacyCurrencies.Contains(currency) ? true : null;

    internal static void CheckNewOffer(string currency, bool explicitlyPriced, bool supported)
    {
        if (LegacyCurrencies.Contains(currency)) return;
        if (!supported) throw Upgrade();
        if (!explicitlyPriced)
            throw new Fault(409, "offer_price_reentry_required",
                "Re-enter the selling currency and unit price in the updated Workbench before saving this draft.");
    }

    private static Fault Upgrade() => new(426, "offer_price_client_upgrade_required",
        "Update and reload Workbench to review the selling currency and its price precision.");

    internal static RouteHandlerBuilder WithOfferPrices(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            if (Supported(context.HttpContext.Request.Headers)) return result;
            // Inspect only records actually returned, after pagination and scope verification.
            IEnumerable<string> currencies = result switch
            {
                ReceivingWorkspace page => page.Purchases.SelectMany(p => p.Offers).Select(o => o.Currency),
                LossReceivingWorkspace page => page.Purchases.SelectMany(p => p.Offers).Select(o => o.Currency),
                BinTransferSnapshot page => page.Positions.SelectMany(p => p.Backing)
                    .Concat(page.Movements.SelectMany(m => m.Backing)).Select(o => o.Currency),
                DraftBackingSnapshot page => page.Offers.Select(o => o.Currency),
                DraftBackingHistory page => [page.Offer.Currency],
                DraftBackingWithdrawalEvidence receipt => [receipt.Currency],
                StockAdjustmentReview page => page.Offers.Select(o => o.Currency),
                StockLossSnapshot page => page.Reductions.Select(o => o.Currency),
                _ => throw new InvalidOperationException("Unknown offer-price projection.")
            };
            if (currencies.Any(code => !LegacyCurrencies.Contains(code))) throw Upgrade();
            return result;
        });
}
