using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EmporioGege.Pages.Auth
{
    public class LogoutModel : PageModel
    {
        // POST com antiforgery: um link/imagem de outro site não consegue deslogar o caixa no meio do turno.
        public async Task<IActionResult> OnPostAsync()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToPage("/Auth/Login");
        }

        public IActionResult OnGet() => RedirectToPage("/Auth/Login");
    }
}
