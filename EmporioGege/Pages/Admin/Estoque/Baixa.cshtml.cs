using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Exceptions;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Pages.Admin.Estoque
{
    [Authorize(Policy = "AdminOnly")]
    public class BaixaModel(IEstoqueMovimentacaoService movimentacaoService, IProdutoService produtoService) : PageModel
    {
        private Guid UsuarioId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        [BindProperty]
        [Required(ErrorMessage = "Selecione o produto.")]
        public Guid? ProdutoId { get; set; }

        [BindProperty]
        [Range(1, int.MaxValue, ErrorMessage = "Quantidade precisa ser pelo menos 1.")]
        public int Quantidade { get; set; } = 1;

        [BindProperty]
        [Required(ErrorMessage = "Selecione o motivo da baixa.")]
        public string? Motivo { get; set; }

        [BindProperty]
        [Required(ErrorMessage = "Informe uma justificativa para a baixa.")]
        public string? Justificativa { get; set; }

        [TempData]
        public string? MensagemErro { get; set; }

        // Só produtos explicitamente habilitados (permite_baixa_manual) aparecem aqui -
        // restrição de negócio pra não deixar dar baixa em qualquer produto (ex. balas/doces).
        public IReadOnlyList<ProdutoDetalheDto> Produtos { get; private set; } = [];

        public async Task OnGetAsync(CancellationToken ct)
        {
            Produtos = await CarregarProdutosElegiveisAsync(ct);
        }

        public async Task<IActionResult> OnPostAsync(CancellationToken ct)
        {
            if (!ModelState.IsValid)
            {
                Produtos = await CarregarProdutosElegiveisAsync(ct);
                return Page();
            }

            try
            {
                await movimentacaoService.RegistrarBaixaManualAsync(ProdutoId!.Value, Quantidade, Motivo!, Justificativa!, UsuarioId, ct);
            }
            catch (EstoqueInsuficienteException ex)
            {
                MensagemErro = $"Estoque insuficiente: disponível {ex.QuantidadeDisponivel}, solicitado {ex.QuantidadeSolicitada}.";
                return RedirectToPage("Index");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                MensagemErro = ex.Message.Split(" (Parameter")[0];
                return RedirectToPage("Index");
            }

            return RedirectToPage("Historico");
        }

        private async Task<IReadOnlyList<ProdutoDetalheDto>> CarregarProdutosElegiveisAsync(CancellationToken ct)
        {
            var produtos = await produtoService.ListarAsync(ct);
            return produtos.Where(p => p.PermiteBaixaManual).ToList();
        }
    }
}
