using System.Net;
using Npgsql;

namespace BrickVault.Api;

internal static class Foundation
{
    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value :
        throw new StartupFault($"missing_setting_{name}");

    public static NpgsqlDataSource Database(string command)
    {
        if (Required("BV_PROFILE") != "foundation" || Required("ASPNETCORE_ENVIRONMENT") != "Development")
            throw new StartupFault("isolated_development_profile_required");
        var role = command switch
        {
            "migrate" => "bv_migrator",
            "serve" => "bv_api",
            "worker" => "bv_worker",
            _ => throw new StartupFault("expected_migrate_serve_or_worker")
        };
        var config = new NpgsqlConnectionStringBuilder(Required("BV_DB"));
        if (config.Host is not ("127.0.0.1" or "::1" or "localhost") ||
            config.Database?.StartsWith("bv_m2_", StringComparison.Ordinal) != true ||
            config.Username != role)
            throw new StartupFault("local_foundation_database_and_scoped_role_required");
        config.IncludeErrorDetail = false;
        config.LogParameters = false;
        config.SearchPath = "pg_catalog";
        return NpgsqlDataSource.Create(config.ConnectionString);
    }

    public static GatewaySettings Gateway()
    {
        var origin = Required("BV_PUBLIC_ORIGIN");
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.GetLeftPart(UriPartial.Authority) != origin || uri.UserInfo.Length > 0)
            throw new StartupFault("canonical_https_origin_required");
        var gatewayToken = Required("BV_GATEWAY_TOKEN");
        if (gatewayToken.Length < 32) throw new StartupFault("invalid_gateway_key");
        var sessionKey = new byte[32];
        if (!Convert.TryFromBase64String(Required("BV_SESSION_KEY"), sessionKey, out int length) || length != 32)
            throw new StartupFault("invalid_session_key");
        if (!int.TryParse(Required("BV_HTTP_PORT"), out var port) || port is < 1 or > 65535)
            throw new StartupFault("invalid_loopback_listener_port");
        var workspace = Required("BV_WORKSPACE_ID");
        if (!Guid.TryParseExact(workspace, "D", out var workspaceId) || workspace.Length != 36 || workspaceId == Guid.Empty)
            throw new StartupFault("invalid_workspace_id");
        return new GatewaySettings(origin, gatewayToken, sessionKey, new IPEndPoint(IPAddress.Loopback, port), workspaceId);
    }
}

internal sealed record GatewaySettings(string Origin, string Token, byte[] SessionKey, IPEndPoint Listener, Guid WorkspaceId);

// Codes come only from program literals and fixed setting names. Never attach exception text or setting values.
internal sealed class StartupFault(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}
