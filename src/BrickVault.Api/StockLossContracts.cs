using System.Security.Cryptography;
using System.Text.Json;

namespace BrickVault.Api;

internal sealed record StockLossCommand
{
    public required Guid CommandId { get; init; }
    public required Guid PurchaseId { get; init; }
    public required int ExpectedRevision { get; init; }
    public required Guid PositionId { get; init; }
    public required Guid LocationId { get; init; }
    public required string ConfirmedLocationCode { get; init; }
    public required Guid CaseId { get; init; }
    public required Guid ExpectedObservationId { get; init; }
    public required string Reason { get; init; }
    public required TransferBacking[] OfferReductions { get; init; }

    public void Validate()
    {
        if (CommandId == Guid.Empty || PurchaseId == Guid.Empty || PositionId == Guid.Empty || LocationId == Guid.Empty ||
            CaseId == Guid.Empty || ExpectedObservationId == Guid.Empty || ExpectedRevision < 1 ||
            !TextFits(ConfirmedLocationCode, 48) || !TextFits(Reason, 200) || OfferReductions is null ||
            OfferReductions.Length > 1_000_000) throw Invalid();

        HashSet<Guid> offers = [];
        var remaining = 1_000_000;
        foreach (var line in OfferReductions)
        {
            if (line is null || line.OfferId == Guid.Empty || line.Quantity <= 0 || line.Quantity > remaining ||
                !offers.Add(line.OfferId)) throw Invalid();
            remaining -= line.Quantity;
        }
    }

    private static bool TextFits(string? value, int max) => value is { Length: > 0 } &&
        value.Length <= max && value == value.Trim() && !value.Any(char.IsControl);
    private static Fault Invalid() => new(400, "invalid_stock_loss",
        "Confirm the bin, reviewed observation and reason, and supply distinct positive offer reductions.");

    public byte[] Digest()
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        stream.Write("stock-loss\n"u8);
        // The operator chooses one quantity per offer. Display order is not part of that decision.
        var ordered = this with { OfferReductions = OfferReductions.OrderBy(row => row.OfferId).ToArray() };
        JsonSerializer.Serialize(stream, ordered, Wire.Json);
        stream.FlushFinalBlock();
        return hash.Hash!;
    }
}

internal sealed record StockLossReduction(Guid OfferId, int Quantity, int PriorBackedQuantity,
    int RemainingBackedQuantity, int UnitPriceMinor, string Currency);

internal sealed record StockLossSnapshot(string Scope, DateTime AsOf, bool Complete, LocationRow Location,
    Guid PositionId, Guid CaseId, Guid PurchaseId, Guid PostingId, Guid CommandId, Guid CorrelationId, int Revision,
    StockCountObservation ReviewedObservation, AdjustmentReceiptEvidence Receipt, string ConfirmedLocationCode,
    string Reason, DateTime PostedAt, int LedgerQuantityBefore, int LedgerQuantityAfter, int LostQuantity,
    int ProtectedProjectQuantity, int OfferBackedQuantityBefore, int OfferBackedQuantityAfter, int PriorDeliveryLossQuantity,
    int? AllocatedItemCostMinor, int? RemainingDeliveryItemCostMinor, IReadOnlyList<StockLossReduction> Reductions,
    string? NextAfter);
