# SyncedNotes in PowerToys Command Palette

Capture and find notes from [PowerToys Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview)
(<kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>Space</kbd>). There are two ways to set it up:

| | Option A: Bookmarks | Option B: Native extension |
| --- | --- | --- |
| Install | Nothing. Built into Command Palette | Build and deploy `SyncedNotes.CmdPal` from Visual Studio |
| Sign-in | Your browser session | An API token (Settings → Integrations) |
| New note | Prompts inline, then opens the web app, which creates the note | Saved in the background with a confirmation toast. The palette never leaves the keyboard |
| Search | Opens the web app's search | Live results inside the palette as you type |
| Extras | | Full form (title, tags, binder, pinned), "Add note to SyncedNotes: …" from the home page, copy links |

---

## Option A: zero install with Command Palette Bookmarks

Command Palette Bookmarks can contain `{placeholders}`. When you run such a bookmark, Command Palette
asks for the values inline in the search box, URL-encodes them, and opens the resulting URL. The
SyncedNotes web app turns these URLs into a new note or a search:

| Bookmark name | URL |
| --- | --- |
| New note | `https://<your-host>/new?text={note}` |
| New note with title | `https://<your-host>/new?title={title}&text={note}` |
| Search notes | `https://<your-host>/?q={search}` |

To add one:

1. Open Command Palette, type **Add bookmark** and press <kbd>Enter</kbd>.
2. Enter a **Name**, for example `New note`, and use one of the URLs above as the **URL or file path**
   (replace `<your-host>`, for example `https://notes.example.com/new?text={note}`). Then select **Save**.
3. To use it, type `new note`, press <kbd>Enter</kbd>, type your text into the `note` field and press <kbd>Enter</kbd> again.

Notes:

- Placeholder names may only contain letters, digits, `-` and `_` (for example `{note}`, `{search}`).
- The URL opens in your default browser, or in the installed SyncedNotes app if it handles links to
  your server. You must already be signed in there. No API token is involved.
- Like any other command, a bookmark can get an alias or a global hotkey in Command Palette
  settings. You can also pin it to Home or to the Dock from its context menu.

---

## Option B: the native extension (`SyncedNotes.CmdPal`)

A Command Palette extension that talks to the SyncedNotes "quick" API with a personal API token.
The project follows the current official extension template, which is what Command Palette's
**Create a new extension** command generates.

### What it adds

| Command | What it does |
| --- | --- |
| **New note** | Type a note and press <kbd>Enter</kbd> to save it. The server uses the first line as the title. <kbd>Ctrl</kbd>+<kbd>Enter</kbd> saves the note and opens it. The context menu (<kbd>Ctrl</kbd>+<kbd>K</kbd>) has **Save as pinned note** and **Continue in the full note form…**. |
| New note form | Title, Markdown text, comma-separated tags, a **Binder** dropdown (loaded from the server) and **Pin this note**, with **Save note** and **Save and open** buttons. Open it from the **New note** page or from the **New note** command's context menu. |
| **Search notes** | Searches as you type (debounced, stale requests are cancelled). An empty search lists recent notes, pinned first. Each result shows the title, a snippet, the binder and tags. <kbd>Enter</kbd> opens the note in the browser, <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>C</kbd> copies its link, and the context menu has **Copy title**, **Copy Markdown link** and **Show details**. |
| **Add note to SyncedNotes: …** | A fallback command. Type anything on the Command Palette home page and this row appears under the results. <kbd>Enter</kbd> saves the text as a note. |
| **Open SyncedNotes** | Opens your server in the browser. |
| **Set up SyncedNotes** | Shown until the server URL and token are configured. It opens the extension settings. |

After saving, the palette closes and a toast confirms the note. When you reopen **New note**, it
offers to open the note you just saved. You can change this behavior in the extension settings.

### Requirements

- Windows with PowerToys installed and Command Palette enabled.
- [Developer Mode](https://learn.microsoft.com/windows/advanced-settings/developer-mode) turned on
  (Settings → System → For developers).
- **Visual Studio 2026** with the components listed in `SyncedNotes.CmdPal/.vsconfig`. Those are
  .NET desktop development, WinUI application development (Windows App SDK C# templates) and the
  Windows 11 SDK (10.0.26100). Visual Studio offers to install any missing components when you open
  the solution. The project targets `net10.0-windows10.0.26100.0`, and .NET 10 is only supported in
  Visual Studio 2026 and later.

### Build and deploy

1. Open `tools/PowerToys/SyncedNotes.CmdPal/SyncedNotes.CmdPal.sln` in Visual Studio.
2. Select the **Debug** configuration and the **x64** or **ARM64** platform that matches your PC.
3. Select **Build → Deploy SyncedNotes.CmdPal**. Building alone doesn't update the installed package,
   and the "SyncedNotes.CmdPal (Unpackaged)" launch profile doesn't deploy it either.
4. In Command Palette, type **Reload** and choose **Reload Command Palette extensions**.
5. Type **Set up SyncedNotes** (or **SyncedNotes**) to find the new commands.

Repeat steps 3 and 4 after every change. Command Palette doesn't notice redeployed packages until
you run **Reload**. To debug, keep the Debug configuration, press <kbd>F5</kbd> with the
"SyncedNotes.CmdPal (Package)" profile, and run **Reload**.

### Configure

1. In SyncedNotes, go to **Settings → Integrations** and create an API token. Tokens look like
   `snp_` followed by 43 characters.
2. Open the extension settings in one of these ways:
   - run **Set up SyncedNotes**
   - choose **SyncedNotes settings** from any SyncedNotes command's context menu (<kbd>Ctrl</kbd>+<kbd>K</kbd>)
   - go to Command Palette settings → Extensions → Installed → SyncedNotes
3. Fill in the settings and select **Save**:

| Setting | Notes |
| --- | --- |
| Server URL | The address you open SyncedNotes at, for example `https://notes.example.com`. `https://` is assumed if you leave out the scheme. Trailing slashes and anything after `#` are removed. Use `https://` because the token is sent with every request. |
| API token | Pasted as-is. Surrounding spaces and a pasted `Bearer ` prefix are removed. |
| After saving a note | **Close Command Palette and show a confirmation** (default), **Keep Command Palette open and offer to open the note**, or **Open the new note in the browser**. |

The fallback row is on by default. It appears in the fallback section below the regular results. To
turn it off, or to mix it into the regular results, open the **Fallback commands** section of the
extension's settings page (**Include in the Global result**). **Manage fallback order** on the
Extensions page changes its priority.

### Troubleshooting

- **The commands don't appear.** Make sure you used **Deploy**, not just **Build**, then run
  **Reload Command Palette extensions**. Check that the extension is listed and enabled under
  Command Palette settings → Extensions → Installed.
- **"SyncedNotes didn't accept the API token (401)".** The token was revoked or mistyped. Create a
  new one under Settings → Integrations and paste it into the extension settings.
- **"The server redirects to …".** The extension doesn't follow redirects, because HttpClient drops
  the `Authorization` header when it does. Use the exact URL from the message, which is usually the
  `https://` address.
- **TLS or certificate errors.** The extension uses Windows' certificate validation. If your server
  uses a self-signed certificate, add the certificate (or its CA) to the Windows certificate store,
  or use a certificate from a public CA.
- **"…answered, but not like a SyncedNotes server".** The URL points at something else, such as a
  reverse proxy's login page or an older server without `/api/v1/quick/*`.

### Privacy and security

- The server URL and API token are stored by the Command Palette extension toolkit in the
  extension's `settings.json`. That file is in the package's per-user data folder under
  `%LOCALAPPDATA%\Packages\SyncedNotes.CmdPal_…\`. The settings page masks the token. Treat the file
  like a password, and revoke the token in SyncedNotes if it leaks. Uninstalling the package
  ("SyncedNotes for Command Palette" in Windows Settings → Apps) removes the file.
- The extension writes to Command Palette's log only which operation failed and the HTTP status. It
  never logs the token, the server URL, search text or note content.
- Links that the server returns are opened only if they are `http(s)` URLs.

### Project layout

```text
SyncedNotes.CmdPal/                  solution folder (mirrors the official template)
├── Directory.Build.props            ┐
├── Directory.Packages.props         │ unchanged from the template (central package versions)
├── nuget.config                     │
├── .vsconfig                        ┘ Visual Studio components to install
├── SyncedNotes.CmdPal.sln
└── SyncedNotes.CmdPal/
    ├── SyncedNotes.CmdPal.csproj    template csproj (MSIX tooling, trimming/AOT analyzers)
    ├── Package.appxmanifest         COM server + com.microsoft.commandpalette app extension
    ├── Program.cs                   COM server hosting (template, unchanged pattern)
    ├── SyncedNotesExtension.cs      IExtension; its [Guid] matches both CLSIDs in the manifest
    ├── SyncedNotesCommandsProvider.cs   top-level commands, fallback, settings
    ├── Pages/                       New note, note form, Search notes, result items
    ├── Fallbacks/                   "Add note to SyncedNotes: …"
    ├── Commands/                    save command
    ├── Services/                    API client, DTOs (source-generated JSON), binder cache
    ├── Helpers/                     settings (JsonSettingsManager), icons, text helpers
    ├── Assets/                      package images (template placeholders)
    └── Properties/                  launchSettings.json + publish profiles (needed to deploy)
```

Notes for maintainers:

- The template and package versions come from `src/modules/cmdpal/ExtensionTemplate` in the
  PowerToys repository. `main` and release v0.103.3151 are identical:
  `Microsoft.CommandPalette.Extensions` 0.11.260520004, `Microsoft.Windows.CsWinRT` 2.3.1,
  `Shmuelie.WinRTServer` 2.1.1 and `Microsoft.Windows.SDK.BuildTools.MSIX` 1.7.251221100. The current
  template no longer references `Microsoft.WindowsAppSDK`.
- The COM class id `30A44282-4941-4E0D-98C5-A4B06AC9FD41` must be identical in three places: the
  `[Guid]` attribute and both `ClassId`/`Id` values in `Package.appxmanifest`.
- `Publisher="CN=SyncedNotes"` is enough for Developer Mode deployment. To sign and distribute an
  `.msix`, change it to match the subject of your signing certificate.
- The API this extension uses:
  - `POST /api/v1/quick/notes` with `{ title, content, tags, binder, pinned }`. Returns 201 with `{ id, title, url }`.
  - `GET /api/v1/quick/notes?q=&limit=` returns `[{ id, title, snippet, binder, tags, pinned, updatedAt, url }]`.
  - `GET /api/v1/quick/binders` returns `[{ id, name }]`.

  Every request sends `Authorization: Bearer <token>`. Errors are RFC 7807 problem details.
