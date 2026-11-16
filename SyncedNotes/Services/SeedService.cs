using SyncedNotes.Data;
using SyncedNotes.Infrastructure;
using SyncedNotes.Sync;

namespace SyncedNotes.Services;

/// <summary>Creates the starter binders and a welcome note for a brand-new account.</summary>
public sealed class SeedService(AppDbContext db, SyncService sync, TimeProvider time)
{
    private static readonly (string Name, string Color, string Icon)[] DefaultBinders =
    [
        ("Work", "blue", "bi-briefcase"),
        ("Home", "green", "bi-house-door"),
        ("Business", "purple", "bi-graph-up-arrow"),
        ("Sport", "orange", "bi-trophy"),
    ];

    public Task SeedNewUserAsync(string userId, CancellationToken ct = default) =>
        sync.WriteAsync(userId, state =>
        {
            var now = time.NowMs();
            for (var i = 0; i < DefaultBinders.Length; i++)
            {
                var (name, color, icon) = DefaultBinders[i];
                db.Binders.Add(new Binder
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Name = name,
                    Color = color,
                    Icon = icon,
                    SortOrder = i,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Rev = ++state.Seq,
                });
            }

            var welcome = new Note
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Title = "Welcome to SyncedNotes",
                Content = WelcomeMarkdown,
                Icon = "bi-stars",
                Color = "indigo",
                Tags = ["getting-started"],
                IsPinned = true,
                SortOrder = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };
            welcome.Rev = welcome.ContentRev = ++state.Seq;
            db.Notes.Add(welcome);
            return Task.FromResult(true);
        }, ct);

    private const string WelcomeMarkdown = """
        Notes are written in **Markdown** and saved on this device first, so everything works offline and syncs when you're back online.

        ## Quick tour

        - [x] Create a note with **Alt + N** (or the **+** button)
        - [ ] Open the command palette with **Ctrl + K** and type what you want to do
        - [ ] Press **Ctrl + E** to switch between editing, split view and preview
        - [ ] Pin notes, give them a colour or icon, and drag them into a binder
        - [ ] Paste or drop an image straight into the editor
        - [ ] Turn on **App lock** in *Settings → Security*

        > Tip: press **?** to see every keyboard shortcut.

        ## Code blocks

        ```csharp
        app.MapGet("/hello", () => "Hello from SyncedNotes!");
        ```

        ```sql
        SELECT Title FROM Notes WHERE IsPinned = 1 ORDER BY UpdatedAt DESC;
        ```

        ## Tables

        | Shortcut | Action |
        | --- | --- |
        | `Ctrl + K` | Command palette |
        | `Alt + N` | New note |
        | `Ctrl + Shift + L` | Lock the app |

        Link to another note with double brackets, like [[Welcome to SyncedNotes]].
        """;
}
