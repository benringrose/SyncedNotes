using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SyncedNotes.Sync;

namespace SyncedNotes.Tests.Infrastructure;

/// <summary>HTTP and JSON helpers shared by the tests.</summary>
public static class Api
{
    /// <summary>The app uses the ASP.NET Core web defaults: camelCase, case-insensitive.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static async Task<HttpResponseMessage> AssertStatusAsync(this Task<HttpResponseMessage> request, HttpStatusCode expected)
    {
        var response = await request;
        await response.AssertStatusAsync(expected);
        return response;
    }

    public static async Task AssertStatusAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (body.Length > 2000) body = body[..2000] + "…";
            Assert.Fail($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}: " +
                        $"expected {(int)expected} {expected} but got {(int)response.StatusCode} {response.StatusCode}.\n{body}");
        }
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        await response.AssertStatusAsync(expected);
        var value = await response.Content.ReadFromJsonAsync<T>(Json);
        Assert.NotNull(value);
        return value;
    }

    public static Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK) =>
        response.ReadAsync<JsonElement>(expected);

    public static async Task<T> GetAsync<T>(this HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.OK) =>
        await (await client.GetAsync(url)).ReadAsync<T>(expected);

    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.OK) =>
        await (await client.GetAsync(url)).ReadJsonAsync(expected);

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object? body) =>
        client.PostAsync(url, JsonBody(body));

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient client, string url, object? body) =>
        client.PutAsync(url, JsonBody(body));

    public static StringContent JsonBody(object? body) =>
        new(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

    /// <summary>Asserts an RFC 7807 problem response and returns its title.</summary>
    public static async Task<(string Title, string? Detail)> ReadProblemAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        await response.AssertStatusAsync(expected);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal((int)expected, problem.GetProperty("status").GetInt32());
        var detail = problem.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        return (problem.GetProperty("title").GetString()!, detail);
    }

    public static string Str(this JsonElement element, string property) =>
        element.GetProperty(property).GetString() ?? throw new InvalidOperationException($"'{property}' is null.");

    // ------------------------------------------------------------------------------------------- Sync

    public static NoteChange NewNote(
        string title = "Note", string content = "", Guid? binderId = null, Guid? id = null, List<string>? tags = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        BaseRev = 0,
        Fields = null,
        BinderId = binderId,
        Title = title,
        Content = content,
        Tags = tags ?? [],
        CreatedAt = NowMs(),
        UpdatedAt = NowMs(),
    };

    public static BinderChange NewBinder(string name, Guid? id = null, string? color = null, string? icon = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        BaseRev = 0,
        Fields = null,
        Name = name,
        Color = color,
        Icon = icon,
        CreatedAt = NowMs(),
        UpdatedAt = NowMs(),
    };

    /// <summary>
    /// What a client sends after editing a note it last saw as <paramref name="seen"/>: its full state, the base
    /// revision it saw and the names of the fields it changed (like the browser client's toNoteChange()).
    /// </summary>
    public static NoteChange EditOf(NoteDto seen, params string[] fields) => new()
    {
        Id = seen.Id,
        BaseRev = seen.Rev,
        Fields = fields,
        BinderId = seen.BinderId,
        Title = seen.Title,
        Content = seen.Content,
        Color = seen.Color,
        Icon = seen.Icon,
        Tags = [.. seen.Tags],
        Pinned = seen.Pinned,
        Archived = seen.Archived,
        TrashedAt = seen.TrashedAt,
        SortOrder = seen.SortOrder,
        CreatedAt = seen.CreatedAt,
        UpdatedAt = NowMs(),
    };

    /// <summary>A permanent delete of a note the client last saw as <paramref name="seen"/>.</summary>
    public static NoteChange DeleteOf(NoteDto seen)
    {
        var change = EditOf(seen);
        change.Fields = null;
        change.Deleted = true;
        return change;
    }

    public static async Task<HttpResponseMessage> PostSyncAsync(this HttpClient client, SyncPushRequest request) =>
        await client.PostJsonAsync("/api/sync", request);

    public static async Task<SyncResponse> PushAsync(this HttpClient client, SyncPushRequest request) =>
        await (await client.PostSyncAsync(request)).ReadAsync<SyncResponse>();

    public static Task<SyncResponse> PushAsync(
        this HttpClient client, long cursor = 0, IEnumerable<NoteChange>? notes = null, IEnumerable<BinderChange>? binders = null) =>
        client.PushAsync(new SyncPushRequest { Cursor = cursor, Notes = notes?.ToList(), Binders = binders?.ToList() });

    public static Task<SyncResponse> PushNotesAsync(this HttpClient client, params NoteChange[] notes) =>
        client.PushAsync(notes: notes);

    public static Task<SyncResponse> PullAsync(this HttpClient client, long since = 0, int? limit = null) =>
        client.GetAsync<SyncResponse>($"/api/sync?since={since}{(limit is null ? "" : $"&limit={limit}")}");

    /// <summary>Pulls every page from <paramref name="since"/> and returns the latest state of each note.</summary>
    public static async Task<(Dictionary<Guid, NoteDto> Notes, Dictionary<Guid, BinderDto> Binders, long Cursor)> PullAllAsync(
        this HttpClient client, long since = 0)
    {
        var notes = new Dictionary<Guid, NoteDto>();
        var binders = new Dictionary<Guid, BinderDto>();
        var cursor = since;
        for (var page = 0; page < 1000; page++)
        {
            var response = await client.PullAsync(cursor);
            foreach (var n in response.Notes) notes[n.Id] = n;
            foreach (var b in response.Binders) binders[b.Id] = b;
            cursor = response.Cursor;
            if (!response.HasMore) break;
        }
        return (notes, binders, cursor);
    }

    /// <summary>The current server copy of a note, as a full pull (since 0) returns it; null when absent.</summary>
    public static async Task<NoteDto?> FindNoteAsync(this HttpClient client, Guid id) =>
        (await client.PullAllAsync()).Notes.GetValueOrDefault(id);

    public static ChangeResult For(this List<ChangeResult> results, Guid id) => Assert.Single(results, r => r.Id == id);

    // ------------------------------------------------------------------------------------------- Images

    /// <summary>A tiny valid 1×1 PNG.</summary>
    public static byte[] Png(int padding = 0)
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        return padding == 0 ? png : [.. png, .. new byte[padding]];
    }

    /// <summary>A JPEG-looking payload (magic bytes are what the server checks).</summary>
    public static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. "JFIF"u8, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0xFF, 0xD9];

    public static ByteArrayContent Bytes(byte[] data, string contentType = "application/octet-stream")
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return content;
    }

    public static async Task<HttpResponseMessage> UploadAsync(this HttpClient client, Guid id, byte[] data, string contentType = "image/png") =>
        await client.PutAsync($"/api/attachments/{id}", Bytes(data, contentType));
}
