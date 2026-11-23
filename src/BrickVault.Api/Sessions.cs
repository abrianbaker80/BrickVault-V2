using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed class Sessions(NpgsqlDataSource db, GatewaySettings settings)
{
    private const string Cookie = "__Host-bv_session";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    private readonly byte[] gatewayDigest = SHA256.HashData(Encoding.UTF8.GetBytes(settings.Token));

    public bool TrustedGateway(HttpContext context)
    {
        var supplied = context.Request.Headers["X-BrickVault-Gateway"];
        return supplied.Count == 1 && CryptographicOperations.FixedTimeEquals(gatewayDigest,
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied[0]!)));
    }

    public void RequireOrigin(HttpContext context)
    {
        var origins = context.Request.Headers.Origin;
        if (origins.Count != 1 || origins[0] != settings.Origin)
            throw new Fault(403, "origin_denied", "The request must originate from the private workspace.");
    }

    private static CookieOptions CookiePolicy() => new()
    {
        Path = "/", Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true
    };

    private static string? CookieValue(HttpContext ctx)
    {
        var token = ctx.Request.Cookies[Cookie];
        return token is { Length: 43 } && token.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ? token : null;
    }

    private byte[] Derive(string domain, string token) =>
        HMACSHA256.HashData(settings.SessionKey, Encoding.ASCII.GetBytes(domain + "/" + token));

    private Session View(string token) =>
        new("operator", WebEncoders.Base64UrlEncode(Derive("csrf", token)));

    public async Task<Session> Login(HttpContext ctx, CancellationToken token)
    {
        var actors = ctx.Request.Headers["X-BrickVault-Actor"];
        if (actors.Count != 1 || actors[0] != "operator") throw Fault.SignIn();
        var raw = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        if (CookieValue(ctx) is { } prior)
        {
            await using var revoke = Query(connection, tx, "DELETE FROM brickvault.sessions WHERE lookup=$1",
                Derive("session", prior));
            await revoke.ExecuteNonQueryAsync(token);
        }
        await using var insert = Query(connection, tx,
            "INSERT INTO brickvault.sessions(lookup,expires_at) VALUES ($1,clock_timestamp()+$2)", Derive("session", raw), Lifetime);
        await insert.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
        var policy = CookiePolicy();
        policy.MaxAge = Lifetime;
        ctx.Response.Cookies.Append(Cookie, raw, policy);
        return View(raw);
    }

    public async Task<Session> Authenticate(HttpContext ctx, bool requireCsrf, CancellationToken token)
    {
        var raw = CookieValue(ctx) ?? throw Fault.SignIn();
        await using var connection = await db.OpenConnectionAsync(token);
        await using var find = Query(connection, null,
            "SELECT 1 FROM brickvault.sessions WHERE lookup=$1 AND expires_at > clock_timestamp()", Derive("session", raw));
        if (await find.ExecuteScalarAsync(token) is null) throw Fault.SignIn();
        var session = View(raw);
        if (requireCsrf)
        {
            var csrf = ctx.Request.Headers["X-CSRF-Token"];
            if (csrf.Count != 1 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(session.CsrfToken),
                    Encoding.UTF8.GetBytes(csrf[0]!)))
                throw new Fault(403, "csrf_denied", "Refresh the workspace session before submitting changes.");
        }
        return session;
    }

    public async Task Logout(HttpContext ctx, CancellationToken token)
    {
        await using var connection = await db.OpenConnectionAsync(token);
        await using var revoke = Query(connection, null, "DELETE FROM brickvault.sessions WHERE lookup=$1",
            Derive("session", CookieValue(ctx)!));
        await revoke.ExecuteNonQueryAsync(token);
        ctx.Response.Cookies.Delete(Cookie, CookiePolicy());
    }
}
