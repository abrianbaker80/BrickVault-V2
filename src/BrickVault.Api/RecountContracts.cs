namespace BrickVault.Api;

internal sealed record RecountPreviewRequest
{
    public required Guid PreviewId { get; init; }
    public required string Scope { get; init; }
    public required Guid[] PositionIds { get; init; }
    public required string? Q { get; init; }
    public required string? ItemType { get; init; }
    public required Guid? LocationId { get; init; }

    public RecountPreviewRequest Normalized()
    {
        if (PreviewId == Guid.Empty || PositionIds is null || PositionIds.Length > 20000 || PositionIds.Contains(Guid.Empty) ||
            PositionIds.Distinct().Count() != PositionIds.Length || Scope is not ("matching" or "selected") ||
            (Scope == "selected" && (PositionIds.Length == 0 || Q is not null || ItemType is not null || LocationId.HasValue)) ||
            (Scope == "matching" && PositionIds.Length != 0) || LocationId == Guid.Empty)
            throw new Fault(400, "invalid_recount", "Choose explicit positions or a matching inventory filter.");
        _ = StockRegister.Filters(Q, ItemType, LocationId, null);
        return this with { Q = string.IsNullOrWhiteSpace(Q) ? null : Q.Trim(), PositionIds = PositionIds.Order().ToArray() };
    }
}
internal sealed record RecountCommand
{
    public required Guid CommandId { get; init; }
    public required Guid PreviewId { get; init; }
}
internal sealed record RecountTarget(Guid PositionId, Guid? PurchaseId, string? PurchaseReference, string? DeliveryReference,
    string? ItemName, Guid? LocationId, string? LocationCode, int? PurchaseRevision, int? CatalogRevision, int? LocationRevision, int? Quantity);
internal sealed record RecountPreviewPage(string Scope, Guid PreviewId, string Mode, string? Q, string? ItemType, Guid? LocationId,
    DateTime AsOf, bool Complete, long Total, Guid? NextAfter, List<RecountTarget> Rows);
internal sealed record RecountResult(RecountTarget Target, string Outcome, Guid? QueueBatchId, Guid? ObservationId);
internal sealed record RecountBatchPage(string Scope, Guid BatchId, Guid CommandId, Guid CorrelationId, DateTime AsOf,
    bool Complete, long Total, long Queued, long Changed, long AlreadyQueued, long Unavailable, Guid? NextAfter, List<RecountResult> Rows);
internal sealed record RecountQueueEntry(Guid BatchId, RecountTarget Target, bool Available);
internal sealed record RecountQueuePage(string Scope, DateTime AsOf, bool Complete, long Total, Guid? NextAfter, List<RecountQueueEntry> Rows);
internal sealed record RecountSummary(Guid BatchId, Guid CommandId, DateTime QueuedAt, long Total, long Queued, long Changed, long AlreadyQueued, long Unavailable);
internal sealed record RecountBatchList(string Scope, DateTime AsOf, bool Complete, Guid? NextAfter, List<RecountSummary> Rows);
