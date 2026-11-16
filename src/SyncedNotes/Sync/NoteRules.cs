using System.Text;
using System.Text.RegularExpressions;
using SyncedNotes.Data;

namespace SyncedNotes.Sync;

/// <summary>Validation and normalisation rules shared by sync, quick-add and seeding.</summary>
public static partial class NoteRules
{
    /// <summary>Allowed clock skew for client-supplied timestamps.</summary>
    private const long MaxFutureSkewMs = 5 * 60 * 1000;

    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        var sb = new StringBuilder(Math.Min(title.Length, NoteLimits.MaxTitleLength));
        foreach (var c in title)
        {
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        var result = sb.ToString().Trim();
        return result.Length > NoteLimits.MaxTitleLength ? result[..NoteLimits.MaxTitleLength].TrimEnd() : result;
    }

    public static string NormalizeBinderName(string? name)
    {
        var result = NormalizeTitle(name);
        return result.Length > NoteLimits.MaxBinderNameLength ? result[..NoteLimits.MaxBinderNameLength].TrimEnd() : result;
    }

    public static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        var result = new List<string>();
        if (tags is null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tags)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var tag = WhitespaceRegex().Replace(raw.Trim().TrimStart('#'), " ");
            tag = new string(tag.Where(c => !char.IsControl(c) && c != ',').ToArray()).Trim();
            if (tag.Length == 0) continue;
            if (tag.Length > NoteLimits.MaxTagLength) tag = tag[..NoteLimits.MaxTagLength].TrimEnd();
            if (seen.Add(tag)) result.Add(tag);
            if (result.Count >= NoteLimits.MaxTags) break;
        }
        return result;
    }

    public static string? NormalizeColor(string? color) =>
        color is not null && NoteLimits.Colors.Contains(color) ? color : null;

    public static string? NormalizeIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        icon = icon.Trim();
        if (icon.StartsWith("bi-", StringComparison.Ordinal))
        {
            return BootstrapIconRegex().IsMatch(icon) ? icon : null;
        }
        // Otherwise treat it as an emoji / short symbol. It is always HTML-escaped when rendered.
        return icon.Length <= 16 && !icon.Any(c => char.IsControl(c) || c is '<' or '>' or '&' or '"' or '\'') ? icon : null;
    }

    public static double NormalizeSortOrder(double value) => double.IsFinite(value) ? value : 0;

    public static long ClampTime(long value, long now) =>
        value <= 0 || value > now + MaxFutureSkewMs ? now : value;

    public static long? ClampOptionalTime(long? value, long now) =>
        value is null or <= 0 ? null : ClampTime(value.Value, now);

    /// <summary>Derives a title from the first meaningful line of Markdown content.</summary>
    public static string DeriveTitle(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        foreach (var rawLine in content.Split('\n'))
        {
            var line = LeadingMarkdownRegex().Replace(rawLine.Trim(), "").Trim();
            if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal)) continue;
            line = NormalizeTitle(line);
            return line.Length > 80 ? line[..80].TrimEnd() + "…" : line;
        }
        return "";
    }

    public static string ConflictTitle(string title, long now)
    {
        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime.ToString("yyyy-MM-dd HH:mm");
        var baseTitle = string.IsNullOrWhiteSpace(title) ? "Untitled" : title;
        return NormalizeTitle($"{baseTitle} (conflicted copy {stamp} UTC)");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^bi-[a-z0-9-]{1,36}$")]
    private static partial Regex BootstrapIconRegex();

    [GeneratedRegex(@"^(#{1,6}\s+|>\s*|[-*+]\s+(\[[ xX]\]\s+)?|\d+[.)]\s+)+")]
    private static partial Regex LeadingMarkdownRegex();
}
