using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SyncedNotes.CmdPal.Helpers;

internal static class TextHelpers
{
    private static readonly char[] _tagSeparators = [',', ';', '\r', '\n'];

    /// <summary>Collapses whitespace runs (including line breaks) into single spaces and trims the result.</summary>
    public static string SingleLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Returns a single-line version of <paramref name="text"/>, cut to <paramref name="maxLength"/> characters with an ellipsis.</summary>
    public static string Ellipsize(string? text, int maxLength)
    {
        var line = SingleLine(text);
        if (line.Length <= maxLength)
        {
            return line;
        }

        var cut = Math.Max(1, maxLength - 1);
        if (char.IsHighSurrogate(line[cut - 1]))
        {
            cut--; // don't split a surrogate pair (emoji etc.)
        }

        return string.Concat(line.AsSpan(0, cut).TrimEnd(), "…");
    }

    /// <summary>Gets the first non-empty line, trimmed (this is what the server uses as the title when none is given).</summary>
    public static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (!trimmed.IsEmpty)
            {
                return trimmed.ToString();
            }
        }

        return string.Empty;
    }

    /// <summary>Parses "work, ideas; #todo" into ["work", "ideas", "todo"]: trimmed, de-duplicated, without a leading '#'.</summary>
    /// <returns>The tags, or <see langword="null"/> when there are none.</returns>
    public static List<string>? ParseTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(_tagSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var tag = part.TrimStart('#').Trim();
            if (tag.Length > 0 && seen.Add(tag))
            {
                tags.Add(tag);
            }
        }

        return tags.Count > 0 ? tags : null;
    }

    public static string FormatRelative(DateTimeOffset when, DateTimeOffset now)
    {
        var delta = now - when;
        if (delta < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{(int)delta.TotalMinutes} min ago";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{(int)delta.TotalHours} h ago";
        }

        var local = when.ToLocalTime();
        var localNow = now.ToLocalTime();
        if (local.Date == localNow.Date.AddDays(-1))
        {
            return "yesterday";
        }

        if (delta < TimeSpan.FromDays(7))
        {
            return local.ToString("dddd", CultureInfo.CurrentCulture);
        }

        return local.Year == localNow.Year
            ? local.ToString("MMM d", CultureInfo.CurrentCulture)
            : local.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    public static string FormatTimestamp(DateTimeOffset when) =>
        when.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
}
