namespace JobScout.Worker.Integrations.Upwork;

/// <summary>Settings for <see cref="UpworkMcpClient"/>, bound from the <c>Upwork</c> configuration section.</summary>
public sealed class UpworkMcpOptions
{
    public const string SectionName = "Upwork";

    public Uri Endpoint { get; set; } = new("https://mcp.upwork.com/mcp");

    /// <summary>The account to act as. When empty, the single freelancer account from <c>list_accounts</c> is used.</summary>
    public string? OrgUid { get; set; }

    /// <summary>
    /// Where tokens and the registered client ID are kept between runs. Holds a refresh token for the Upwork
    /// account; on Windows it is encrypted for the current user with DPAPI. Delete it to sign out.
    /// </summary>
    public string TokenCachePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JobScout", "upwork_tokens.bin");

    public Uri RedirectUri { get; set; } = new("http://localhost:53682/callback");

    /// <summary>Client name used for OAuth dynamic client registration.</summary>
    public string ClientName { get; set; } = "Personal Upwork job scanner";

    /// <summary>
    /// When true, a missing or expired sign-in opens the browser and waits for the callback.
    /// When false (the default, for unattended workers), it throws <see cref="UpworkAuthRequiredException"/> instead.
    /// </summary>
    public bool InteractiveSignIn { get; set; }

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for one tool call (and for listing tools).</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// When true, drift (unknown fields, mistyped optional fields, unknown enum values) fails the call with
    /// <see cref="UpworkContractMismatchException"/> instead of being reported in the response diagnostics.
    /// </summary>
    public bool TreatDriftAsError { get; set; }
}
