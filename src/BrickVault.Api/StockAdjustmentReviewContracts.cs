namespace BrickVault.Api;

internal sealed record AdjustmentReceiptEvidence(Guid DeliveryId, string DeliveryReference, int DeliveryQuantity,
    int? ItemCostMinor, string Currency, Guid OriginPlacementId, Guid CatalogId, int CatalogRevision);

internal sealed record AdjustmentOfferBacking(Guid Id, int BackedQuantity, int UnitPriceMinor, string Currency);

internal sealed record StockAdjustmentReview(string Scope, DateTime AsOf, bool Complete, LocationRow Location,
    Guid PositionId, Guid CaseId, Guid PurchaseId, int PurchaseRevision, AdjustmentReceiptEvidence Receipt,
    StockCountObservation LatestObservation, int LedgerQuantity, int CandidateLossQuantity, int PostLossLedgerQuantity,
    int ProtectedProjectQuantity, int ProtectedProjectShortfall, int OfferBackedQuantity, int OfferCount,
    int OfferCapacityAfterLoss, int MinimumOfferBackingReduction, IReadOnlyList<AdjustmentOfferBacking> Offers,
    string? NextAfter);
