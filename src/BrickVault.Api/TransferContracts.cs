using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrickVault.Api;

internal sealed record TransferCommand
{
    // One million distinct lines is the physical upper bound for one position.
    // 128 MiB admits that many normally serialized GUID/quantity pairs plus the command envelope.
    internal const int MaxRequestBodyBytes = 128 * 1024 * 1024;

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
    // Keep the digest of a previously prepared explicit transfer unchanged.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MoveAllBacking { get; init; }

    private static bool TextFits(string? value, int max) => value is { Length: > 0 } &&
        value.Length <= max && value == value.Trim() && !value.Any(char.IsControl);
    private static bool ValidQuantity(int quantity) => quantity is >= 1 and <= 1_000_000;

    public void Validate()
    {
        if (CommandId == Guid.Empty || ExpectedRevision < 1 || PurchaseId == Guid.Empty ||
            SourcePlacementId == Guid.Empty || SourceLocationId == Guid.Empty || DestinationLocationId == Guid.Empty ||
            SourceLocationId == DestinationLocationId || !TextFits(ConfirmedSourceLocationCode, 48) ||
            !TextFits(ConfirmedDestinationLocationCode, 48) || !ValidQuantity(Quantity) || !TextFits(Reason, 200) ||
            Backing is null || (MoveAllBacking && Backing.Length != 0))
            throw Invalid();

        if (Backing.Length > Quantity) throw OverAllocated();
        HashSet<Guid> offers = [];
        var remaining = Quantity;
        foreach (var line in Backing)
        {
            if (line is null || line.OfferId == Guid.Empty || !ValidQuantity(line.Quantity) || !offers.Add(line.OfferId))
                throw Invalid();
            if (line.Quantity > remaining) throw OverAllocated();
            remaining -= line.Quantity;
        }
    }

    private static Fault Invalid() => new(400, "invalid_transfer",
        "Supply both bin confirmations, a quantity and a reason. Choose distinct offer allocations or all backing.");

    private static Fault OverAllocated() => new(409, "transfer_backing_unavailable",
        "The selected offer quantities exceed the physical quantity being moved.");

    public byte[] Digest()
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        stream.Write("transfer\n"u8);
        JsonSerializer.Serialize(stream, this, Wire.Json);
        stream.FlushFinalBlock();
        return hash.Hash!;
    }
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
