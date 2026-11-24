using System.Reflection;
using System.Text.Json;
using BrickVault.Api;
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
    var bins = new BinStock(db);
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
    api.MapGet("/receiving", (CancellationToken ct) => receiving.Snapshot(ct));
    api.MapGet("/receiving/bins/{locationId:guid}", (Guid locationId, CancellationToken ct) => bins.Snapshot(locationId, ct));
    api.MapPost("/receiving/purchases", (HttpContext c, CancellationToken ct) => Save<PurchaseCommand>(c, receiving.Save, ct));
    api.MapPost("/receiving/deliveries", (HttpContext c, CancellationToken ct) => Save<DeliveryCommand>(c, receiving.Save, ct));
    api.MapPost("/receiving/putaway", (HttpContext c, CancellationToken ct) => Save<PutawayCommand>(c, receiving.Save, ct));
    api.MapPost("/receiving/offers", (HttpContext c, CancellationToken ct) => Save<OfferCommand>(c, receiving.Save, ct));
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

static async Task<Receipt> Save<T>(HttpContext c, Func<T, Guid, CancellationToken, Task<Receipt>> write,
    CancellationToken ct) where T : class
{
    ((Session)c.Items[typeof(Session)]!).RequireWorkspace(c.Request.Headers);
    if (!c.Request.HasJsonContentType()) throw new Fault(415, "json_required", "Use the application/json content type.");
    var request = await c.Request.ReadFromJsonAsync<T>(Wire.Json, ct) ?? throw Fault.Invalid();
    var receipt = await write(request, (Guid)c.Items[typeof(Guid)]!, ct);
    c.Response.Headers["X-Correlation-Id"] = receipt.CorrelationId.ToString();
    return receipt;
}
