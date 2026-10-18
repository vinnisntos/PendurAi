using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Pages.Cardapio
{
    // Página pública (sem login): o cardápio da loja e o botão que abre o atendimento no WhatsApp.
    public class IndexModel(ICardapioOnlineService cardapioService) : PageModel
    {
        // O bot de WhatsApp só entra na conversa quando a mensagem contém "/comprar".
        public const string MensagemPedido = "Quero fazer um pedido 🍰 (/comprar)";

        public CardapioPublicoDto Cardapio { get; private set; } = default!;

        public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
        {
            var cardapio = await cardapioService.ObterCardapioPublicoAsync(slug, ct);
            if (cardapio is null)
                return NotFound();

            Cardapio = cardapio;
            return Page();
        }

        public string? LinkWhatsApp(string? produto = null)
        {
            if (Cardapio.WhatsApp is null)
                return null;
            var texto = produto is null ? MensagemPedido : $"{MensagemPedido} - {produto}";
            return $"https://wa.me/{Cardapio.WhatsApp}?text={Uri.EscapeDataString(texto)}";
        }
    }
}
