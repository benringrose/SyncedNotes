using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SyncedNotes.CmdPal.Helpers;

namespace SyncedNotes.CmdPal.Services;

/// <summary>
/// Client for the SyncedNotes "quick" API:
/// <c>POST /api/v1/quick/notes</c>, <c>GET /api/v1/quick/notes?q=&amp;limit=</c>, <c>GET /api/v1/quick/binders</c>.
/// Every request sends <c>Authorization: Bearer &lt;token&gt;</c>. Failures surface as
/// <see cref="SyncedNotesException"/> with a friendly message.
/// </summary>
internal sealed class SyncedNotesClient
{
    private const string NotesPath = "/api/v1/quick/notes";
    private const string BindersPath = "/api/v1/quick/binders";
    private const int MaxQueryLength = 200;

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromSeconds(20);

    // A single HttpClient for the lifetime of the extension process (recommended usage;
    // creating one per request exhausts sockets). The server URL comes from the settings
    // and can change at any time, so it is not used as BaseAddress.
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly SettingsManager _settings;

    public SyncedNotesClient(SettingsManager settings)
    {
        _settings = settings;
    }

    public async Task<CreatedNote> CreateNoteAsync(CreateNoteRequest request, CancellationToken cancellationToken)
    {
        var connection = GetConnection();

        var body = JsonSerializer.SerializeToUtf8Bytes(request, SyncedNotesJsonContext.Default.CreateNoteRequest);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(connection, NotesPath, query: null)) { Content = content };

        var created = await SendAsync(connection, message, NotesPath, "save the note", SyncedNotesJsonContext.Default.CreatedNote, CreateTimeout, cancellationToken).ConfigureAwait(false);
        if (created is null || string.IsNullOrWhiteSpace(created.Id))
        {
            throw InvalidResponse(connection, "save the note", "the response didn't contain the new note's id");
        }

        return created;
    }

    public async Task<IReadOnlyList<NoteSummary>> SearchNotesAsync(string? query, int limit, CancellationToken cancellationToken)
    {
        var connection = GetConnection();

        var text = TextHelpers.SingleLine(query);
        if (text.Length > MaxQueryLength)
        {
            text = text[..MaxQueryLength];
        }

        var limitText = Math.Clamp(limit, 1, 50).ToString(CultureInfo.InvariantCulture);
        var queryString = text.Length > 0
            ? $"q={Uri.EscapeDataString(text)}&limit={limitText}"
            : $"limit={limitText}";

        using var message = new HttpRequestMessage(HttpMethod.Get, BuildUri(connection, NotesPath, queryString));
        var notes = await SendAsync(connection, message, NotesPath, "search notes", SyncedNotesJsonContext.Default.ListNoteSummary, QueryTimeout, cancellationToken).ConfigureAwait(false);
        return notes ?? [];
    }

    public async Task<IReadOnlyList<BinderInfo>> GetBindersAsync(CancellationToken cancellationToken)
    {
        var connection = GetConnection();

        using var message = new HttpRequestMessage(HttpMethod.Get, BuildUri(connection, BindersPath, query: null));
        var binders = await SendAsync(connection, message, BindersPath, "load binders", SyncedNotesJsonContext.Default.ListBinderInfo, QueryTimeout, cancellationToken).ConfigureAwait(false);
        return binders ?? [];
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            // Redirects are reported instead of followed: HttpClient drops the Authorization
            // header on redirects (and turns a redirected POST into a GET), which would only
            // produce confusing 401s. CreateRedirectError() tells the user which URL to use.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),

            // Recycle pooled connections so DNS changes (e.g. a moved self-hosted server) are picked up.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            // Each request gets its own timeout via a CancellationToken (see SendAsync).
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/problem+json"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SyncedNotes-CmdPal", "0.1"));
        return client;
    }

    private static Uri BuildUri(ServerConnection connection, string path, string? query) =>
        new(string.IsNullOrEmpty(query) ? connection.BaseUrl + path : $"{connection.BaseUrl}{path}?{query}");

    private static string Authority(ServerConnection connection) =>
        Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var uri) ? uri.Authority : connection.BaseUrl;

    private static bool IsJson(MediaTypeHeaderValue? contentType) =>
        contentType?.MediaType is { } mediaType && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);

    // Only the operation and the outcome are logged (they end up in Command Palette's log):
    // never the token, the server URL, search text or note content.
    private static void Log(string operation, string outcome, MessageState state) =>
        ExtensionHost.LogMessage(new LogMessage { Message = $"SyncedNotes: {operation} {outcome}", State = state });

    private static SyncedNotesException InvalidResponse(ServerConnection connection, string operation, string reason, Exception? innerException = null)
    {
        Log(operation, "returned an unexpected response", MessageState.Error);
        return new SyncedNotesException(
            SyncedNotesErrorKind.InvalidResponse,
            $"{Authority(connection)} answered, but not like a SyncedNotes server ({reason}). Check the server URL in the extension settings and that the server is up to date.",
            null,
            innerException);
    }

    private static SyncedNotesException CreateNetworkError(ServerConnection connection, string operation, Exception exception)
    {
        var requestError = (exception as HttpRequestException)?.HttpRequestError ?? HttpRequestError.Unknown;
        Log(operation, $"failed: {requestError}", MessageState.Error);

        var host = Authority(connection);
        (SyncedNotesErrorKind Kind, string Message) error = requestError switch
        {
            HttpRequestError.NameResolutionError =>
                (SyncedNotesErrorKind.Network, $"Couldn't find {host}. Check the server URL in the extension settings and your network connection."),
            HttpRequestError.ConnectionError =>
                (SyncedNotesErrorKind.Network, $"Couldn't connect to {host}. Is the server running and reachable from this PC?"),
            HttpRequestError.SecureConnectionError =>
                (SyncedNotesErrorKind.Tls, $"Couldn't establish a secure (TLS) connection to {host}. If the server uses a self-signed certificate, Windows has to trust it."),
            HttpRequestError.ProxyTunnelError =>
                (SyncedNotesErrorKind.Network, $"Your proxy server refused the connection to {host}."),
            _ when exception.InnerException is AuthenticationException =>
                (SyncedNotesErrorKind.Tls, $"Couldn't establish a secure (TLS) connection to {host}. If the server uses a self-signed certificate, Windows has to trust it."),
            _ =>
                (SyncedNotesErrorKind.Network, $"Couldn't reach {host}: {exception.Message}"),
        };

        return new SyncedNotesException(error.Kind, error.Message, null, exception);
    }

    private static SyncedNotesException CreateRedirectError(ServerConnection connection, HttpResponseMessage response, string path)
    {
        var status = (int)response.StatusCode;
        var location = response.Headers.Location;
        if (location is not null && !location.IsAbsoluteUri && response.RequestMessage?.RequestUri is { } requestUri)
        {
            location = new Uri(requestUri, location);
        }

        // e.g. http://host/api/v1/quick/notes -> https://host/api/v1/quick/notes  =>  suggest https://host
        string? suggestion = null;
        if (location is { IsAbsoluteUri: true })
        {
            var target = location.GetLeftPart(UriPartial.Path).TrimEnd('/');
            if (target.EndsWith(path, StringComparison.OrdinalIgnoreCase))
            {
                suggestion = target[..^path.Length];
            }
        }

        var message = suggestion is not null && !string.Equals(suggestion, connection.BaseUrl, StringComparison.OrdinalIgnoreCase)
            ? $"The server redirects to {suggestion}. Change the server URL in the extension settings to {suggestion}."
            : location is { IsAbsoluteUri: true }
                ? $"The server redirected the request to {location.GetLeftPart(UriPartial.Path)} ({status}). Check the server URL in the extension settings. If SyncedNotes is behind a login proxy, let /api/v1/quick/ requests with a bearer token through."
                : $"The server answered with a redirect ({status}). Check the server URL in the extension settings.";

        return new SyncedNotesException(SyncedNotesErrorKind.Redirect, message, status);
    }

    private static async Task<string?> TryReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!IsJson(response.Content.Headers.ContentType))
        {
            return null; // e.g. an HTML error page from a reverse proxy
        }

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var problem = await JsonSerializer.DeserializeAsync(stream, SyncedNotesJsonContext.Default.ProblemDetailsResponse, cancellationToken).ConfigureAwait(false);
                return problem is null ? null : DescribeProblem(problem);
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or OperationCanceledException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? DescribeProblem(ProblemDetailsResponse problem)
    {
        var parts = new List<string>();
        var headline = string.IsNullOrWhiteSpace(problem.Detail) ? problem.Title : problem.Detail;
        if (!string.IsNullOrWhiteSpace(headline))
        {
            parts.Add(headline.Trim());
        }

        // ASP.NET Core validation problems: { "errors": { "Title": [ "The field Title must ..." ] } }
        if (problem.Errors.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in problem.Errors.EnumerateObject())
            {
                IEnumerable<string?> messages = field.Value.ValueKind switch
                {
                    JsonValueKind.Array => field.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()),
                    JsonValueKind.String => new[] { field.Value.GetString() },
                    _ => Array.Empty<string?>(),
                };

                parts.AddRange(messages.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!.Trim()));
                if (parts.Count >= 4)
                {
                    break;
                }
            }
        }

        return parts.Count == 0 ? null : TextHelpers.Ellipsize(string.Join(' ', parts.Distinct()), 300);
    }

    private ServerConnection GetConnection() =>
        _settings.TryGetConnection(out var connection, out var problem)
            ? connection
            : throw new SyncedNotesException(SyncedNotesErrorKind.NotConfigured, problem);

    private async Task<T?> SendAsync<T>(
        ServerConnection connection,
        HttpRequestMessage message,
        string path,
        string operation,
        JsonTypeInfo<T> responseType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw await CreateHttpErrorAsync(connection, response, path, operation, timeoutCts.Token).ConfigureAwait(false);
            }

            if (!IsJson(response.Content.Headers.ContentType))
            {
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? "no content type";
                throw InvalidResponse(connection, operation, $"it returned {mediaType} instead of JSON");
            }

            var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync(stream, responseType, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // cancelled by the caller (e.g. the user kept typing): not an error
        }
        catch (OperationCanceledException ex)
        {
            Log(operation, "timed out", MessageState.Warning);
            throw new SyncedNotesException(
                SyncedNotesErrorKind.Timeout,
                $"{Authority(connection)} didn't answer within {timeout.TotalSeconds:0} seconds. Check that the server is running and reachable.",
                null,
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw CreateNetworkError(connection, operation, ex);
        }
        catch (IOException ex)
        {
            throw CreateNetworkError(connection, operation, ex);
        }
        catch (JsonException ex)
        {
            throw InvalidResponse(connection, operation, "its JSON didn't have the expected shape", ex);
        }
    }

    private async Task<SyncedNotesException> CreateHttpErrorAsync(ServerConnection connection, HttpResponseMessage response, string path, string operation, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        Log(operation, $"failed with HTTP {status}", MessageState.Error);

        if (status is >= 300 and < 400)
        {
            return CreateRedirectError(connection, response, path);
        }

        var serverMessage = await TryReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
        var serverSuffix = string.IsNullOrEmpty(serverMessage) ? string.Empty : $" The server said: {serverMessage}";

        switch (status)
        {
            case 401:
                var hint = _settings.TokenLooksValid
                    ? string.Empty
                    : " (The saved token doesn't look like a SyncedNotes API token; those start with “snp_”.)";
                return new SyncedNotesException(
                    SyncedNotesErrorKind.Unauthorized,
                    $"SyncedNotes didn't accept the API token (401). It may have been revoked or mistyped. Create a new token in SyncedNotes under Settings → Integrations and paste it into the extension settings.{hint}",
                    status);

            case 403:
                return new SyncedNotesException(SyncedNotesErrorKind.Forbidden, $"SyncedNotes refused to {operation} (403 Forbidden).{serverSuffix}", status);

            case 404:
                return new SyncedNotesException(
                    SyncedNotesErrorKind.NotFound,
                    $"The SyncedNotes API wasn't found at {connection.BaseUrl} (404). Check the server URL in the extension settings and that the server is up to date.",
                    status);

            case 400 or 422:
                return new SyncedNotesException(
                    SyncedNotesErrorKind.BadRequest,
                    string.IsNullOrEmpty(serverMessage) ? $"SyncedNotes couldn't {operation} ({status})." : $"SyncedNotes couldn't {operation}: {serverMessage}",
                    status);

            case 413:
                return new SyncedNotesException(SyncedNotesErrorKind.BadRequest, "The note is too large for the server (413).", status);

            case 429:
                return new SyncedNotesException(SyncedNotesErrorKind.RateLimited, "Too many requests. Wait a moment and try again (429).", status);

            case >= 500:
                return new SyncedNotesException(SyncedNotesErrorKind.ServerError, $"The SyncedNotes server had a problem ({status}).{serverSuffix}", status);

            default:
                return new SyncedNotesException(SyncedNotesErrorKind.HttpError, $"SyncedNotes couldn't {operation} (HTTP {status}).{serverSuffix}", status);
        }
    }
}
