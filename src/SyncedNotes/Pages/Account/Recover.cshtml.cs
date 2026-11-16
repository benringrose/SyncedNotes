using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SyncedNotes.Pages.Account;

[AllowAnonymous]
public class RecoverModel : PageModel
{
    public void OnGet()
    {
    }
}
