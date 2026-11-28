namespace BrickVault.Api;

internal static class SetContentsContract
{
    internal const string Header = "X-BrickVault-Set-Contents";

    internal static bool Negotiate(HttpContext context)
    {
        var values = context.Request.Headers[Header];
        if (values.Count != 1 || values[0] != "1") return false;
        context.Response.Headers[Header] = "1";
        return true;
    }

    internal static Fault Upgrade() => new(426, "set_contents_client_upgrade_required",
        "Update Workbench to see the recorded box and instruction manual details.");
}
