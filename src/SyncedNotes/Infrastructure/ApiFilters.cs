namespace SyncedNotes.Infrastructure;

/// <summary>
/// CSRF protection for cookie-authenticated JSON APIs. Every state-changing request must carry the
/// <c>X-SyncedNotes: 1</c> header. Browsers cannot add custom headers to cross-site requests without a
/// CORS preflight (which this app never grants), so a forged cross-site form or fetch is rejected.
/// </summary>
public sealed class AntiCsrfHeaderFilter : IEndpointFilter
{
    public const string HeaderName = "X-SyncedNotes";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        {
            return next(context);
        }

        if (!string.Equals(request.Headers[HeaderName], "1", StringComparison.Ordinal))
        {
            return ValueTask.FromResult<object?>(ApiProblems.BadRequest(
                "Missing request header.", $"State-changing requests must include the '{HeaderName}: 1' header."));
        }

        // Defence in depth: modern browsers tell us when a request is cross-site.
        if (string.Equals(request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult<object?>(ApiProblems.Forbidden("Cross-site requests are not allowed."));
        }

        return next(context);
    }
}

/// <summary>
/// Rejects requests from a browser session that is currently locked by the app lock (HTTP 423).
/// The lock state lives in the encrypted authentication cookie, so it cannot be cleared client-side.
/// </summary>
public sealed class RequireUnlockedFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.User.IsAppLocked())
        {
            return ValueTask.FromResult<object?>(ApiProblems.Problem(
                StatusCodes.Status423Locked, "The app is locked.", "Unlock the app with your passkey or PIN to continue."));
        }

        return next(context);
    }
}
