using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Pages.Admin.CardapioOnline
{
    [Authorize(Policy = "AdminOnly")]
    public class IndexModel(ICardapioOnlineService cardapioService) : PageModel
    {
        public const int DiasPedidos = 30;

        [BindProperty]
        public string? Slug { get; set; }

        [BindProperty]
        public string? WhatsApp { get; set; }

        [BindProperty]
        public string? CallbackUrl { get; set; }

        public ConfiguracaoCardapioDto Configuracao { get; private set; } = default!;

        public IReadOnlyList<PedidoOnlineResumoDto> Pedidos { get; private set; } = [];

        // Só preenchido na resposta do "Gerar credenciais": não fica salvo em cookie/TempData.
        public CredenciaisBotDto? CredenciaisNovas { get; private set; }

        public string? LinkPublico => Configuracao.Slug is null ? null : $"{Request.Scheme}://{Request.Host}/c/{Configuracao.Slug}";

        public string LinkApi => $"{Request.Scheme}://{Request.Host}";

        [TempData]
        public string? MensagemSucesso { get; set; }

        [TempData]
        public string? MensagemErro { get; set; }

        public async Task OnGetAsync(CancellationToken ct) => await CarregarAsync(ct);

        public async Task<IActionResult> OnPostSalvarAsync(CancellationToken ct)
        {
            try
            {
                await cardapioService.SalvarConfiguracaoAsync(Slug, WhatsApp, CallbackUrl, ct);
                MensagemSucesso = "Configuração salva.";
            }
            catch (ArgumentException ex)
            {
                MensagemErro = ex.Message;
            }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostGerarCredenciaisAsync(CancellationToken ct)
        {
            CredenciaisNovas = await cardapioService.GerarCredenciaisAsync(ct);
            await CarregarAsync(ct);
            return Page();
        }

        private async Task CarregarAsync(CancellationToken ct)
        {
            Configuracao = await cardapioService.ObterConfiguracaoAsync(ct);
            Pedidos = await cardapioService.ListarPedidosAsync(DiasPedidos, ct);
            Slug ??= Configuracao.Slug;
            WhatsApp ??= Configuracao.WhatsApp;
            CallbackUrl ??= Configuracao.CallbackUrl;
        }
    }
}
