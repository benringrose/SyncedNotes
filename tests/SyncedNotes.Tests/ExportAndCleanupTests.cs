using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SyncedNotes.Data;
using SyncedNotes.Services;
using SyncedNotes.Tests.Infrastructure;
using static SyncedNotes.Tests.Infrastructure.Api;

namespace SyncedNotes.Tests;

/// <summary>The entries of a downloaded export zip.</summary>
public sealed class ExportArchive(Dictionary<string, byte[]> entries)
{
    public IReadOnlyCollection<string> Paths => entries.Keys;

    public byte[] Bytes(string path) => entries[path];

    public string Text(string path) => Encoding.UTF8.GetString(entries[path]);

    public static ExportArchive Read(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var result = new Dictionary<string, byte[]>();
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            result.Add(entry.FullName, buffer.ToArray());
        }
        return new ExportArchive(result);
    }
}

/// <summary>GET /api/export: Markdown files with YAML front matter, one folder per binder, plus images.</summary>
public class ExportTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    public static async Task<ExportArchive> DownloadExportAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/export");
        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        return ExportArchive.Read(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_export_is_a_zip_of_markdown_notes_grouped_by_binder_with_their_images()
    {
        var client = factory.CreateClientFor(await factory.CreateUserAsync());
        var image = Guid.NewGuid();
        var unusedImage = Guid.NewGuid();
        var png = Png();
        await client.UploadAsync(image, png).AssertStatusAsync(HttpStatusCode.OK);
        await client.UploadAsync(unusedImage, Jpeg()).AssertStatusAsync(HttpStatusCode.OK);

        var work = NewBinder("Work");
        var odd = NewBinder("Side/Projects: 2026");
        var meeting = NewNote("Meeting notes", $"Agenda\n\n![chart](/attachments/{image})\n", binderId: work.Id, tags: ["team", "q4"]);
        meeting.Pinned = true;
        meeting.Color = "blue";
        meeting.Icon = "bi-people";
        var meeting2 = NewNote("Meeting notes", "second meeting", binderId: work.Id);
        var loose = NewNote("Loose thought", "hmm");
        var archived = NewNote("Old plan", $"![x](/attachments/{image.ToString().ToUpperInvariant()})", binderId: odd.Id);
        archived.Archived = true;
        var trashed = NewNote("Trashed note", "in the bin");
        trashed.TrashedAt = NowMs();
        var deleted = NewNote("Deleted note", "gone");
        var untitled = NewNote("", "no title");
        var unsafeName = NewNote("Q: what/why?", "chars");
        var created = await client.PushAsync(binders: [work, odd], notes: [meeting, meeting2, loose, archived, trashed, deleted, untitled, unsafeName]);
        await client.PushNotesAsync(DeleteOf(created.Notes.Single(n => n.Id == deleted.Id)));

        var response = await client.GetAsync("/api/export");
        await response.AssertStatusAsync(HttpStatusCode.OK);
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Matches(@"^SyncedNotes-\d{4}-\d{2}-\d{2}\.zip$", disposition.FileName!.Trim('"'));
        var zip = ExportArchive.Read(await response.Content.ReadAsByteArrayAsync());

        var expectedPaths = new HashSet<string>
        {
            "notes/Work/Meeting notes.md",
            "notes/Work/Meeting notes (2).md",
            "notes/Unfiled/Loose thought.md",
            "notes/Archive/Side-Projects- 2026/Old plan.md",
            "notes/Unfiled/Untitled.md",
            "notes/Unfiled/Q- what-why-.md",
            $"attachments/{image}.png",
            "README.txt",
        };
        Assert.Equal(expectedPaths, zip.Paths.ToHashSet());

        // Two notes with the same title both survive.
        var meetings = new[] { zip.Text("notes/Work/Meeting notes.md"), zip.Text("notes/Work/Meeting notes (2).md") };
        var first = Assert.Single(meetings, m => m.Contains($"id: {meeting.Id}"));
        Assert.Single(meetings, m => m.Contains($"id: {meeting2.Id}"));

        // YAML front matter, then the Markdown with image links made relative to the file.
        var lines = first.Split('\n');
        Assert.Equal("---", lines[0]);
        Assert.Contains("title: \"Meeting notes\"", lines);
        Assert.Contains("binder: \"Work\"", lines);
        Assert.Contains("tags: [\"team\", \"q4\"]", lines);
        Assert.Contains("pinned: true", lines);
        Assert.Contains("color: blue", lines);
        Assert.Contains("icon: \"bi-people\"", lines);
        var createdIso = DateTimeOffset.FromUnixTimeMilliseconds(meeting.CreatedAt).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        Assert.Contains($"created: {createdIso}", lines);
        Assert.Contains(lines, l => l.StartsWith("updated: "));
        Assert.DoesNotContain("archived: true", lines);
        Assert.EndsWith($"---\n\nAgenda\n\n![chart](../../attachments/{image}.png)\n", first);

        var old = zip.Text("notes/Archive/Side-Projects- 2026/Old plan.md");
        Assert.Contains("archived: true\n", old);
        Assert.Contains("binder: \"Side/Projects: 2026\"\n", old);
        Assert.EndsWith($"![x](../../../attachments/{image}.png)", old);

        var noTitle = zip.Text("notes/Unfiled/Untitled.md");
        Assert.Contains("title: \"\"\n", noTitle);
        Assert.DoesNotContain("binder:", noTitle);
        Assert.DoesNotContain("tags:", noTitle);

        Assert.Equal(png, zip.Bytes($"attachments/{image}.png"));
        var readme = zip.Text("README.txt");
        Assert.Contains("6 notes", readme);
        Assert.Contains("1 images", readme);
    }

    [Fact]
    public async Task Links_to_images_the_user_does_not_own_are_left_alone()
    {
        var alice = factory.CreateClientFor(await factory.CreateUserAsync());
        var bob = factory.CreateClientFor(await factory.CreateUserAsync());
        var aliceImage = Guid.NewGuid();
        await alice.UploadAsync(aliceImage, Png()).AssertStatusAsync(HttpStatusCode.OK);
        await bob.PushNotesAsync(NewNote("Borrowed", $"![](/attachments/{aliceImage})"));

        var zip = await DownloadExportAsync(bob);

        Assert.Equal(new HashSet<string> { "README.txt", "notes/Unfiled/Borrowed.md" }, zip.Paths.ToHashSet());
        Assert.EndsWith($"![](/attachments/{aliceImage})", zip.Text("notes/Unfiled/Borrowed.md"));
    }

    [Fact]
    public async Task An_empty_account_exports_just_the_readme()
    {
        var client = factory.CreateClientFor(await factory.CreateUserAsync());

        var zip = await DownloadExportAsync(client);

        Assert.Equal(new[] { "README.txt" }, zip.Paths);
        Assert.Contains("0 notes", zip.Text("README.txt"));
    }

    [Theory]
    [InlineData("th-TH")] // Thai Buddhist calendar: 2026 is 2569
    [InlineData("ar-SA")] // Umm al-Qura calendar
    public void Front_matter_timestamps_are_iso_8601_whatever_the_server_culture(string culture)
    {
        var created = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Title = "Dated",
            CreatedAt = created.ToUnixTimeMilliseconds(),
            UpdatedAt = created.AddDays(1).ToUnixTimeMilliseconds(),
        };

        var previous = CultureInfo.CurrentCulture;
        string yaml;
        try
        {
            // The server's culture comes from the OS (e.g. LANG=th_TH.UTF-8 or Windows regional settings).
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            yaml = ExportService.BuildFrontMatter(note, null);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Contains("created: 2026-01-02T03:04:05.678Z\n", yaml);
        Assert.Contains("updated: 2026-01-03T03:04:05.678Z\n", yaml);
    }
}

/// <summary>CleanupService.RunOnceAsync: trash retention and unused images.</summary>
public class CleanupTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    private Task RunCleanupAsync(DateTimeOffset now) =>
        new CleanupService(factory.Services.GetRequiredService<IServiceScopeFactory>(), new FakeTimeProvider(now), NullLogger<CleanupService>.Instance)
            .RunOnceAsync(CancellationToken.None);

    private static long Ms(DateTimeOffset time) => time.ToUnixTimeMilliseconds();

    [Fact]
    public async Task Notes_trashed_more_than_30_days_ago_are_permanently_deleted()
    {
        var now = DateTimeOffset.UtcNow;
        var client = factory.CreateClientFor(await factory.CreateUserAsync());
        var expired = NewNote("expired", "old text", tags: ["x"]);
        expired.TrashedAt = Ms(now - TimeSpan.FromDays(31));
        var recent = NewNote("recently trashed");
        recent.TrashedAt = Ms(now - TimeSpan.FromDays(29));
        var live = NewNote("live");
        var archived = NewNote("archived");
        archived.Archived = true;
        var created = await client.PushNotesAsync(expired, recent, live, archived);

        await RunCleanupAsync(now);

        // Other devices learn about it like any other permanent delete: a tombstone with a new revision.
        var changes = await client.PullAsync(since: created.Cursor);
        var tombstone = Assert.Single(changes.Notes);
        Assert.Equal(expired.Id, tombstone.Id);
        Assert.True(tombstone.Deleted);
        Assert.Equal("", tombstone.Title);
        Assert.Equal("", tombstone.Content);
        Assert.Empty(tombstone.Tags);
        Assert.True(tombstone.Rev > created.Cursor);
        Assert.Equal(Ms(now), tombstone.UpdatedAt);
        Assert.Equal(new HashSet<Guid> { recent.Id, live.Id, archived.Id }, (await client.PullAsync()).Notes.Select(n => n.Id).ToHashSet());

        // Running again changes nothing.
        await RunCleanupAsync(now);
        Assert.Empty((await client.PullAsync(since: changes.Cursor)).Notes);
    }

    [Fact]
    public async Task Unused_images_older_than_two_days_are_removed()
    {
        var uploadedAt = DateTimeOffset.UtcNow;
        var cleanupAt = uploadedAt + TimeSpan.FromDays(3);
        var alice = factory.CreateClientFor(await factory.CreateUserAsync());
        var bob = factory.CreateClientFor(await factory.CreateUserAsync());

        var usedByLive = Guid.NewGuid();
        var usedByArchived = Guid.NewGuid();
        var usedByTrashed = Guid.NewGuid();
        var usedOnlyByExpired = Guid.NewGuid();
        var unused = Guid.NewGuid();
        var linkedByBob = Guid.NewGuid();
        foreach (var id in new[] { usedByLive, usedByArchived, usedByTrashed, usedOnlyByExpired, unused, linkedByBob })
        {
            await alice.UploadAsync(id, Png()).AssertStatusAsync(HttpStatusCode.OK);
        }

        var live = NewNote("live", $"![](/attachments/{usedByLive})");
        var archived = NewNote("archived", $"<img src=\"/attachments/{usedByArchived.ToString().ToUpperInvariant()}\">");
        archived.Archived = true;
        var trashed = NewNote("trashed", $"![](/attachments/{usedByTrashed})");
        trashed.TrashedAt = Ms(cleanupAt - TimeSpan.FromDays(10));
        var expired = NewNote("expired", $"![](/attachments/{usedOnlyByExpired})");
        expired.TrashedAt = Ms(cleanupAt - TimeSpan.FromDays(31));
        await alice.PushNotesAsync(live, archived, trashed, expired);
        // A link from another account doesn't keep Alice's image alive.
        await bob.PushNotesAsync(NewNote("bob", $"![](/attachments/{linkedByBob})"));

        // Images uploaded less than two days ago are never removed (the note may not have synced yet).
        await RunCleanupAsync(uploadedAt + TimeSpan.FromDays(1));
        Guid[] all = [usedByLive, usedByArchived, usedByTrashed, usedOnlyByExpired, unused, linkedByBob];
        Assert.Equal(6, await factory.WithDbAsync(db => db.Attachments.CountAsync(a => all.Contains(a.Id))));

        await RunCleanupAsync(cleanupAt);

        var remaining = await factory.WithDbAsync(db => db.Attachments.AsNoTracking()
            .Where(a => all.Contains(a.Id))
            .Select(a => a.Id).ToListAsync());
        Assert.Equal(new HashSet<Guid> { usedByLive, usedByArchived, usedByTrashed }, remaining.ToHashSet());
        await alice.GetAsync($"/attachments/{usedByLive}").AssertStatusAsync(HttpStatusCode.OK);
        await alice.GetAsync($"/attachments/{unused}").AssertStatusAsync(HttpStatusCode.NotFound);
        Assert.Null(await alice.FindNoteAsync(expired.Id)); // its note was purged in the same run
    }
}
