using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace BrickVault.Api;

internal abstract record ReceivingCommand
{
    public required Guid CommandId { get; init; }
    public required int ExpectedRevision { get; init; }
    public abstract string Kind { get; }
    public void Validate()
    {
        if (CommandId == Guid.Empty || !Valid())
            throw new Fault(400, "invalid_receiving", "Check the required receiving fields, identifiers, quantities and amounts.");
    }

    protected abstract bool Valid();
    protected static bool Text(string? text, int max) => text is { Length: > 0 } &&
        text.Length <= max && text.AsSpan().Trim().Length == text.Length && !text.Any(char.IsControl);
    protected static bool ValidQuantity(int number) => number is >= 1 and <= 1_000_000;
    protected static bool Money(int? number) => number is null or (>= 0 and <= 100_000_000);
    public byte[] Digest() => SHA256.HashData(Encoding.UTF8.GetBytes(
        Kind + "\n" + JsonSerializer.Serialize(this, GetType(), Wire.Json)));
}

internal sealed record PurchaseCommand : ReceivingCommand
{
    [JsonIgnore]
    public override string Kind => "purchase";
    public required string Reference { get; init; }
    public required string Source { get; init; }
    public required string Currency { get; init; }
    public required int OrderedQuantity { get; init; }
    public required int? PendingChargeMinor { get; init; }

    protected override bool Valid() => ExpectedRevision == 0 && Text(Reference, 64) && Text(Source, 160) &&
        Currency is { Length: 3 } && Currency.All(c => c is >= 'A' and <= 'Z') &&
        ValidQuantity(OrderedQuantity) && Money(PendingChargeMinor);
}

internal abstract record PurchaseChange : ReceivingCommand
{
    public required Guid PurchaseId { get; init; }

    protected bool ValidParent => PurchaseId != Guid.Empty && ExpectedRevision > 0;
}

internal sealed record DeliveryCommand : PurchaseChange
{
    [JsonIgnore]
    public override string Kind => "delivery";
    public required string Reference { get; init; }
    public required int Quantity { get; init; }
    public required Guid? CatalogId { get; init; }
    public required int? ItemCostMinor { get; init; }

    protected override bool Valid() => ValidParent && Text(Reference, 64) &&
        ValidQuantity(Quantity) && CatalogId != Guid.Empty && Money(ItemCostMinor);
}

internal sealed record PutawayCommand : PurchaseChange
{
    [JsonIgnore]
    public override string Kind => "putaway";
    public required Guid DeliveryId { get; init; }
    public required Guid CatalogId { get; init; }
    public required Guid LocationId { get; init; }
    public required string ConfirmedLocationCode { get; init; }
    // Preserve the wire digest of an existing putaway request that omitted this field.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ConfirmedCatalogRevision { get; init; }
    public required int Quantity { get; init; }

    protected override bool Valid() => ValidParent && DeliveryId != Guid.Empty &&
        CatalogId != Guid.Empty && LocationId != Guid.Empty && Text(ConfirmedLocationCode, 48) &&
        ConfirmedCatalogRevision is null or > 0 && ValidQuantity(Quantity);
}

internal sealed record OfferCommand : PurchaseChange
{
    [JsonIgnore]
    public override string Kind => "offer";
    public required Guid CatalogId { get; init; }
    public required int UnitPriceMinor { get; init; }
    public required BackingRow[] Backing { get; init; }

    protected override bool Valid() => ValidParent && CatalogId != Guid.Empty && UnitPriceMinor > 0 &&
        Money(UnitPriceMinor) && Backing is { Length: >= 1 and <= 64 } &&
        Backing.All(a => a is not null && a.PlacementId != Guid.Empty && ValidQuantity(a.Quantity)) &&
        Backing.Select(a => a.PlacementId).Distinct().Count() == Backing.Length;
}

internal sealed record BackingRow([property: JsonRequired] Guid PlacementId,
    [property: JsonRequired] int Quantity);
internal sealed record PlacementRow(Guid Id, Guid CatalogId, int CatalogRevision, Guid LocationId,
    string LocationCode, int Quantity, int BackedQuantity, int FreeToBackQuantity);
internal sealed record DeliveryRow(Guid Id, string Reference, int Quantity, Guid? CatalogId,
    int? CatalogRevision, int? ItemCostMinor, int IntakeQuantity, IReadOnlyList<PlacementRow> Placements);
internal sealed record OfferRow(Guid Id, Guid CatalogId, int CatalogRevision, string Currency,
    int UnitPriceMinor, IReadOnlyList<BackingRow> Backing, int BackedQuantity, int PublishedQuantity, bool BackingCurrent);
internal sealed record StockBalances(long PhysicalOnHand, long Unavailable, long Reserved, long Free,
    long BackedQuantity, long WithheldQuantity, int PublishedQuantity, long KnownCostFreeQuantity,
    long UnknownCostFreeQuantity, long? KnownItemCostMinor, long? KnownPurchaseComponentsMinor);
internal sealed record PurchaseRow(Guid Id, int Revision, string Reference, string Source, string Currency,
    int OrderedQuantity, int? PendingChargeMinor, IReadOnlyList<DeliveryRow> Deliveries,
    IReadOnlyList<OfferRow> Offers, StockBalances Balances);
internal sealed record ReceivingWorkspace(string Scope, DateTime AsOf, bool Complete, IReadOnlyList<PurchaseRow> Purchases);

internal sealed record DeliveryRecoveryTotals(long GrossLostQuantity, long RecoveredQuantity,
    string? GrossAllocatedItemCostMinor, string? RestoredItemCostMinor)
{
    internal static DeliveryRecoveryTotals? Read(NpgsqlDataReader row, int first, bool acceptRecoveries)
    {
        var recovered = row.GetInt64(first + 1);
        if (!acceptRecoveries)
        {
            if (recovered > 0) throw StockLossRecoveries.Upgrade();
            return null;
        }
        return new(row.GetInt64(first), recovered, row.IsDBNull(first + 2) ? null : row.GetString(first + 2),
            row.IsDBNull(first + 3) ? null : row.GetString(first + 3));
    }
}

internal sealed record LayerPartOut(Guid? ParentTransformationId, Guid? SourceDeliveryId, Guid? SourcePositionId,
    int ConsumedQuantity, int? ConsumedItemCostMinor)
{
    internal static LayerPartOut? Read(NpgsqlDataReader row, int first, bool partOuts)
    {
        var parent = Database.Optional<Guid>(row, first);
        var consumed = row.GetInt32(first + 3);
        if (!partOuts)
        {
            if (parent.HasValue || consumed > 0) throw Upgrade();
            return null;
        }
        return new(parent, Database.Optional<Guid>(row, first + 1), Database.Optional<Guid>(row, first + 2),
            consumed, Database.Optional<int>(row, first + 4));
    }

    internal static Fault Upgrade() => new(426, "part_out_client_upgrade_required",
        "Update Workbench to see transformed stock and its original receipt layers.");
}

internal sealed record LossDeliveryRow(Guid Id, string Reference, int Quantity, Guid? CatalogId, int? CatalogRevision,
    int? ItemCostMinor, int IntakeQuantity, IReadOnlyList<PlacementRow> Placements, int LostQuantity,
    int? AllocatedLostItemCostMinor, int? RemainingItemCostMinor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? QuarantinedQuantity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DeliveryRecoveryTotals? RecoveryTotals = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayerPartOut? PartOut = null);
internal sealed record LossPurchaseRow(Guid Id, int Revision, string Reference, string Source, string Currency,
    int OrderedQuantity, int? PendingChargeMinor, IReadOnlyList<LossDeliveryRow> Deliveries,
    IReadOnlyList<OfferRow> Offers, StockBalances Balances,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<LossDeliveryRow>? DerivedLayers = null);
internal sealed record LossReceivingWorkspace(string Scope, DateTime AsOf, bool Complete, IReadOnlyList<LossPurchaseRow> Purchases);

internal sealed record BinStockSnapshot(string Scope, DateTime AsOf, bool Complete, LocationRow Location,
    long PhysicalOnHand, long BackedQuantity, long UnbackedQuantity, long FreeToBackQuantity,
    IReadOnlyList<BinStockLayer> Layers);
internal sealed record BinStockLayer(Guid PlacementId, Guid PurchaseId, string PurchaseReference, Guid DeliveryId,
    string DeliveryReference, Guid ReceiptCommandId, Guid ReceiptCorrelationId, int ReceiptQuantity,
    int? ReceiptItemCostMinor, string Currency, Guid CatalogId, int CatalogRevision, CatalogRow CurrentCatalog,
    string LocationCodeAtPutaway, int Quantity, int BackedQuantity, int UnbackedQuantity, int FreeToBackQuantity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? QuarantinedQuantity = null);
