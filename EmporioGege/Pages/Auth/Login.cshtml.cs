using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using EmporioGege.Core.Interfaces;
using EmporioGege.Infrastructure.Auth;
using EmporioGege.Models;

namespace EmporioGege.Pages.Auth
{
    public class LoginModel : PageModel
    {
        private readonly Supabase.Client _supabase;
        private readonly ITenantService _tenantService;
        private readonly LoginTentativaLimiter _tentativaLimiter;

        public LoginModel(Supabase.Client supabase, ITenantService tenantService, LoginTentativaLimiter tentativaLimiter)
        {
            _supabase = supabase;
            _tenantService = tenantService;
            _tentativaLimiter = tentativaLimiter;
        }

        [BindProperty]
        [Required(ErrorMessage = "O e-mail é obrigatório.")]
        [EmailAddress(ErrorMessage = "E-mail inválido.")]
        public string Email { get; set; } = default!;

        [BindProperty]
        [Required(ErrorMessage = "A senha é obrigatória.")]
        [DataType(DataType.Password)]
        public string Senha { get; set; } = default!;

        public string? MensagemErro { get; set; }

        // CORREÇÃO DE UX/ENGENHARIA: Mudado de 'void' para 'IActionResult'
        public IActionResult OnGet()
        {
            // Se o cara já estiver logado e tentar entrar no login, agora ele é ejetado de verdade
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirecionarPorRole(User.FindFirst(ClaimTypes.Role)?.Value);
            }

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
                return Page();

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";
            if (!_tentativaLimiter.PodeTentar(Email, ip))
            {
                // Mensagem idêntica à de credencial inválida - não vazar se o bloqueio é por
                // força bruta ou por senha errada (evita confirmar a existência da conta).
                MensagemErro = "Usuário ou senha inválidos.";
                return Page();
            }

            try
            {
                // 1. Tenta autenticar o usuário no Supabase Auth
                var sessao = await _supabase.Auth.SignIn(Email, Senha);

                if (sessao?.User == null)
                {
                    MensagemErro = "Usuário ou senha inválidos.";
                    return Page();
                }

                // 2. Busca o perfil do usuário na tabela pública para saber a Role e o TenantId
                var resultadoPerfil = await _supabase
                    .From<Profile>()
                    .Where(x => x.Id == sessao.User.Id)
                    .Single();

                if (resultadoPerfil == null)
                {
                    MensagemErro = "Perfil não encontrado no sistema.";
                    return Page();
                }

                if (!resultadoPerfil.Ativo)
                {
                    MensagemErro = "Seu acesso foi desativado pelo administrador da loja.";
                    return Page();
                }

                // 2.5. Bloqueia login se a licença da loja estiver suspensa/cancelada/expirada.
                // Superadmin fica de fora: ele gerencia todas as lojas e não deve ficar preso
                // pela licença de uma loja específica (mesmo tendo tenant_id vinculado nos dados de teste).
                string? tenantNome = null;
                if (resultadoPerfil.Role != "superadmin" && !string.IsNullOrEmpty(resultadoPerfil.TenantId)
                    && Guid.TryParse(resultadoPerfil.TenantId, out var tenantId))
                {
                    var tenant = await _tenantService.ObterAsync(tenantId);
                    if (tenant != null)
                    {
                        tenantNome = tenant.NomeFantasia;
                        MensagemErro = tenant.StatusLicenca switch
                        {
                            "suspenso" => "Acesso suspenso. Entre em contato com o suporte.",
                            "cancelado" => "Acesso cancelado. Entre em contato com o suporte.",
                            // Precisa vir ANTES do "_ when" de expiração abaixo: um tenant recém-
                            // cadastrado via autocadastro (CadastroLojaService) tem DataExpiracao
                            // seedada com a data de criação, que cairia como "expirada" já no dia
                            // seguinte se essa checagem não tivesse prioridade sobre aquela.
                            "pendente" => "Cadastro em análise: estamos aguardando a confirmação do primeiro pagamento. Você receberá um e-mail assim que for confirmado.",
                            // Trial vencido, mas o job (TrialExpiradoProcessor, roda de hora em
                            // hora) ainda não converteu pra "pendente" - mensagem própria em vez
                            // de cair no "Licença expirada" genérico, já que aqui é esperado (não
                            // indica problema) e se resolve sozinho em minutos.
                            "trial" when tenant.DataExpiracao < DateTime.UtcNow.AddHours(-24) =>
                                "Não conseguimos gerar sua cobrança automaticamente. Fale com o suporte pelo WhatsApp (15) 98163-6193 para liberar seu acesso.",
                            "trial" when tenant.DataExpiracao.Date < DateTime.UtcNow.Date =>
                                "Seu período de teste terminou. Estamos gerando sua cobrança - você recebe um e-mail com o link de pagamento em instantes. Tente novamente em alguns minutos.",
                            "trial" => null, // Dentro do período de teste: acesso liberado, sem cobrança.
                            // Loja com assinatura no Asaas: quem manda é o status (o webhook suspende no
                            // atraso). A data só vale pra licença manual, senão a loja em dia era
                            // bloqueada entre o fim do "vencimento + 5 dias" e a cobrança seguinte.
                            _ when tenant.AsaasSubscriptionId is null && tenant.DataExpiracao.Date < DateTime.UtcNow.Date =>
                                "Licença expirada. Entre em contato com o suporte.",
                            _ => null
                        };

                        if (MensagemErro != null)
                            return Page();
                    }
                }

                // 3. Cria as credenciais (Claims) para salvar no Cookie do navegador de forma segura
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, resultadoPerfil.Id),
                    new Claim(ClaimTypes.Name, resultadoPerfil.Nome),
                    new Claim(ClaimTypes.Role, resultadoPerfil.Role),
                    new Claim("TenantId", resultadoPerfil.TenantId ?? ""),
                    new Claim("TenantNome", tenantNome ?? "")
                };

                var identidade = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identidade);

                // 4. Salva o cookie HttpOnly trancado no navegador do usuário (Blindagem contra XSS)
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
                });

                // 5. Redireciona o cara para o painel correto dele
                return RedirecionarPorRole(resultadoPerfil.Role);
            }
            catch (Exception)
            {
                // Cybersecurity: Mensagem genérica para não vazar detalhes do banco em caso de pane
                MensagemErro = "Erro ao tentar realizar o login. Verifique os dados ou a conexão.";
                return Page();
            }
        }

        private IActionResult RedirecionarPorRole(string? role)
        {
            // O .NET vai ignorar se a pasta é maiúscula ou minúscula na rota, vai achar o "/SuperAdmin/Index" de boa!
            return role switch
            {
                "superadmin" => RedirectToPage("/SuperAdmin/Index"),
                "administrador" => RedirectToPage("/Admin/Index"),
                _ => RedirectToPage("/Caixa/Index")
            };
        }
    }
}