using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EmporioGege.Pages.Auth
{
    // Destino do link de recuperação de senha. O token de acesso chega no fragmento da URL, o
    // auth-retorno.js copia pro campo oculto e aqui ele é usado uma vez pra trocar a senha direto
    // na API do Supabase Auth (PUT /auth/v1/user) - sem abrir sessão no app.
    public class NovaSenhaModel(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<NovaSenhaModel> logger) : PageModel
    {
        [BindProperty]
        public string? AccessToken { get; set; }

        [BindProperty]
        [Required(ErrorMessage = "A senha é obrigatória.")]
        [MinLength(6, ErrorMessage = "A senha precisa ter pelo menos 6 caracteres.")]
        [DataType(DataType.Password)]
        public string Senha { get; set; } = default!;

        [BindProperty]
        [Compare(nameof(Senha), ErrorMessage = "As senhas não coincidem.")]
        [DataType(DataType.Password)]
        public string ConfirmarSenha { get; set; } = default!;

        public bool Postado { get; set; }
        public bool Concluido { get; set; }
        public string? MensagemErro { get; set; }

        public void OnGet() { }

        public async Task<IActionResult> OnPostAsync(CancellationToken ct)
        {
            Postado = true;

            if (string.IsNullOrWhiteSpace(AccessToken))
            {
                MensagemErro = "Link de recuperação inválido ou expirado. Peça um novo link.";
                return Page();
            }

            if (!ModelState.IsValid)
                return Page();

            try
            {
                var cliente = httpClientFactory.CreateClient();
                using var requisicao = new HttpRequestMessage(HttpMethod.Put, $"{configuration["Supabase:Url"]!.TrimEnd('/')}/auth/v1/user")
                {
                    Content = JsonContent.Create(new { password = Senha })
                };
                requisicao.Headers.Add("apikey", configuration["Supabase:Key"]);
                requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

                using var resposta = await cliente.SendAsync(requisicao, ct);
                if (resposta.IsSuccessStatusCode)
                {
                    Concluido = true;
                    return Page();
                }

                var conteudo = await resposta.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Supabase recusou a troca de senha: {Status} {Conteudo}", resposta.StatusCode, conteudo);

                MensagemErro = resposta.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                        => "Este link expirou. Peça um novo link de recuperação.",
                    _ when conteudo.Contains("same_password") || conteudo.Contains("different from the old")
                        => "A nova senha precisa ser diferente da atual.",
                    _ when conteudo.Contains("weak_password")
                        => "Senha fraca demais. Use uma senha mais longa, misturando letras e números.",
                    _ => "Não conseguimos trocar sua senha agora. Tente de novo ou peça um novo link."
                };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao chamar o Supabase Auth pra trocar a senha.");
                MensagemErro = "Não conseguimos trocar sua senha agora. Tente de novo em instantes.";
            }

            return Page();
        }
    }
}
