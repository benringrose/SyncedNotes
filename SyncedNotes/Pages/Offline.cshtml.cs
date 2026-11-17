using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SyncedNotes.Pages;

[AllowAnonymous]
public class OfflineModel : PageModel
{
    public void OnGet()
    {
    }
}
