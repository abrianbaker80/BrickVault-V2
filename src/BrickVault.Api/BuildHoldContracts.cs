using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrickVault.Api;

internal abstract record BuildHoldCommand
{
    public required Guid CommandId { get; init; }
    public required Guid PurchaseId { get; init; }
    public required int ExpectedRevision { get; init; }
    public required int Quantity { get; init; }
    public required string Reason { get; init; }

    [JsonIgnore]
    public abstract string Kind { get; }

    protected static bool TextFits(string? value, int max) => value is { Length: > 0 } &&
        value.Length <= max && value == value.Trim() && !value.Any(char.IsControl);

    public void Validate()
    {
        if (CommandId == Guid.Empty || PurchaseId == Guid.Empty || ExpectedRevision <= 0 ||
            Quantity is < 1 or > 1_000_000 || !TextFits(Reason, 200) || !ValidSelection())
            throw new Fault(400, "invalid_hold", "Confirm the named hold or position, quantity, reason and any open shortage evidence.");
    }

    protected abstract bool ValidSelection();

    public byte[] Digest()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(Kind + "\n"));
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(this, GetType(), Wire.Json));
        return hash.GetHashAndReset();
    }
}

internal sealed record StockHoldCommand : BuildHoldCommand
{
    public required Guid PositionId { get; init; }
    public required Guid LocationId { get; init; }
    public required string ConfirmedLocationCode { get; init; }
    public required string Purpose { get; init; }

    [JsonIgnore]
    public override string Kind => "hold";

    protected override bool ValidSelection() => PositionId != Guid.Empty && LocationId != Guid.Empty &&
        TextFits(ConfirmedLocationCode, 48) && TextFits(Purpose, 120);
}

internal sealed record StockHoldReleaseCommand : BuildHoldCommand
{
    public required Guid HoldId { get; init; }
    public Guid? ShortageId { get; init; }
    public Guid? ExpectedObservationId { get; init; }

    [JsonIgnore]
    public override string Kind => "hold-release";

    protected override bool ValidSelection() => HoldId != Guid.Empty && ShortageId != Guid.Empty &&
        ExpectedObservationId != Guid.Empty && ShortageId.HasValue == ExpectedObservationId.HasValue;
}

internal sealed record StockHoldRow(Guid Id, Guid CommandId, Guid CorrelationId, string Purpose, string Reason,
    int Quantity, int ReleasedQuantity, int RemainingQuantity, DateTime CreatedAt);
internal sealed record StockHoldPositionSnapshot(string Scope, DateTime AsOf, bool Complete, LocationRow Location,
    Guid PositionId, Guid PurchaseId, int PurchaseRevision, int LedgerQuantity, int OfferBackedQuantity,
    int ReservedQuantity, int FreeQuantity, int AvailableToHoldQuantity, Guid? ShortageId, Guid? LatestObservationId,
    IReadOnlyList<StockHoldRow> Holds, Guid? NextAfter);
internal sealed record StockHoldReleaseRow(Guid Id, Guid CommandId, Guid CorrelationId, int Quantity, string Reason,
    Guid? ShortageId, Guid? ExpectedObservationId, DateTime ReleasedAt);
internal sealed record StockHoldReleasePage(string Scope, DateTime AsOf, bool Complete, Guid HoldId,
    IReadOnlyList<StockHoldReleaseRow> Releases, Guid? NextAfter);
