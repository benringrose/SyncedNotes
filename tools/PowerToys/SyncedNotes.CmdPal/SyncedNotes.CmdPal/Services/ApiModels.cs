using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SyncedNotes.CmdPal.Services;

// DTOs for the SyncedNotes "quick" API (/api/v1/quick/*). JSON is camelCase.
// Response DTOs are deliberately lenient (everything nullable) so a slightly different
// server version doesn't break the extension.

/// <summary>Body of <c>POST /api/v1/quick/notes</c>.</summary>
internal sealed class CreateNoteRequest
{
    /// <summary>Gets or sets the title (max 200 chars). Null/empty: the server derives it from the first line of <see cref="Content"/>.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the Markdown content (may be empty when a title is given).</summary>
    public string Content { get; set; } = string.Empty;

    public List<string>? Tags { get; set; }

    /// <summary>Gets or sets the binder NAME (case-insensitive; ignored by the server if not found).</summary>
    public string? Binder { get; set; }

    public bool? Pinned { get; set; }
}

/// <summary>201 response of <c>POST /api/v1/quick/notes</c>.</summary>
internal sealed class CreatedNote
{
    public string? Id { get; set; }

    public string? Title { get; set; }

    public string? Url { get; set; }
}

/// <summary>Item of the <c>GET /api/v1/quick/notes</c> response.</summary>
internal sealed class NoteSummary
{
    public string? Id { get; set; }

    public string? Title { get; set; }

    public string? Snippet { get; set; }

    public string? Binder { get; set; }

    public List<string>? Tags { get; set; }

    public bool? Pinned { get; set; }

    /// <summary>Gets or sets the last update time as Unix epoch milliseconds.</summary>
    public long? UpdatedAt { get; set; }

    public string? Url { get; set; }
}

/// <summary>Item of the <c>GET /api/v1/quick/binders</c> response.</summary>
internal sealed class BinderInfo
{
    public string? Id { get; set; }

    public string? Name { get; set; }
}

/// <summary>RFC 7807 problem details, plus ASP.NET Core's validation <c>errors</c> map.</summary>
internal sealed class ProblemDetailsResponse
{
    public string? Type { get; set; }

    public string? Title { get; set; }

    public string? Detail { get; set; }

    public int? Status { get; set; }

    public JsonElement Errors { get; set; }
}

// Source-generated (reflection-free) serialization: the template enables the trimming/AOT
// analyzers and trims Release builds, so reflection-based JsonSerializer calls must be avoided.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(CreateNoteRequest))]
[JsonSerializable(typeof(CreatedNote))]
[JsonSerializable(typeof(List<NoteSummary>))]
[JsonSerializable(typeof(List<BinderInfo>))]
[JsonSerializable(typeof(ProblemDetailsResponse))]
internal sealed partial class SyncedNotesJsonContext : JsonSerializerContext
{
}
