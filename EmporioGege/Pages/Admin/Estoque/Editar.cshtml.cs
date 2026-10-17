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
    public class EditarModel(IProdutoService produtoService) : PageModel
    {
        private Guid UsuarioId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        [BindProperty(SupportsGet = true)]
        public Guid? Id { get; set; }

        [BindProperty]
        [Required(ErrorMessage = "Informe o nome do produto.")]
        public string Nome { get; set; } = default!;

        [BindProperty]
        public string? CodigoBarras { get; set; }

        [BindProperty]
        [Range(0, double.MaxValue, ErrorMessage = "Custo não pode ser negativo.")]
        public decimal CustoMedio { get; set; }

        [BindProperty]
        [Range(0.01, double.MaxValue, ErrorMessage = "Informe um preço de venda maior que zero.")]
        public decimal PrecoVendaBase { get; set; }

        [BindProperty]
        [Range(0, int.MaxValue, ErrorMessage = "Estoque não pode ser negativo.")]
        public int EstoqueAtual { get; set; }

        [BindProperty]
        public int? EstoqueOriginal { get; set; }

        [BindProperty]
        [Range(0, int.MaxValue, ErrorMessage = "Estoque mínimo não pode ser negativo.")]
        public int EstoqueMinimo { get; set; } = 5;

        [BindProperty]
        [Required]
        public string UnidadeMedida { get; set; } = "Un";

        [BindProperty]
        [Range(1, int.MaxValue, ErrorMessage = "Quantidade por caixa precisa ser pelo menos 1.")]
        public int QuantidadePorCaixa { get; set; } = 1;

        [BindProperty]
        [DataType(DataType.Date)]
        public DateOnly? DataValidade { get; set; }

        [BindProperty]
        [Range(0, double.MaxValue, ErrorMessage = "Preço da caixa não pode ser negativo.")]
        public decimal? PrecoCaixa { get; set; }

        [BindProperty]
        [Range(0, double.MaxValue, ErrorMessage = "Preço de atacado não pode ser negativo.")]
        public decimal? PrecoAtacado { get; set; }

        // Opcionais de propósito: classificação fiscal real (varia por produto), exigida pela
        // Focus NFe na emissão de NFC-e, mas não algo que a aplicação possa preencher sozinha -
        // fica em branco até o dono da loja/contador preencher (ver README.txt, migration 0011).
        [BindProperty]
        public string? CodigoNcm { get; set; }

        [BindProperty]
        public string? Cfop { get; set; }

        [BindProperty]
        public bool PermiteBaixaManual { get; set; }

        [TempData]
        public string? MensagemErro { get; set; }

        public async Task<IActionResult> OnGetAsync(CancellationToken ct)
        {
            if (Id is null)
                return Page();

            var produto = await produtoService.ObterAsync(Id.Value, ct);
            if (produto is null)
            {
                MensagemErro = "Produto não encontrado.";
                return RedirectToPage("Index");
            }

            Nome = produto.Nome;
            CodigoBarras = produto.CodigoBarras;
            CustoMedio = produto.CustoMedio;
            PrecoVendaBase = produto.PrecoVendaBase;
            EstoqueAtual = produto.EstoqueAtual;
            EstoqueOriginal = produto.EstoqueAtual;
            EstoqueMinimo = produto.EstoqueMinimo;
            UnidadeMedida = produto.UnidadeMedida;
            QuantidadePorCaixa = produto.QuantidadePorCaixa;
            DataValidade = produto.DataValidade;
            PrecoCaixa = produto.PrecoCaixa;
            PrecoAtacado = produto.PrecoAtacado;
            CodigoNcm = produto.CodigoNcm;
            Cfop = produto.Cfop;
            PermiteBaixaManual = produto.PermiteBaixaManual;

            return Page();
        }

        public async Task<IActionResult> OnPostAsync(CancellationToken ct)
        {
            if (!ModelState.IsValid)
                return Page();

            var dto = new SalvarProdutoDto(
                Id, Nome, CodigoBarras, CustoMedio, PrecoVendaBase, EstoqueAtual, EstoqueMinimo,
                UnidadeMedida, QuantidadePorCaixa, DataValidade, PrecoCaixa, PrecoAtacado, CodigoNcm, Cfop, PermiteBaixaManual, EstoqueOriginal);

            try
            {
                await produtoService.SalvarAsync(dto, UsuarioId, ct);
            }
            catch (InvalidOperationException ex)
            {
                MensagemErro = ex.Message;
                return Page();
            }

            return RedirectToPage("Index");
        }
    }
}
