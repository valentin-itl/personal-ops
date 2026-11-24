using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using ModelContextProtocol.Authentication;

namespace JobScout.Worker.Integrations.Upwork;

internal static class UpworkOAuth
{
    /// <summary>Opens the browser for Upwork sign-in and waits for the redirect on the local callback URI.</summary>
    public static async Task<AuthorizationResult?> SignInWithBrowserAsync(
        AuthorizationCallbackContext context, CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(context.RedirectUri.GetLeftPart(UriPartial.Authority) + "/");
        listener.Start();

        Console.WriteLine("Opening your browser for Upwork sign-in...");

        Process.Start(new ProcessStartInfo(context.AuthorizationUri.AbsoluteUri) { UseShellExecute = true });

        var http = await listener.GetContextAsync().WaitAsync(cancellationToken);
        var query = HttpUtility.ParseQueryString(http.Request.Url!.Query);

        var page = "<html><body><h3>Upwork sign-in finished. You can close this tab.</h3></body></html>"u8.ToArray();
        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.OutputStream.WriteAsync(page, cancellationToken);
        http.Response.Close();

        if (query["error"] is { } error)
        {
            throw new UpworkAuthRequiredException($"Upwork sign-in failed: {error} {query["error_description"]}")
            {
                Tool = "connect",
                ErrorCode = error,
            };
        }

        // The SDK checks state (and iss, when present) before exchanging the code for tokens.
        return new AuthorizationResult { Code = query["code"]!, State = query["state"]!, Iss = query["iss"] };
    }

    /// <summary>Used when interactive sign-in is off: an unattended worker must not wait for a browser.</summary>
    public static Task<AuthorizationResult?> RejectInteractiveSignIn(
        AuthorizationCallbackContext context, CancellationToken cancellationToken) =>
        throw new UpworkAuthRequiredException(
            "Upwork sign-in is required (no usable token: missing, expired or revoked). " +
            $"Run once with {UpworkMcpOptions.SectionName}:InteractiveSignIn=true on this machine and user to sign in again.")
        {
            Tool = "connect",
            ErrorCode = "SIGN_IN_REQUIRED",
        };
}

/// <summary>
/// Keeps tokens and the registered client ID between runs, so you sign in once.
/// The file holds a refresh token for your Upwork account: on Windows it is encrypted for your
/// user with DPAPI. Delete the file to sign out of this app.
/// </summary>
internal sealed class ProtectedFileTokenCache(string path) : ITokenCache
{
    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;
        var bytes = Unprotect(await File.ReadAllBytesAsync(path, cancellationToken));
        return JsonSerializer.Deserialize<TokenContainer>(bytes);
    }

    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Protect(JsonSerializer.SerializeToUtf8Bytes(tokens));
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
    }

    private static byte[] Protect(byte[] data) =>
        OperatingSystem.IsWindows() ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser) : data;

    private static byte[] Unprotect(byte[] data) =>
        OperatingSystem.IsWindows() ? ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser) : data;
}
