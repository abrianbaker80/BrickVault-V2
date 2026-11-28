using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    // A null value has the historical wire digest. An empty array is an affirmative zero.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StockLossQuarantineSelection[]? QuarantineAttributions { get; init; }

    public void Validate()
    {
        if (CommandId == Guid.Empty || PurchaseId == Guid.Empty || PositionId == Guid.Empty || LocationId == Guid.Empty ||
            CaseId == Guid.Empty || ExpectedObservationId == Guid.Empty || ExpectedRevision < 1 ||
            !TextFits(ConfirmedLocationCode, 48) || !TextFits(Reason, 200) || OfferReductions is null ||
            OfferReductions.Length > 1_000_000 || QuarantineAttributions is { Length: > 1_000_000 }) throw Invalid();

        HashSet<Guid> offers = [];
        var remaining = 1_000_000;
        foreach (var line in OfferReductions)
        {
            if (line is null || line.OfferId == Guid.Empty || line.Quantity <= 0 || line.Quantity > remaining ||
                !offers.Add(line.OfferId)) throw Invalid();
            remaining -= line.Quantity;
        }
        if (QuarantineAttributions is not { } attributions) return;
        HashSet<Guid> quarantines = [];
        foreach (var line in attributions)
        {
            if (line is null || line.QuarantineId == Guid.Empty || line.Quantity <= 0 || line.Quantity > remaining ||
                !quarantines.Add(line.QuarantineId)) throw Invalid();
            remaining -= line.Quantity;
        }
    }

    private static bool TextFits(string? value, int max) => value is { Length: > 0 } &&
        value.Length <= max && value == value.Trim() && !value.Any(char.IsControl);
    private static Fault Invalid() => new(400, "invalid_stock_loss",
        "Confirm the bin, reviewed observation and reason, and supply distinct positive offer reductions and quarantine attributions.");

    public byte[] Digest()
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        stream.Write("stock-loss\n"u8);
        // Each selected offer or quarantine receipt carries one quantity regardless of display order.
        var ordered = this with
        {
            OfferReductions = OfferReductions.OrderBy(row => row.OfferId).ToArray(),
            QuarantineAttributions = QuarantineAttributions?.OrderBy(row => row.QuarantineId).ToArray()
        };
        JsonSerializer.Serialize(stream, ordered, Wire.Json);
        stream.FlushFinalBlock();
        return hash.Hash!;
    }
}

internal sealed record StockLossQuarantineSelection([property: JsonRequired] Guid QuarantineId, [property: JsonRequired] int Quantity);
internal sealed record StockLossQuarantineAttribution(Guid QuarantineId, int Quantity, int PriorRemainingQuantity, int RemainingQuantity);

internal sealed record StockLossReduction(Guid OfferId, int Quantity, int PriorBackedQuantity,
    int RemainingBackedQuantity, int UnitPriceMinor, string Currency,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PriceScaleVerified = null);

internal sealed record StockLossSnapshot(string Scope, DateTime AsOf, bool Complete, LocationRow Location,
    Guid PositionId, Guid CaseId, Guid PurchaseId, Guid PostingId, Guid CommandId, Guid CorrelationId, int Revision,
    StockCountObservation ReviewedObservation, AdjustmentReceiptEvidence Receipt, string ConfirmedLocationCode,
    string Reason, DateTime PostedAt, int LedgerQuantityBefore, int LedgerQuantityAfter, int LostQuantity,
    int ProtectedProjectQuantity, int OfferBackedQuantityBefore, int OfferBackedQuantityAfter, int PriorDeliveryLossQuantity,
    int? AllocatedItemCostMinor, int? RemainingDeliveryItemCostMinor, IReadOnlyList<StockLossReduction> Reductions,
    string? NextAfter,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? QuarantineAttributionRecorded = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? QuarantinedQuantityBefore = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? QuarantinedQuantityAfter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<StockLossQuarantineAttribution>? QuarantineAttributions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextQuarantineAfter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PriorDeliveryLossQuantityMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PriorTransformedQuantity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PriorTransformedItemCostMinor = null);
