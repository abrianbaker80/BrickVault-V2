using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrickVault.Api;

internal sealed record StockCountCommand
{
    public required Guid CommandId { get; init; }
    public required Guid PurchaseId { get; init; }
    public required int ExpectedRevision { get; init; }
    public required Guid PositionId { get; init; }
    public required Guid LocationId { get; init; }
    public required string ConfirmedLocationCode { get; init; }
    public required int CountedQuantity { get; init; }
    public required string Reason { get; init; }

    private static bool TextFits(string? value, int max) => value is { Length: > 0 } &&
        value.Length <= max && value == value.Trim() && !value.Any(char.IsControl);

    public void Validate()
    {
        if (CommandId == Guid.Empty || PurchaseId == Guid.Empty || ExpectedRevision <= 0 ||
            PositionId == Guid.Empty || LocationId == Guid.Empty || CountedQuantity is < 0 or > 1_000_000 ||
            !TextFits(ConfirmedLocationCode, 48) || !TextFits(Reason, 200))
            throw new Fault(400, "invalid_count", "Choose a receipt position and confirm its actual bin, counted quantity and reason.");
    }

    public byte[] Digest()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("count\n"u8);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(this, Wire.Json));
        return hash.GetHashAndReset();
    }
}

internal sealed record CountPosition(Guid PositionId, Guid PurchaseId, string PurchaseReference, Guid DeliveryId,
    string DeliveryReference, int PurchaseRevision, Guid CatalogId, int CatalogRevision, int LedgerQuantity,
    int OfferBackedQuantity, bool ShortageOpen);
internal sealed record StockCountObservation(Guid Id, Guid CommandId, Guid CorrelationId, Guid PurchaseId, Guid PositionId,
    Guid LocationId, string LocationCode, int ExpectedPurchaseRevision, int LedgerQuantity, int OfferBackedQuantity,
    int CountedQuantity, int MissingQuantity, int UnidentifiedSurplusQuantity, string Reason, DateTime ObservedAt,
    bool PostedAdjustment);
internal sealed record StockShortageCase(Guid Id, Guid PositionId, Guid PurchaseId, string State,
    Guid OpeningObservationId, DateTime OpenedAt, int OpeningMissingQuantity, Guid LatestObservationId,
    DateTime LatestObservedAt, int LatestObservedMissingQuantity);
internal sealed record BinCountSnapshot(string Scope, DateTime AsOf, bool Complete, LocationRow Location,
    Guid? PositionId, Guid? NextAfter, IReadOnlyList<CountPosition> Positions, IReadOnlyList<StockCountObservation> Observations,
    IReadOnlyList<StockShortageCase> Shortages,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StockShortageClosureSummary>? Closures = null);
