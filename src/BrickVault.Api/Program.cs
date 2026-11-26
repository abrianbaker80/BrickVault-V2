using System.Reflection;
using System.Text.Json;
using BrickVault.Api;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

try
{
    await using var db = Foundation.Database(args.FirstOrDefault() ?? "");
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
    var token = shutdown.Token;
    if (args is ["migrate"] or ["migrate", "--to", _])
    {
        var target = args.Length == 1 ? Migration.Current : int.Parse(args[2],
            System.Globalization.CultureInfo.InvariantCulture);
        await Migration.Apply(db, target, token);
        Console.WriteLine($"foundation_schema version={target}");
        return 0;
    }
    await Migration.Check(db, token);
    if (args is ["worker"])
    {
        await Worker.Run(db, token);
        return 0;
    }
    if (args is not ["serve"]) throw new InvalidOperationException("Unknown invocation.");
    var settings = Foundation.Gateway();
    var sessions = new Sessions(db, settings);
    var references = new References(db);
    var receiving = new Receiving(db);
    var counts = new StockCounts(db);
    var closeShortages = new StockShortageClosures(db);
    var holds = new BuildHolds(db);
    var adjustmentReviews = new StockAdjustmentReviews(db);
    var stockLosses = new StockLosses(db);
    var stockPartOuts = new StockPartOuts(db);
    var recoverLoss = new StockLossRecoveries(db);
    var recounts = new Recounts(db);
    var bins = new BinStock(db);
    var inventory = new StockRegister(db);
    var positionTimelines = new StockPositionTimelines(db);
    var quarantines = new StockQuarantines(db);
    var receiptLots = new StockReceiptLots(db);
    var draftBacking = new DraftBackingWithdrawals(db);
    var transfers = new Transfers(db);
    var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
    {
        Args = [], EnvironmentName = "Development", ContentRootPath = AppContext.BaseDirectory
    });
    // Bind only to the explicit loopback endpoint. Do not consume ambient Kestrel/URL configuration.
    builder.Configuration.Sources.Clear();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(server =>
    {
        server.AddServerHeader = false;
        server.Limits.MaxRequestBodySize = 16384;
        server.Listen(settings.Listener);
    });
    var app = builder.Build();
    app.Use(async (ctx, next) =>
    {
        var correlation = Guid.NewGuid();
        ctx.Items[typeof(Guid)] = correlation;
        ctx.Response.Headers["X-Correlation-Id"] = correlation.ToString();
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        try
        {
            if (!sessions.TrustedGateway(ctx)) throw new Fault(403, "gateway_denied", "Private gateway access is required.");
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                var isRead = HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method);
                if (!isRead) sessions.RequireOrigin(ctx);
                if (!(ctx.Request.Path == "/api/v1/session" && HttpMethods.IsPost(ctx.Request.Method)))
                    ctx.Items[typeof(Session)] = await sessions.Authenticate(ctx, !isRead, ctx.RequestAborted);
                await Migration.Check(db, ctx.RequestAborted);
            }
            await next(ctx);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
        catch (Exception error) when (!ctx.Response.HasStarted)
        {
            var fault = error switch
            {
                Fault known => known,
                JsonException => new Fault(400, "invalid_json", "The request must match the version 1 JSON contract."),
                BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => new Fault(413,
                    "request_too_large", "This request exceeds the allowed size. Reduce the payload and submit it again."),
                BadHttpRequestException bad => new Fault(bad.StatusCode, "invalid_request", "The request could not be read."),
                NpgsqlException or TimeoutException or InvalidOperationException => new Fault(503, "foundation_unavailable",
                    "The foundation service is unavailable. Retry the same command after recovery."),
                _ => new Fault(500, "request_failed", "The request could not be completed.")
            };
            // Log a classified outcome and generated identifier only. Exceptions, headers, paths and data can contain secrets.
            if (fault.Status >= 500) Console.Error.WriteLine($"foundation_request {fault.Code} correlation={correlation:D}");
            ctx.Response.StatusCode = fault.Status;
            if (ctx.Request.Path != "/internal/ready")
                await ctx.Response.WriteAsJsonAsync(new ApiError(fault.Code, fault.Message, correlation), Wire.Json, ctx.RequestAborted);
        }
    });
    app.MapGet("/internal/ready", async (HttpContext c, CancellationToken ct) =>
    {
        if (!sessions.TrustedReadiness(c)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        await Migration.Check(db, ct);
        return Results.NoContent();
    });
    var api = app.MapGroup("/api/v1");
    api.MapPost("/session", (HttpContext c, CancellationToken ct) => sessions.Login(c, ct));
    api.MapGet("/session", (HttpContext c) => (Session)c.Items[typeof(Session)]!);
    api.MapDelete("/session", async (HttpContext c, CancellationToken ct) =>
    {
        await sessions.Logout(c, ct);
        return Results.NoContent();
    });
    api.MapGet("/workspace", (HttpContext c, CancellationToken ct) =>
        references.Workspace(ct, c.Request.Query["audit"] == "all"));
    api.MapPost("/catalog", (HttpContext c, CancellationToken ct) => Save<CatalogCommand>(c, references.Save, ct));
    api.MapPost("/locations", (HttpContext c, CancellationToken ct) => Save<LocationCommand>(c, references.Save, ct));
    api.MapGet("/receiving", (CancellationToken ct) => receiving.LegacySnapshot(ct));
    api.MapGet("/receiving/current", async (HttpContext c, CancellationToken ct) =>
        c.Request.Query["losses"] == "true"
            ? (object)await receiving.LossSnapshot(ct, c.Request.Query["quarantine"] == "true", c.Request.Query["recoveries"] == "true", c.Request.Query["partOuts"] == "true")
            : await receiving.CurrentSnapshot(c.Request.Query["holds"] == "true", ct));
    api.MapGet("/receiving/bins/{locationId:guid}/positions/{positionId:guid}/holds",
        (Guid locationId, Guid positionId, Guid? after, int? limit, Guid? commandId, bool? quarantine, CancellationToken ct) =>
            holds.Snapshot(locationId, positionId, after, limit ?? 25, ct, commandId, quarantine == true));
    api.MapGet("/receiving/bins/{locationId:guid}/positions/{positionId:guid}/holds/{holdId:guid}/releases",
        (Guid locationId, Guid positionId, Guid holdId, Guid? after, int? limit, Guid? commandId, CancellationToken ct) =>
            holds.Releases(locationId, positionId, holdId, after, limit ?? 25, ct, commandId));
    api.MapPost("/receiving/holds", (HttpContext c, CancellationToken ct) => Save<StockHoldCommand>(c, holds.Save, ct));
    api.MapPost("/receiving/holds/release", (HttpContext c, CancellationToken ct) => Save<StockHoldReleaseCommand>(c, holds.Save, ct));
    api.MapGet("/receiving/bins/{locationId:guid}/positions/{positionId:guid}/shortages/{caseId:guid}/adjustment-review",
        (Guid locationId, Guid positionId, Guid caseId, string? after, int? limit, bool? quarantine, CancellationToken ct) =>
            adjustmentReviews.Read(locationId, positionId, caseId, after, limit ?? 25, ct, quarantine == true));
    api.MapGet("/receiving/bins/{locationId:guid}/counts",
        (Guid locationId, Guid? positionId, Guid? after, int? limit, Guid? observationId, Guid? commandId, bool? closures,
            CancellationToken ct) => counts.Snapshot(locationId, ct, positionId, after, limit ?? 25, observationId, commandId, closures == true));
    api.MapPost("/receiving/shortage-closures", (HttpContext c, CancellationToken ct) =>
        Save<StockShortageClosureCommand>(c, closeShortages.Save, ct));
    api.MapGet("/receiving/bins/{locationId:guid}/positions/{positionId:guid}/shortages/{caseId:guid}/closure",
        (Guid locationId, Guid positionId, Guid caseId, Guid? commandId, CancellationToken ct) =>
            closeShortages.Read(locationId, positionId, caseId, commandId, ct));
    api.MapPost("/receiving/counts", (HttpContext c, CancellationToken ct) => Save<StockCountCommand>(c, counts.Save, ct));
    api.MapPost("/receiving/stock-losses", (HttpContext c, CancellationToken ct) =>
        Save<StockLossCommand>(c, stockLosses.Save, ct, TransferCommand.MaxRequestBodyBytes));
    api.MapGet("/receiving/bins/{locationId:guid}/positions/{positionId:guid}/posted-loss",
        (Guid locationId, Guid positionId, Guid? caseId, Guid? observationId, Guid? postingId, Guid? commandId,
            string? after, int? limit, bool? quarantine, string? quarantineAfter, int? quarantineLimit, bool? recoveries, bool? partOuts, CancellationToken ct) =>
            stockLosses.Read(locationId, positionId, caseId, observationId, postingId, commandId, after, limit ?? 25, ct,
                quarantine == true, quarantineAfter, quarantineLimit ?? 25, recoveries == true, partOuts == true));
    api.MapPost("/receiving/part-outs", (HttpContext c, CancellationToken ct) =>
        Save<StockPartOutCommand>(c, stockPartOuts.Save, ct, TransferCommand.MaxRequestBodyBytes));
    api.MapGet("/inventory/positions/{positionId:guid}/part-out-review",
        (Guid positionId, int? quantity, Guid? after, int? limit, CancellationToken ct) =>
            stockPartOuts.Review(positionId, quantity ?? 1, after, limit ?? 25, ct));
    api.MapGet("/inventory/positions/{positionId:guid}/part-out-receipt",
        (Guid positionId, Guid? partOutId, Guid? commandId, string? outputAfter, int? outputLimit, string? residualAfter,
            int? residualLimit, CancellationToken ct) =>
            stockPartOuts.Evidence(positionId, partOutId, commandId, outputAfter, outputLimit ?? 25, residualAfter, residualLimit ?? 25, ct));
    api.MapPost("/receiving/stock-loss-recoveries", (HttpContext c, CancellationToken ct) =>
        Save<StockLossRecoveryCommand>(c, recoverLoss.Save, ct, TransferCommand.MaxRequestBodyBytes));
    api.MapGet("/receiving/bins/{locationId:guid}/positions/{positionId:guid}/posted-loss/{postingId:guid}/recovery-review",
        (Guid locationId, Guid positionId, Guid postingId, string? after, int? limit, Guid? historyAfter, int? historyLimit, CancellationToken ct) =>
            recoverLoss.Review(locationId, positionId, postingId, after, limit ?? 25, historyAfter, historyLimit ?? 25, ct));
    api.MapGet("/receiving/bins/{locationId:guid}/positions/{positionId:guid}/posted-loss/{postingId:guid}/recovery-receipt",
        (Guid locationId, Guid positionId, Guid postingId, Guid? recoveryId, Guid? commandId, string? after, int? limit, CancellationToken ct) =>
            recoverLoss.Evidence(locationId, positionId, postingId, recoveryId, commandId, after, limit ?? 25, ct));
    api.MapGet("/inventory/positions", (string? q, string? itemType, Guid? locationId, Guid? positionId, Guid? after, int? limit,
        bool? quarantine, CancellationToken ct) =>
        inventory.Search(q, itemType, locationId, positionId, after, limit ?? 25, ct, quarantine == true));
    api.MapGet("/inventory/positions/{positionId:guid}/movements", (Guid positionId, Guid? after, int? limit, CancellationToken ct) =>
        inventory.History(positionId, after, limit ?? 25, ct));
    api.MapGet("/inventory/positions/{positionId:guid}/timeline",
        (Guid positionId, string? after, int? limit, bool? withdrawals, bool? quarantine, bool? closures, bool? recoveries, bool? partOuts, CancellationToken ct) =>
            positionTimelines.Read(positionId, after, limit ?? 25, ct, withdrawals == true, quarantine == true, closures == true, recoveries == true, partOuts == true));
    api.MapGet("/inventory/receipt-lots/{deliveryId:guid}",
        (Guid deliveryId, string? positionAfter, string? eventAfter, int? positionLimit, int? eventLimit,
            bool? withdrawals, bool? quarantine, bool? closures, bool? recoveries, bool? partOuts, CancellationToken ct) =>
            receiptLots.Read(deliveryId, positionAfter, eventAfter, positionLimit ?? 25, eventLimit ?? 25, ct,
                withdrawals == true, quarantine == true, closures == true, recoveries == true, partOuts == true));
    api.MapGet("/inventory/positions/{positionId:guid}/quarantine",
        (Guid positionId, Guid? after, int? limit, bool? lossAttribution, bool? recoveries, bool? partOuts, CancellationToken ct) =>
            quarantines.Snapshot(positionId, after, limit ?? 25, ct, lossAttribution == true, recoveries == true, partOuts == true));
    api.MapGet("/inventory/positions/{positionId:guid}/quarantine/receipts",
        (Guid positionId, Guid? receiptId, Guid? commandId, bool? recoveries, bool? partOuts, CancellationToken ct) =>
            quarantines.Evidence(positionId, receiptId, commandId, ct, recoveries == true, partOuts == true));
    api.MapPost("/inventory/quarantines", (HttpContext c, CancellationToken ct) => Save<StockQuarantineCommand>(c, quarantines.Save, ct));
    api.MapPost("/inventory/quarantine-releases", (HttpContext c, CancellationToken ct) => Save<StockQuarantineReleaseCommand>(c, quarantines.Save, ct));
    api.MapGet("/receiving/bins/{locationId:guid}", (Guid locationId, bool? quarantine, CancellationToken ct) =>
        bins.Snapshot(locationId, ct, quarantine == true));
    api.MapGet("/inventory/positions/{positionId:guid}/draft-backing", (Guid positionId, string? after, int? limit, bool? quarantine, CancellationToken ct) =>
        draftBacking.Snapshot(positionId, after, limit ?? 25, ct, quarantine == true));
    api.MapGet("/inventory/positions/{positionId:guid}/draft-backing/{offerId:guid}/history",
        (Guid positionId, Guid offerId, string? after, int? limit, bool? quarantine, CancellationToken ct) =>
            draftBacking.History(positionId, offerId, after, limit ?? 25, ct, quarantine == true));
    api.MapGet("/inventory/positions/{positionId:guid}/draft-backing/receipts",
        (Guid positionId, Guid? receiptId, Guid? commandId, CancellationToken ct) =>
            draftBacking.Evidence(positionId, receiptId, commandId, ct));
    api.MapPost("/inventory/draft-backing-withdrawals", (HttpContext c, CancellationToken ct) =>
        Save<DraftBackingWithdrawalCommand>(c, draftBacking.Save, ct));
    api.MapGet("/receiving/bins/{locationId:guid}/movements",
        (Guid locationId, bool? quarantine, CancellationToken ct) => bins.Movements(locationId, ct, quarantine == true));
    api.MapPost("/receiving/transfers", (HttpContext c, CancellationToken ct) =>
        Save<TransferCommand>(c, transfers.Save, ct, TransferCommand.MaxRequestBodyBytes));
    api.MapPost("/receiving/purchases", (HttpContext c, CancellationToken ct) => Save<PurchaseCommand>(c, receiving.Save, ct));
    api.MapPost("/receiving/deliveries", (HttpContext c, CancellationToken ct) => Save<DeliveryCommand>(c, receiving.Save, ct));
    api.MapPost("/receiving/putaway", (HttpContext c, CancellationToken ct) => Save<PutawayCommand>(c, receiving.Save, ct));
    api.MapPost("/receiving/offers", (HttpContext c, CancellationToken ct) => Save<OfferCommand>(c, receiving.Save, ct));
    var recount = api.MapGroup("/inventory/recounts");
    recount.MapPost("/previews", async (HttpContext c, CancellationToken ct) =>
    {
        ((Session)c.Items[typeof(Session)]!).RequireWorkspace(c.Request.Headers);
        AllowBody(c, 1024 * 1024);
        if (!c.Request.HasJsonContentType()) throw new Fault(415, "json_required", "Use the application/json content type.");
        var request = await c.Request.ReadFromJsonAsync<RecountPreviewRequest>(Wire.Json, ct) ?? throw new Fault(400, "invalid_recount", "Choose a recount scope.");
        return await recounts.Freeze(request, ct);
    });
    recount.MapGet("/previews/{id:guid}", (Guid id, Guid? after, int? limit, CancellationToken ct) => recounts.Preview(id, after, limit ?? 25, ct));
    recount.MapPost("/batches", (HttpContext c, CancellationToken ct) => Save<RecountCommand>(c, recounts.Confirm, ct));
    recount.MapGet("/batches/{id:guid}", (Guid id, Guid? after, int? limit, CancellationToken ct) => recounts.Batch(id, after, limit ?? 25, ct));
    recount.MapGet("/batches", (Guid? after, int? limit, CancellationToken ct) => recounts.Batches(after, limit ?? 25, ct));
    recount.MapGet("/queue", (Guid? after, int? limit, CancellationToken ct) => recounts.Queue(after, limit ?? 25, ct));
    api.MapGet("/diagnostics", async (CancellationToken ct) =>
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await Worker.Health(conn, null, ct);
    });
    api.MapGet("/diagnostics/{correlationId:guid}", (Guid correlationId, CancellationToken ct) =>
        references.Diagnostic(correlationId, ct));
    api.MapGet("/export", async (HttpContext c, CancellationToken ct) =>
    {
        if (c.Request.Query.Count != 1 || c.Request.Query["scope"].Count != 1 || c.Request.Query["scope"][0] != "reference")
            throw new Fault(400, "invalid_scope", "Select the reference export scope.");
        c.Response.Headers.ContentDisposition = "attachment; filename=brickvault-reference.json";
        return await references.Export(ct);
    });
    api.MapGet("/contract", () => Results.Stream(
        Assembly.GetExecutingAssembly().GetManifestResourceStream("BrickVault.Api.openapi.v1.json")!, "application/json"));
    app.Map("/api/{**path}", () => { throw new Fault(404, "unknown_endpoint", "This API endpoint is unavailable."); });
    app.UseDefaultFiles();
    app.UseStaticFiles();
    await app.RunAsync();
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception e)
{
    // Keep provider details, file paths and environment values out of process logs.
    var code = e switch
    {
        StartupFault safe => safe.Code,
        PostgresException pg => $"database_{pg.SqlState}",
        NpgsqlException => "database_unavailable",
        _ => "incompatible_configuration_or_schema"
    };
    Console.Error.WriteLine($"foundation_startup {code}");
    return 1;
}

static void AllowBody(HttpContext c, int limit)
{
    if (c.Request.ContentLength > limit)
        throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
    var size = c.Features.Get<IHttpMaxRequestBodySizeFeature>() ??
        throw new InvalidOperationException("The server does not expose a request body limit.");
    size.MaxRequestBodySize = limit;
}

static async Task<Receipt> Save<T>(HttpContext c, Func<T, Guid, CancellationToken, Task<Receipt>> write,
    CancellationToken ct, int? bodyLimit = null) where T : class
{
    ((Session)c.Items[typeof(Session)]!).RequireWorkspace(c.Request.Headers);
    if (bodyLimit is int limit) AllowBody(c, limit);
    if (!c.Request.HasJsonContentType()) throw new Fault(415, "json_required", "Use the application/json content type.");
    var request = await c.Request.ReadFromJsonAsync<T>(Wire.Json, ct) ?? throw Fault.Invalid();
    var receipt = await write(request, (Guid)c.Items[typeof(Guid)]!, ct);
    c.Response.Headers["X-Correlation-Id"] = receipt.CorrelationId.ToString();
    return receipt;
}
