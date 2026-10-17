using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Pages.Admin.Estoque
{
    [Authorize(Policy = "AdminOnly")]
    public class EntradaModel(IEstoqueMovimentacaoService movimentacaoService, IProdutoService produtoService) : PageModel
    {
        private Guid UsuarioId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        [BindProperty]
        [Required(ErrorMessage = "Selecione o produto.")]
        public Guid? ProdutoId { get; set; }

        [BindProperty]
        [Range(1, int.MaxValue, ErrorMessage = "Quantidade precisa ser pelo menos 1.")]
        public int Quantidade { get; set; } = 1;

        [BindProperty]
        public string? Justificativa { get; set; }

        [BindProperty]
        [Range(0, 1_000_000, ErrorMessage = "Custo unitário inválido.")]
        public decimal? CustoUnitario { get; set; }

        [TempData]
        public string? MensagemErro { get; set; }

        public IReadOnlyList<ProdutoDetalheDto> Produtos { get; private set; } = [];

        public async Task OnGetAsync(CancellationToken ct)
        {
            Produtos = await produtoService.ListarAsync(ct);
        }

        public async Task<IActionResult> OnPostAsync(CancellationToken ct)
        {
            if (!ModelState.IsValid)
            {
                Produtos = await produtoService.ListarAsync(ct);
                return Page();
            }

            try
            {
                await movimentacaoService.RegistrarEntradaAsync(ProdutoId!.Value, Quantidade, Justificativa, UsuarioId, CustoUnitario, ct);
            }
            catch (InvalidOperationException ex)
            {
                MensagemErro = ex.Message;
                return RedirectToPage("Index");
            }

            return RedirectToPage("Historico");
        }
    }
}
