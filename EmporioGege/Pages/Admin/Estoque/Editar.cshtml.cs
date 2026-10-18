using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using EmporioGege.Application.DTOs;
using EmporioGege.Application.Services;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Pages.Admin.Estoque
{
    [Authorize(Policy = "AdminOnly")]
    public class EditarModel(IProdutoService produtoService, ICardapioOnlineService cardapioService) : PageModel
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

        // Cardápio online (bot de WhatsApp + página pública) - salvo à parte, depois do produto.
        [BindProperty]
        public bool ExibirCardapio { get; set; }

        [BindProperty]
        [StringLength(300, ErrorMessage = "A descrição pode ter no máximo 300 caracteres.")]
        public string? Descricao { get; set; }

        [BindProperty]
        public IFormFile? Foto { get; set; }

        [BindProperty]
        public bool RemoverFoto { get; set; }

        public string? FotoUrl { get; set; }

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

            var cardapio = await cardapioService.ObterProdutoAsync(Id.Value, ct);
            ExibirCardapio = cardapio?.ExibirCardapio ?? false;
            Descricao = cardapio?.Descricao;
            FotoUrl = cardapio?.FotoUrl;

            return Page();
        }

        public async Task<IActionResult> OnPostAsync(CancellationToken ct)
        {
            if (!ModelState.IsValid)
                return Page();

            var dto = new SalvarProdutoDto(
                Id, Nome, CodigoBarras, CustoMedio, PrecoVendaBase, EstoqueAtual, EstoqueMinimo,
                UnidadeMedida, QuantidadePorCaixa, DataValidade, PrecoCaixa, PrecoAtacado, CodigoNcm, Cfop, PermiteBaixaManual, EstoqueOriginal);

            if (Foto is { Length: > CardapioOnlineService.TamanhoMaximoFoto })
            {
                MensagemErro = "A foto pode ter no máximo 2 MB.";
                return Page();
            }

            Guid produtoId;
            try
            {
                produtoId = await produtoService.SalvarAsync(dto, UsuarioId, ct);
            }
            catch (InvalidOperationException ex)
            {
                MensagemErro = ex.Message;
                return Page();
            }

            byte[]? foto = null;
            if (Foto is { Length: > 0 })
            {
                using var buffer = new MemoryStream();
                await Foto.CopyToAsync(buffer, ct);
                foto = buffer.ToArray();
            }

            try
            {
                await cardapioService.SalvarProdutoAsync(produtoId, ExibirCardapio, Descricao, foto, RemoverFoto, ct);
            }
            catch (ArgumentException ex)
            {
                // O produto já foi salvo; só o cardápio falhou - volta pra edição dele com o motivo.
                MensagemErro = $"Produto salvo, mas o cardápio não: {ex.Message}";
                return RedirectToPage("Editar", new { id = produtoId });
            }

            return RedirectToPage("Index");
        }
    }
}
