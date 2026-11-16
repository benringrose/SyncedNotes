using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SyncedNotes.Pages;

/// <summary>
/// The app shell. It contains no user data (everything is rendered client-side from the offline store),
/// so the service worker can safely cache it for offline use.
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    public void OnGet()
    {
        Response.Headers.CacheControl = "no-cache";
    }
}
