using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Pages.Caixa.Recibo
{
    // Recibo impresso pelo navegador do caixa (impressora térmica instalada no PC da loja).
    // A impressão ESC/POS abre a porta serial do SERVIDOR, que hospedado na nuvem nunca chega à loja.
    public class IndexModel(IVendaService vendaService) : PageModel
    {
        public VendaDetalheDto Venda { get; private set; } = default!;

        public string NomeLoja => User.FindFirst("ImpersonatedTenantNome")?.Value is { Length: > 0 } impersonada
            ? impersonada
            : User.FindFirst("TenantNome")?.Value ?? "PendurAi";

        public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
        {
            var venda = await vendaService.ObterDetalheAsync(id, ct);
            if (venda is null)
                return NotFound();

            Venda = venda;
            return Page();
        }
    }
}
