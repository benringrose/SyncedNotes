using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SyncedNotes.Auth;
using SyncedNotes.Data;
using SyncedNotes.Infrastructure;

namespace SyncedNotes.Pages.Account;

[AllowAnonymous]
public class LoginModel(RegistrationPolicy registration, AppDbContext db) : PageModel
{
    public bool RegistrationOpen { get; private set; }
    public string ReturnUrl { get; private set; } = "/";
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl, bool signedOut = false, bool deleted = false)
    {
        ReturnUrl = HttpRequestExtensions.SafeLocalUrl(returnUrl);
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(ReturnUrl);
        }

        // First run: there is nobody to sign in yet.
        if (!await db.Users.AnyAsync())
        {
            return RedirectToPage("/Account/Register");
        }

        RegistrationOpen = await registration.IsOpenAsync();
        Message = signedOut ? "You've been signed out." : deleted ? "Your account and notes have been deleted." : null;
        return Page();
    }
}
