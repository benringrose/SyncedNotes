using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SyncedNotes.Auth;
using SyncedNotes.Data;

namespace SyncedNotes.Pages.Account;

[AllowAnonymous]
public class RegisterModel(RegistrationPolicy registration, AppDbContext db) : PageModel
{
    public bool RegistrationOpen { get; private set; }
    public bool FirstUser { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect("/");
        }

        FirstUser = !await db.Users.AnyAsync();
        RegistrationOpen = await registration.IsOpenAsync();
        return Page();
    }
}
