using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;
using EmporioGege.Infrastructure.Auth;

namespace EmporioGege.Pages
{
    public class CadastroLojaModel(ICadastroLojaService cadastroLojaService, CadastroLojaTentativaLimiter tentativaLimiter) : PageModel
    {
        [BindProperty(SupportsGet = true)]
        public string? Plano { get; set; }

        [BindProperty]
        [Required(ErrorMessage = "Informe o nome da loja.")]
        [StringLength(120, ErrorMessage = "Nome muito longo.")]
        public string NomeFantasia { get; set; } = default!;

        [BindProperty]
        [Required(ErrorMessage = "Informe seu nome completo.")]
        [StringLength(120, ErrorMessage = "Nome muito longo.")]
        public string NomeRepresentante { get; set; } = default!;

        [BindProperty]
        [Required(ErrorMessage = "Informe seu CPF ou RG.")]
        public string CpfRgDono { get; set; } = default!;

        [BindProperty]
        [Required(ErrorMessage = "Informe o CNPJ da loja.")]
        public string Cnpj { get; set; } = default!;

        [BindProperty]
        [Required(ErrorMessage = "Informe cidade e estado.")]
        public string CidadeEstado { get; set; } = default!;

        [BindProperty]
        [Required(ErrorMessage = "Informe seu telefone.")]
        public string TelefoneDono { get; set; } = default!;

        [BindProperty]
        [Required(ErrorMessage = "O e-mail é obrigatório.")]
        [EmailAddress(ErrorMessage = "E-mail inválido.")]
        public string EmailDono { get; set; } = default!;

        [BindProperty]
        [Required(ErrorMessage = "A senha é obrigatória.")]
        [MinLength(6, ErrorMessage = "A senha precisa ter pelo menos 6 caracteres.")]
        [DataType(DataType.Password)]
        public string Senha { get; set; } = default!;

        [BindProperty]
        [Compare(nameof(Senha), ErrorMessage = "As senhas não coincidem.")]
        [DataType(DataType.Password)]
        public string ConfirmarSenha { get; set; } = default!;

        [BindProperty]
        [Required(ErrorMessage = "Escolha a forma de pagamento.")]
        [RegularExpression("^(PIX|BOLETO|CREDIT_CARD)$", ErrorMessage = "Escolha uma forma de pagamento válida.")]
        public string TipoCobranca { get; set; } = "PIX";

        // Anti-robô sem serviço externo: campo invisível que só robô preenche, e tempo mínimo
        // entre abrir e enviar o formulário.
        [BindProperty]
        public string? Website { get; set; }

        [BindProperty]
        public long AbertoEm { get; set; }

        public string? MensagemErro { get; set; }

        [TempData]
        public string? MensagemSucesso { get; set; }

        public bool ExibirSucesso { get; set; }

        public void OnGet(bool sucesso = false)
        {
            ExibirSucesso = sucesso;
            AbertoEm = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (Plano is not ("start" or "pro" or "enterprise"))
                Plano = "pro";
        }

        public async Task<IActionResult> OnPostAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(Website) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - AbertoEm < 4)
            {
                MensagemErro = "Não foi possível enviar o cadastro. Confira os dados e tente de novo.";
                AbertoEm = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return Page();
            }

            if (Plano is not ("start" or "pro" or "enterprise"))
                ModelState.AddModelError(nameof(Plano), "Escolha um plano válido.");

            // O Asaas recusa CNPJ com dígito errado, e o trial ficava preso sem conseguir gerar cobrança.
            if (!string.IsNullOrEmpty(Cnpj) && !EmporioGege.Core.Common.Cnpj.EhValido(Cnpj))
                ModelState.AddModelError(nameof(Cnpj), "CNPJ inválido. Confira os números.");

            if (!ModelState.IsValid)
                return Page();

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";
            if (!tentativaLimiter.PodeTentar(EmailDono, ip))
            {
                MensagemErro = "Muitas tentativas de cadastro em pouco tempo. Tente novamente mais tarde ou entre em contato com o suporte.";
                return Page();
            }

            var resultado = await cadastroLojaService.CadastrarAsync(new CadastrarLojaDto(
                NomeFantasia, NomeRepresentante, CpfRgDono, Cnpj, CidadeEstado, TelefoneDono, EmailDono, Senha, Plano!, TipoCobranca), ct);

            if (!resultado.Sucesso)
            {
                MensagemErro = resultado.Mensagem;
                return Page();
            }

            MensagemSucesso = resultado.Mensagem;
            return RedirectToPage(new { sucesso = true });
        }
    }
}
