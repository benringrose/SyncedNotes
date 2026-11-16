using Microsoft.EntityFrameworkCore;
using SyncedNotes.Data;

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
/// Guards the note and account APIs:
/// <list type="bullet">
/// <item>A session whose account no longer exists gets 401, like a signed-out one. After an account is deleted, its
/// other devices keep a valid cookie until Identity re-validates it (up to 30 minutes), and without this check their
/// writes would fail with a foreign-key error.</item>
/// <item>A session locked by the app lock gets 423. The lock state lives in the encrypted authentication cookie, so it
/// can't be cleared client-side.</item>
/// </list>
/// </summary>
public sealed class RequireActiveSessionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var userId = http.User.GetUserId();
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        if (!await db.Users.AnyAsync(u => u.Id == userId, http.RequestAborted))
        {
            return ApiProblems.Unauthorized("Not signed in.");
        }

        if (http.User.IsAppLocked())
        {
            return ApiProblems.Problem(
                StatusCodes.Status423Locked, "The app is locked.", "Unlock the app with your passkey or PIN to continue.");
        }

        return await next(context);
    }
}
