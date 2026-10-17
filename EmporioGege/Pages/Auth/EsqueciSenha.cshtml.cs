using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EmporioGege.Pages.Auth
{
    public class EsqueciSenhaModel(Supabase.Client supabase, ILogger<EsqueciSenhaModel> logger) : PageModel
    {
        [BindProperty]
        [Required(ErrorMessage = "O e-mail é obrigatório.")]
        [EmailAddress(ErrorMessage = "E-mail inválido.")]
        public string Email { get; set; } = default!;

        public bool Enviado { get; set; }

        public void OnGet() { }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
                return Page();

            try
            {
                // O link do e-mail volta pra /Auth/NovaSenha (precisa estar nas Redirect URLs do
                // painel do Supabase). O próprio Supabase limita a frequência de envio por e-mail.
                var redirect = $"{Request.Scheme}://{Request.Host}/Auth/NovaSenha";
                await supabase.Auth.ResetPasswordForEmail(new Supabase.Gotrue.ResetPasswordForEmailOptions(Email) { RedirectTo = redirect });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao solicitar recuperação de senha.");
            }

            // Resposta igual exista ou não a conta (não confirmar quais e-mails estão cadastrados).
            Enviado = true;
            return Page();
        }
    }
}
