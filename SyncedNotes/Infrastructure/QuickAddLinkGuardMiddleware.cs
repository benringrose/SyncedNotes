using Microsoft.AspNetCore.WebUtilities;

namespace SyncedNotes.Infrastructure;

/// <summary>
/// Quick-add links (<c>/new?text=…</c>, the Web Share Target <c>/share</c>) create a note as soon as the app opens,
/// which is what makes PowerToys Command Palette bookmarks and the share sheet frictionless. To stop other websites
/// from silently adding notes to a signed-in user's account, navigations that were not started by the user
/// (Fetch Metadata <c>Sec-Fetch-Site</c> other than <c>none</c>/<c>same-origin</c>) are redirected with
/// <c>confirm=1</c>, and the app then asks before saving. This runs before authentication so the flag is kept in
/// the sign-in return URL too.
/// </summary>
public sealed class QuickAddLinkGuardMiddleware(RequestDelegate next)
{
    private const string ConfirmKey = "confirm";

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (HttpMethods.IsGet(request.Method) && IsQuickAddPath(request.Path) && !IsConfirmed(request.Query) && !IsUserInitiated(request))
        {
            context.Response.Redirect($"{request.PathBase}{request.Path}{WithConfirm(request.Query)}");
            return Task.CompletedTask;
        }

        return next(context);
    }

    public static bool IsQuickAddPath(PathString path) =>
        path.Equals("/new", StringComparison.OrdinalIgnoreCase) || path.Equals("/share", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the browser says the user started this navigation (typed URL, bookmark, other app) or it came from this site.</summary>
    public static bool IsUserInitiated(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        return site.Equals("none", StringComparison.OrdinalIgnoreCase) || site.Equals("same-origin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The sign-in page sends the user on to its return URL from script, which the browser reports as a same-origin
    /// navigation. So when the sign-in page itself was opened from another site, mark a quick-add return URL as
    /// needing confirmation.
    /// </summary>
    public static string ProtectReturnUrl(HttpRequest request, string returnUrl)
    {
        if (IsUserInitiated(request)) return returnUrl;

        var hash = returnUrl.IndexOf('#');
        var fragment = hash < 0 ? "" : returnUrl[hash..];
        var withoutFragment = hash < 0 ? returnUrl : returnUrl[..hash];
        var queryStart = withoutFragment.IndexOf('?');
        var path = queryStart < 0 ? withoutFragment : withoutFragment[..queryStart];
        if (!IsQuickAddPath(new PathString(path))) return returnUrl;

        var query = new QueryCollection(QueryHelpers.ParseQuery(queryStart < 0 ? "" : withoutFragment[queryStart..]));
        return IsConfirmed(query) ? returnUrl : $"{path}{WithConfirm(query)}{fragment}";
    }

    // The app reads the flag with URLSearchParams, which is case-sensitive, so only an exact "confirm=1" counts
    // (ASP.NET Core's query collection would also match "CONFIRM=1").
    private static bool IsConfirmed(IQueryCollection query) =>
        query.Any(kv => string.Equals(kv.Key, ConfirmKey, StringComparison.Ordinal) && kv.Value is ["1"]);

    private static QueryString WithConfirm(IQueryCollection query) =>
        QueryString.Create(query
            .Where(kv => !kv.Key.Equals(ConfirmKey, StringComparison.OrdinalIgnoreCase))
            .SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string?>(kv.Key, v))))
            .Add(ConfirmKey, "1");
}
