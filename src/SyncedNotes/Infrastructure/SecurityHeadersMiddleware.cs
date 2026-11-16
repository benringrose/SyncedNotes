namespace SyncedNotes.Infrastructure;

/// <summary>Adds a strict Content Security Policy and other hardening headers to every response.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    // No inline scripts are used anywhere in the app, so script-src can stay 'self'.
    // Images may come from https: because notes can embed external images in Markdown.
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob: https:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "worker-src 'self'; " +
        "manifest-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;
            if (!headers.ContainsKey("Content-Security-Policy"))
            {
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
            }
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "same-origin";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Permissions-Policy"] =
                "publickey-credentials-get=(self), publickey-credentials-create=(self), camera=(), microphone=(), geolocation=()";
            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}
