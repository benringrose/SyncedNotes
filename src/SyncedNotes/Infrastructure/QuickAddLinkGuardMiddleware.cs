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
    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (HttpMethods.IsGet(request.Method) &&
            (request.Path.Equals("/new", StringComparison.OrdinalIgnoreCase) || request.Path.Equals("/share", StringComparison.OrdinalIgnoreCase)) &&
            !string.Equals(request.Query["confirm"], "1", StringComparison.Ordinal))
        {
            var site = request.Headers["Sec-Fetch-Site"].ToString();
            var trusted = site.Equals("none", StringComparison.OrdinalIgnoreCase) || site.Equals("same-origin", StringComparison.OrdinalIgnoreCase);
            if (!trusted)
            {
                var query = QueryString.Create(request.Query
                    .Where(kv => !kv.Key.Equals("confirm", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string?>(kv.Key, v))))
                    .Add("confirm", "1");
                context.Response.Redirect($"{request.PathBase}{request.Path}{query}");
                return Task.CompletedTask;
            }
        }

        return next(context);
    }
}
