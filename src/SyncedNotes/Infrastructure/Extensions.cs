using System.Security.Claims;
using SyncedNotes.Auth;

namespace SyncedNotes.Infrastructure;

public static class TimeProviderExtensions
{
    /// <summary>Current UTC time as Unix epoch milliseconds.</summary>
    public static long NowMs(this TimeProvider time) => time.GetUtcNow().ToUnixTimeMilliseconds();
}

public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("The current principal has no user id.");

    /// <summary>True when the browser session has been locked by the app lock.</summary>
    public static bool IsAppLocked(this ClaimsPrincipal principal) =>
        principal.HasClaim(AppClaims.Locked, "1");
}

public static class HttpRequestExtensions
{
    public static bool IsApiRequest(this HttpRequest request) =>
        request.Path.StartsWithSegments("/api") || request.Path.StartsWithSegments("/attachments");

    /// <summary>Absolute base URL of the app (scheme, host and path base), without a trailing slash.</summary>
    public static string BaseUrl(this HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');

    /// <summary>Returns <paramref name="returnUrl"/> when it is a safe local URL, otherwise "/".</summary>
    public static string SafeLocalUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return "/";
        // Only allow app-relative paths: "/foo" but not "//evil.com" or "/\evil.com". Control characters are
        // rejected too, because browsers drop tabs and new lines from URLs ("/\t/evil.com" becomes "//evil.com").
        if (returnUrl[0] == '/' &&
            (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\')) &&
            !returnUrl.Any(char.IsControl))
        {
            return returnUrl;
        }
        return "/";
    }
}

public static class ApiProblems
{
    public static IResult Problem(int status, string title, string? detail = null) =>
        Results.Problem(statusCode: status, title: title, detail: detail);

    public static IResult BadRequest(string title, string? detail = null) => Problem(StatusCodes.Status400BadRequest, title, detail);
    public static IResult NotFound(string title = "Not found.") => Problem(StatusCodes.Status404NotFound, title);
    public static IResult Forbidden(string title, string? detail = null) => Problem(StatusCodes.Status403Forbidden, title, detail);
    public static IResult Unauthorized(string title, string? detail = null) => Problem(StatusCodes.Status401Unauthorized, title, detail);
    public static IResult Conflict(string title, string? detail = null) => Problem(StatusCodes.Status409Conflict, title, detail);
}
