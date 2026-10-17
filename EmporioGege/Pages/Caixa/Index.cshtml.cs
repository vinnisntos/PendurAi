using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Enums;
using EmporioGege.Core.Exceptions;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Pages.Caixa
{
    [Authorize(Policy = "CaixaOnly")]
    public class IndexModel(
        ICatalogoService catalogoService, IClienteService clienteService, IVendaService vendaService,
        ITurnoService turnoService, ITenantProvider tenantProvider, ITenantService tenantService,
        IImpressoraReciboService impressoraReciboService) : PageModel
    {
        public List<ProdutoCatalogoDto> Produtos { get; set; } = [];

        public List<PrecoProdutoDto> PrecosDiferenciados { get; set; } = [];

        public List<ClienteDto> Clientes { get; set; } = [];

        public bool SemTenant { get; set; }

        private Guid UsuarioId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        public async Task OnGetAsync(CancellationToken ct)
        {
            if (tenantProvider.TenantId is null)
            {
                // Ex.: superadmin acessando o PDV fora do contexto de uma loja específica.
                SemTenant = true;
                return;
            }

            // Via Dapper (não pelo cliente Supabase/Postgrest): o Supabase.Client é Scoped e
            // não persiste sessão entre requisições, então qualquer leitura por ele fora do
            // request de login rodaria como anon — o catálogo sempre voltaria vazio.
            Produtos = (await catalogoService.ListarProdutosAsync(ct)).ToList();
            PrecosDiferenciados = (await catalogoService.ListarPrecosDiferenciadosAsync(ct)).ToList();
            Clientes = (await clienteService.ListarAsync(ct)).ToList();
        }

        public async Task<JsonResult> OnPostProcessarVendaAsync([FromBody] ProcessarVendaRequest? request, CancellationToken ct)
        {
            if (request?.Itens is null || request.Itens.Count == 0)
            {
                return new JsonResult(new { sucesso = false, mensagem = "Nenhum item na venda." })
                { StatusCode = StatusCodes.Status400BadRequest };
            }

            var itens = new List<ItemVendaDto>();
            foreach (var item in request.Itens)
            {
                if (!Guid.TryParse(item.ProdutoId, out var produtoId) || item.Quantidade <= 0)
                {
                    return new JsonResult(new { sucesso = false, mensagem = $"Item inválido na venda: produto {item.ProdutoId}." })
                    { StatusCode = StatusCodes.Status400BadRequest };
                }

                // Preço não vem do cliente (só o tipo escolhido) — VendaService resolve o
                // valor de verdade a partir de precos_produto/preco_venda_base no servidor.
                var tipoPreco = item.TipoPreco?.ToUpperInvariant() switch
                {
                    "CAIXA" => TipoPreco.Caixa,
                    "ATACADO" => TipoPreco.Atacado,
                    _ => TipoPreco.Balcao
                };

                itens.Add(new ItemVendaDto(produtoId, item.Quantidade, tipoPreco));
            }

            Guid? clienteId = null;
            if (string.Equals(request.MetodoPagamento, "FIADO", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(request.ClienteId) || !Guid.TryParse(request.ClienteId, out var clienteIdParseado))
                {
                    return new JsonResult(new { sucesso = false, mensagem = "Selecione um cliente para venda fiado." })
                    { StatusCode = StatusCodes.Status400BadRequest };
                }
                clienteId = clienteIdParseado;
            }

            var turnoAberto = await turnoService.ObterTurnoAbertoAsync(UsuarioId, ct);
            if (turnoAberto is null)
            {
                return new JsonResult(new { sucesso = false, mensagem = "Abra o turno de caixa antes de registrar vendas." })
                { StatusCode = StatusCodes.Status409Conflict };
            }

            try
            {
                var resultado = await vendaService.FinalizarVendaAsync(
                    new FinalizarVendaDto(turnoAberto.Id, itens, TipoOrigemVenda.Balcao, request.EmitirNotaFiscal, request.MetodoPagamento, clienteId), ct);

                var reciboImpresso = await TentarImprimirReciboAsync(resultado, request.MetodoPagamento, ct);

                return new JsonResult(new
                {
                    sucesso = true,
                    mensagem = "Venda processada com sucesso.",
                    vendaId = resultado.VendaId,
                    totalVenda = resultado.TotalVenda,
                    emitirNotaFiscal = request.EmitirNotaFiscal,
                    metodoPagamento = request.MetodoPagamento,
                    reciboImpresso
                });
            }
            catch (EstoqueInsuficienteException ex)
            {
                // Venda é atômica (VendaService): nenhum item deste carrinho teve baixa
                // de estoque confirmada — a transação inteira reverte junto.
                return new JsonResult(new
                {
                    sucesso = false,
                    mensagem = $"Estoque insuficiente para o produto {ex.ProdutoId}: disponível {ex.QuantidadeDisponivel}, solicitado {ex.QuantidadeSolicitada}.",
                    produtoId = ex.ProdutoId
                })
                { StatusCode = StatusCodes.Status409Conflict };
            }
            catch (LimiteCreditoExcedidoException ex)
            {
                // ex.Message já vem formatado (pt-BR) do próprio construtor da exceção.
                return new JsonResult(new { sucesso = false, mensagem = ex.Message, clienteId = ex.ClienteId })
                { StatusCode = StatusCodes.Status409Conflict };
            }
            catch (TurnoInvalidoException ex)
            {
                return new JsonResult(new { sucesso = false, mensagem = ex.Message }) { StatusCode = StatusCodes.Status409Conflict };
            }
            catch (ArgumentException ex)
            {
                return new JsonResult(new { sucesso = false, mensagem = ex.Message.Split(" (Parameter")[0] }) { StatusCode = StatusCodes.Status400BadRequest };
            }
        }

        // Impressão nunca bloqueia a venda: já está tudo gravado no banco antes daqui.
        // Nome do produto vem de uma releitura do catálogo (resultado.Itens só tem o
        // ProdutoId + preço já aplicado de verdade na transação, que é o que importa).
        private async Task<bool> TentarImprimirReciboAsync(ResultadoVendaDto resultado, string metodoPagamento, CancellationToken ct)
        {
            var catalogo = await catalogoService.ListarProdutosAsync(ct);
            var nomesPorId = catalogo.ToDictionary(p => p.Id, p => p.Nome);

            var itensRecibo = resultado.Itens
                .Select(i => new ItemReciboDto(
                    nomesPorId.GetValueOrDefault(i.ProdutoId, "Produto"), i.Quantidade, i.PrecoUnitarioAplicado, i.Subtotal))
                .ToList();

            var loja = await tenantService.ObterAsync(tenantProvider.RequireTenantId(), ct);

            var recibo = new ReciboVendaDto(
                loja?.NomeFantasia ?? "Empório Gege", resultado.VendaId, DateTime.UtcNow, itensRecibo, resultado.TotalVenda, metodoPagamento);

            return await impressoraReciboService.ImprimirAsync(recibo, ct);
        }

        public record ItemVendaRequest(string ProdutoId, int Quantidade, string? TipoPreco = null);

        public record ProcessarVendaRequest(List<ItemVendaRequest> Itens, bool EmitirNotaFiscal, string MetodoPagamento, string? ClienteId = null);
    }
}
