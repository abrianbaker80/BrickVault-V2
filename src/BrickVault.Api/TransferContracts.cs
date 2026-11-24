using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrickVault.Api;

internal sealed record TransferCommand
{
    public required Guid CommandId { get; init; }
    public required int ExpectedRevision { get; init; }
    public required Guid PurchaseId { get; init; }
    public required Guid SourcePlacementId { get; init; }
    public required Guid SourceLocationId { get; init; }
    public required string ConfirmedSourceLocationCode { get; init; }
    public required Guid DestinationLocationId { get; init; }
    public required string ConfirmedDestinationLocationCode { get; init; }
    public required int Quantity { get; init; }
    public required string Reason { get; init; }
    public required TransferBacking[] Backing { get; init; }

    private static bool TextFits(string? value, int max) => value is { Length: > 0 } &&
        value.Length <= max && value == value.Trim() && !value.Any(char.IsControl);
    private static bool ValidQuantity(int quantity) => quantity is >= 1 and <= 1_000_000;

    public void Validate()
    {
        if (CommandId == Guid.Empty || ExpectedRevision < 1 || PurchaseId == Guid.Empty ||
            SourcePlacementId == Guid.Empty || SourceLocationId == Guid.Empty || DestinationLocationId == Guid.Empty ||
            SourceLocationId == DestinationLocationId || !TextFits(ConfirmedSourceLocationCode, 48) ||
            !TextFits(ConfirmedDestinationLocationCode, 48) || !ValidQuantity(Quantity) || !TextFits(Reason, 200) ||
            Backing is not { Length: <= 64 } ||
            Backing.Any(b => b is null || b.OfferId == Guid.Empty || !ValidQuantity(b.Quantity)) ||
            Backing.Select(b => b.OfferId).Distinct().Count() != Backing.Length)
            throw new Fault(400, "invalid_transfer", "Supply both bin confirmations, a quantity, a reason and distinct offer allocations.");
    }

    public byte[] Digest() => SHA256.HashData(Encoding.UTF8.GetBytes(
        "transfer\n" + JsonSerializer.Serialize(this, Wire.Json)));
}

internal sealed record TransferBacking([property: JsonRequired] Guid OfferId, [property: JsonRequired] int Quantity);
internal sealed record TransferBackingChoice(Guid OfferId, int Quantity, int UnitPriceMinor, string Currency);
internal sealed record TransferPosition(BinStockLayer Layer, int PurchaseRevision, string LocationCodeAtPlacement,
    IReadOnlyList<TransferBackingChoice> Backing);
internal sealed record TransferMovement(Guid Id, Guid CommandId, Guid CorrelationId, Guid PurchaseId,
    string PurchaseReference, Guid DeliveryId, string DeliveryReference, Guid SourcePlacementId,
    Guid DestinationPlacementId, Guid SourceLocationId, string SourceLocationCode, Guid DestinationLocationId,
    string DestinationLocationCode, Guid CatalogId, int CatalogRevision, int Quantity, string Reason,
    IReadOnlyList<TransferBackingChoice> Backing, DateTime OccurredAt);
internal sealed record BinTransferSnapshot(string Scope, DateTime AsOf, bool Complete, LocationRow Location,
    long PhysicalOnHand, long BackedQuantity, long UnbackedQuantity, long FreeToBackQuantity,
    IReadOnlyList<TransferPosition> Positions, IReadOnlyList<TransferMovement> Movements);
