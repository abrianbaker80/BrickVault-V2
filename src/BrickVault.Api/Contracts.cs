using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BrickVault.Api;

internal static class Wire
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false,
        MaxDepth = 8
    };
}

internal sealed class Fault(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public static Fault Invalid() => new(400, "invalid_reference", "Check the required fields, allowed values and field lengths.");
    public static Fault Conflict() => new(409, "revision_conflict", "This reference has changed. Reload its current revision.");
    public static Fault StockShortageOpen() => new(409, "stock_shortage_open",
        "An open witnessed shortage blocks new backing, new build holds and transfers from this position.");
    public static Fault SignIn() => new(401, "sign_in_required", "Sign in to open the foundation workspace.");
}

internal abstract record ReferenceCommand
{
    public required Guid CommandId { get; init; }
    public required int ExpectedRevision { get; init; }
    public Guid? Id { get; init; }

    public void Validate()
    {
        if (CommandId == Guid.Empty || Id == Guid.Empty || ExpectedRevision < 0 ||
            (ExpectedRevision > 0 && Id is null)) throw Fault.Invalid();
        ValidateFields();
    }

    protected abstract void ValidateFields();

    protected static bool TextFits(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        value == value.Trim() && !value.Any(char.IsControl);

    public byte[] Digest(string kind) => SHA256.HashData(Encoding.UTF8.GetBytes(
        kind + "\n" + JsonSerializer.Serialize(this, GetType(), Wire.Json)));
}

internal sealed record CatalogCommand : ReferenceCommand
{
    public required string ItemType { get; init; }
    public required string ItemNumber { get; init; }
    public required string? Variant { get; init; }
    public required string Name { get; init; }
    public required int? ColorId { get; init; }
    public required string ColorState { get; init; }
    public required string Condition { get; init; }
    public required string Completeness { get; init; }
    public required string Source { get; init; }
    public required string License { get; init; }
    public required string Mapping { get; init; }

    protected override void ValidateFields()
    {
        if (ItemType is not ("part" or "set" or "minifigure" or "book" or "gear" or "instruction" or "original-box" or "catalog") ||
            !TextFits(ItemNumber, 64) || (Variant is not null && !TextFits(Variant, 64)) ||
            !TextFits(Name, 160) || !TextFits(Source, 80) ||
            ColorState is not ("known" or "unknown" or "not-applicable") ||
            (ColorId.HasValue != (ColorState == "known")) || ColorId < 0 ||
            Condition is not ("new" or "used" or "unknown") ||
            Completeness is not ("complete" or "incomplete" or "unknown" or "not-applicable") ||
            License is not ("operator-owned" or "permitted" or "unknown" or "restricted") ||
            Mapping is not ("confirmed" or "ambiguous" or "unmapped")) throw Fault.Invalid();
    }
}

internal sealed partial record LocationCommand : ReferenceCommand
{
    public required string Code { get; init; }
    public required string Name { get; init; }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,47}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidCode();

    protected override void ValidateFields()
    {
        if (!TextFits(Code, 48) || !ValidCode().IsMatch(Code) || !TextFits(Name, 160)) throw Fault.Invalid();
    }
}

internal sealed record Session(string Actor, string CsrfToken, Guid WorkspaceId)
{
    public void RequireWorkspace(IHeaderDictionary headers)
    {
        var supplied = headers["X-Workspace-Id"];
        if (supplied.Count != 1 || supplied[0] is not { Length: 36 } raw ||
            !Guid.TryParseExact(raw, "D", out var id) || id != WorkspaceId)
            throw new Fault(409, "workspace_mismatch", "This command belongs to a different or unconfirmed workspace.");
    }
}
internal sealed record ApiError(string Code, string Message, Guid CorrelationId);
internal sealed record Receipt(Guid CommandId, Guid EntityId, int Revision, string Kind, Guid CorrelationId);
internal sealed record CatalogRow(Guid Id, int Revision, string ItemType, string ItemNumber, string? Variant,
    string Name, int? ColorId, string ColorState, string Condition, string Completeness, string Source,
    string License, string Mapping, bool IntegrationEligible);
internal sealed record LocationRow(Guid Id, int Revision, string Code, string Name);
internal sealed record AuditRow(Guid CommandId, Guid EntityId, int Revision, string Kind, Guid CorrelationId,
    string Actor, DateTime OccurredAt);
internal sealed record Health(string WorkerState, DateTime? LastHeartbeat, int PendingJobs, int CompletedJobs,
    int BlockedJobs, int SchemaVersion, int ApiVersion);
internal sealed record Diagnostic(Guid CommandId, Guid EntityId, int Revision, string Kind, Guid CorrelationId,
    string JobState, int PayloadVersion, DateTime? ProcessedAt);
internal sealed record ReferenceExport(string Scope, DateTime AsOf, bool Complete,
    List<CatalogRow> Catalog, List<LocationRow> Locations);
internal sealed record Workspace(string Scope, DateTime AsOf, bool Complete,
    List<CatalogRow> Catalog, List<LocationRow> Locations, List<AuditRow> Audit, Health Health);
