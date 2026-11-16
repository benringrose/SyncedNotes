using SyncedNotes.Data;
using SyncedNotes.Endpoints;
using SyncedNotes.Infrastructure;
using SyncedNotes.Services;
using SyncedNotes.Sync;

namespace SyncedNotes.Tests;

/// <summary>Unit tests for the pure validation and formatting helpers the endpoints rely on.</summary>
public class NoteRulesTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  Hello  ", "Hello")]
    [InlineData("a\tb\nc\u0000d\u001Fe", "a b c d e")]
    [InlineData("Émoji \U0001F600 ok", "Émoji \U0001F600 ok")]
    public void Titles_lose_control_characters_and_surrounding_whitespace(string? title, string expected) =>
        Assert.Equal(expected, NoteRules.NormalizeTitle(title));

    [Fact]
    public void Titles_and_binder_names_are_capped()
    {
        Assert.Equal(NoteLimits.MaxTitleLength, NoteRules.NormalizeTitle(new string('t', 1000)).Length);
        Assert.Equal("word", NoteRules.NormalizeTitle("word" + new string(' ', NoteLimits.MaxTitleLength) + "tail"));
        Assert.Equal(NoteLimits.MaxBinderNameLength, NoteRules.NormalizeBinderName(new string('b', 100)).Length);
    }

    [Theory]
    [InlineData("# Heading\nbody", "Heading")]
    [InlineData("###### Deep heading", "Deep heading")]
    [InlineData("\n\n   \nFirst real line\nsecond", "First real line")]
    [InlineData("> quoted", "quoted")]
    [InlineData("- [x] done item", "done item")]
    [InlineData("* bullet", "bullet")]
    [InlineData("+ plus", "plus")]
    [InlineData("1. step one", "step one")]
    [InlineData("2) step two", "step two")]
    [InlineData("> - [ ] 3. nested", "nested")]
    [InlineData("```csharp\ncode()\n```", "code()")]
    [InlineData("#hashtag", "#hashtag")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Titles_are_derived_from_the_first_meaningful_markdown_line(string? content, string expected) =>
        Assert.Equal(expected, NoteRules.DeriveTitle(content));

    [Fact]
    public void Derived_titles_are_shortened_to_80_characters()
    {
        var title = NoteRules.DeriveTitle(new string('x', 100) + "\nmore");

        Assert.Equal(new string('x', 80) + "…", title);
    }

    [Fact]
    public void Tags_are_normalised()
    {
        var tags = NoteRules.NormalizeTags(["  #Work ", "WORK", "#", "", "a,b", "x\u0001y", "two  words", new string('t', 80)]);

        Assert.Equal(new[] { "Work", "ab", "xy", "two words", new string('t', NoteLimits.MaxTagLength) }, tags);
        Assert.Empty(NoteRules.NormalizeTags(null));
        Assert.Equal(NoteLimits.MaxTags, NoteRules.NormalizeTags(Enumerable.Range(0, 100).Select(i => $"t{i}")).Count);
    }

    [Theory]
    [InlineData("red", "red")]
    [InlineData("indigo", "indigo")]
    [InlineData("RED", null)]
    [InlineData("black", null)]
    [InlineData(null, null)]
    public void Colours_come_from_the_palette(string? color, string? expected) =>
        Assert.Equal(expected, NoteRules.NormalizeColor(color));

    [Theory]
    [InlineData("bi-star", "bi-star")]
    [InlineData("bi-a", "bi-a")]
    [InlineData("bi-arrow-up-right-circle-fill", "bi-arrow-up-right-circle-fill")]
    [InlineData("bi-UPPER", null)]
    [InlineData("bi-with space", null)]
    [InlineData("⭐", "⭐")]
    [InlineData("<script>", null)]
    [InlineData("\"", null)]
    [InlineData(null, null)]
    public void Icons_are_icon_names_or_short_safe_symbols(string? icon, string? expected) =>
        Assert.Equal(expected, NoteRules.NormalizeIcon(icon));

    [Fact]
    public void Bootstrap_icon_names_are_limited_to_36_characters_after_the_prefix()
    {
        Assert.NotNull(NoteRules.NormalizeIcon("bi-" + new string('a', 36)));
        Assert.Null(NoteRules.NormalizeIcon("bi-" + new string('a', 37)));
    }

    [Fact]
    public void Client_timestamps_are_clamped_to_the_server_clock()
    {
        const long now = 1_800_000_000_000;
        const long skew = 5 * 60 * 1000;

        Assert.Equal(now, NoteRules.ClampTime(0, now));
        Assert.Equal(now, NoteRules.ClampTime(-1, now));
        Assert.Equal(123, NoteRules.ClampTime(123, now));
        Assert.Equal(now + skew, NoteRules.ClampTime(now + skew, now));
        Assert.Equal(now, NoteRules.ClampTime(now + skew + 1, now));
        Assert.Null(NoteRules.ClampOptionalTime(null, now));
        Assert.Null(NoteRules.ClampOptionalTime(0, now));
        Assert.Null(NoteRules.ClampOptionalTime(-5, now));
        Assert.Equal(now, NoteRules.ClampOptionalTime(long.MaxValue, now));
    }

    [Fact]
    public void Sort_orders_must_be_finite()
    {
        Assert.Equal(0, NoteRules.NormalizeSortOrder(double.NaN));
        Assert.Equal(0, NoteRules.NormalizeSortOrder(double.PositiveInfinity));
        Assert.Equal(-1.5, NoteRules.NormalizeSortOrder(-1.5));
    }

    [Fact]
    public void Conflicted_copies_are_named_after_the_original_with_a_utc_timestamp()
    {
        var now = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero).ToUnixTimeMilliseconds();

        Assert.Equal("Plan (conflicted copy 2026-03-04 05:06 UTC)", NoteRules.ConflictTitle("Plan", now));
        Assert.Equal("Untitled (conflicted copy 2026-03-04 05:06 UTC)", NoteRules.ConflictTitle("  ", now));
    }
}

public class AttachmentRulesTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0, 0, 0, 0, 0, 0, 0, 0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61, 0, 0, 0, 0, 0, 0 }, "image/gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 }, null)] // RIFF/WAVE audio
    [InlineData(new byte[] { 0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x73 }, "image/avif")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D }, null)] // ftyp isom: MP4 video
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, null)]
    [InlineData(new byte[] { }, null)]
    public void Image_types_are_detected_from_magic_bytes(byte[] data, string? expected) =>
        Assert.Equal(expected, AttachmentRules.DetectImageType(data));

    [Theory]
    [InlineData(null, "image/png", "image.png")]
    [InlineData("   ", "image/jpeg", "image.jpg")]
    [InlineData("holiday.webp", "image/webp", "holiday.webp")]
    [InlineData("dir/sub/pic.gif", "image/gif", "dir_sub_pic.gif")]
    [InlineData("a\u0000b\nc.png", "image/png", "a_b_c.png")]
    public void File_names_are_made_safe(string? name, string contentType, string expected) =>
        Assert.Equal(expected, AttachmentRules.SanitizeFileName(name, contentType));

    [Fact]
    public void Long_file_names_keep_their_extension()
    {
        var name = AttachmentRules.SanitizeFileName(new string('n', 300) + ".png", "image/png");

        Assert.Equal(120, name.Length);
        Assert.EndsWith(".png", name);
    }
}

public class LocalUrlTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/notes", "/notes")]
    [InlineData("/new?text=a#b", "/new?text=a#b")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("evil.example/path", "/")]
    [InlineData("javascript:alert(1)", "/")]
    public void Only_app_relative_urls_are_accepted(string? returnUrl, string expected) =>
        Assert.Equal(expected, HttpRequestExtensions.SafeLocalUrl(returnUrl));

    [Theory]
    [InlineData("/\t/evil.example")]
    [InlineData("/\n/evil.example")]
    [InlineData("/\r\n/evil.example")]
    public void Urls_that_hide_a_host_behind_a_tab_or_newline_are_rejected(string returnUrl)
    {
        // WHATWG URL parsing removes ASCII tabs and newlines, so browsers read these as "//evil.example".
        Assert.Equal("/", HttpRequestExtensions.SafeLocalUrl(returnUrl));
    }
}

public class SnippetTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("# Title\n\nSome **bold** _text_", "Title Some bold text")]
    [InlineData("- [ ] task one\n- [x] task two", "task one task two")]
    [InlineData("See [the docs](https://example.com) ![diagram](/attachments/x)", "See the docs")]
    [InlineData("```js\nlet a = 1;\n```", "js let a = 1;")]
    [InlineData("> quoted | table | row", "quoted table row")]
    public void Snippets_are_plain_text(string? markdown, string expected) =>
        Assert.Equal(expected, QuickEndpoints.Snippet(markdown));

    [Fact]
    public void Snippets_are_shortened_with_an_ellipsis()
    {
        var snippet = QuickEndpoints.Snippet(string.Join(' ', Enumerable.Repeat("word", 100)), length: 20);

        Assert.Equal("word word word word…", snippet);
    }
}
