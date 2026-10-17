using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Pages.Admin.Clientes
{
    [Authorize(Policy = "AdminOnly")]
    public class EditarModel(IClienteService clienteService) : PageModel
    {
        [BindProperty(SupportsGet = true)]
        public Guid? Id { get; set; }

        [BindProperty]
        [Required(ErrorMessage = "Informe o nome do cliente.")]
        public string Nome { get; set; } = default!;

        [BindProperty]
        public string? Telefone { get; set; }

        [BindProperty]
        [Required(ErrorMessage = "Informe o CPF ou RG do cliente.")]
        public string CpfRg { get; set; } = default!;

        [BindProperty]
        [Range(0, double.MaxValue, ErrorMessage = "Limite de crédito não pode ser negativo.")]
        public decimal LimiteCredito { get; set; }

        public decimal SaldoDevedorAtual { get; private set; }

        public IReadOnlyList<PagamentoFiadoDto> Pagamentos { get; private set; } = [];

        [TempData]
        public string? MensagemErro { get; set; }

        public async Task<IActionResult> OnGetAsync(CancellationToken ct)
        {
            if (Id is null)
                return Page();

            var cliente = await clienteService.ObterAsync(Id.Value, ct);
            if (cliente is null)
            {
                MensagemErro = "Cliente não encontrado.";
                return RedirectToPage("Index");
            }

            Nome = cliente.Nome;
            Telefone = cliente.Telefone;
            CpfRg = cliente.CpfRg ?? "";
            LimiteCredito = cliente.LimiteCredito;
            SaldoDevedorAtual = cliente.SaldoDevedor;
            Pagamentos = await clienteService.ListarPagamentosAsync(Id.Value, ct);

            return Page();
        }

        public async Task<IActionResult> OnPostAsync(CancellationToken ct)
        {
            if (!ModelState.IsValid)
                return Page();

            await clienteService.SalvarAsync(new SalvarClienteDto(Id, Nome, Telefone, CpfRg, LimiteCredito), ct);

            return RedirectToPage("Index");
        }
    }
}
