using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Pages.Admin.Clientes
{
    [Authorize(Policy = "AdminOnly")]
    public class IndexModel(IClienteService clienteService) : PageModel
    {
        public IReadOnlyList<ClienteDto> Clientes { get; private set; } = [];

        [TempData]
        public string? MensagemSucesso { get; set; }

        [TempData]
        public string? MensagemErro { get; set; }

        public async Task OnGetAsync(CancellationToken ct)
        {
            Clientes = await clienteService.ListarAsync(ct);
        }

        public async Task<IActionResult> OnPostRegistrarPagamentoAsync(Guid id, decimal valor, CancellationToken ct)
        {
            if (valor <= 0)
            {
                MensagemErro = "Informe um valor de pagamento maior que zero.";
                return RedirectToPage();
            }

            try
            {
                await clienteService.RegistrarPagamentoAsync(id, valor, ct);
            }
            catch (InvalidOperationException ex)
            {
                MensagemErro = ex.Message;
                return RedirectToPage();
            }
            MensagemSucesso = "Pagamento registrado.";
            return RedirectToPage();
        }
    }
}
