using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SyncedNotes.CmdPal.Helpers;

/// <summary>Segoe Fluent Icons glyphs used by the extension (they follow the light/dark theme).</summary>
internal static class Icons
{
    internal static IconInfo SyncedNotes { get; } = new(""); // QuickNote

    internal static IconInfo NewNote { get; } = new(""); // QuickNote

    internal static IconInfo Save { get; } = new(""); // Add

    internal static IconInfo Form { get; } = new(""); // Edit

    internal static IconInfo Search { get; } = new(""); // Search

    internal static IconInfo Note { get; } = new(""); // Document

    internal static IconInfo PinnedNote { get; } = new(""); // Pin

    internal static IconInfo Open { get; } = new(""); // OpenInNewWindow

    internal static IconInfo Globe { get; } = new(""); // Globe

    internal static IconInfo Copy { get; } = new(""); // Copy

    internal static IconInfo Link { get; } = new(""); // Link

    internal static IconInfo Settings { get; } = new(""); // Settings

    internal static IconInfo Binder { get; } = new(""); // Folder

    internal static IconInfo Refresh { get; } = new(""); // Refresh

    internal static IconInfo Warning { get; } = new(""); // Warning

    internal static IconInfo Info { get; } = new(""); // Info
}
