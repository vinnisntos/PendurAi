using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Enums;
using EmporioGege.Core.Exceptions;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Pages.Caixa.Comandas
{
    [Authorize(Policy = "CaixaOnly")]
    public class IndexModel(
        IComandaService comandaService,
        ICatalogoService catalogoService,
        IClienteService clienteService,
        ITurnoService turnoService,
        ISupervisorAutorizacaoService supervisorAutorizacaoService,
        ITenantProvider tenantProvider,
        ITenantService tenantService,
        IImpressoraReciboService impressoraReciboService) : PageModel
    {
        public List<ProdutoCatalogoDto> Produtos { get; set; } = [];

        public List<PrecoProdutoDto> PrecosDiferenciados { get; set; } = [];

        public List<ClienteDto> Clientes { get; set; } = [];

        public List<ComandaResumoDto> ComandasAbertas { get; set; } = [];

        public bool SemTenant { get; set; }

        private Guid UsuarioId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // Administrador/superadmin autorizam a si mesmos: só o vendedor precisa da
        // senha de supervisor pra cancelar comanda ou remover item já lançado.
        // Público porque a view usa isso pra decidir se abre o modal de autorização.
        public bool DispensaAutorizacaoSupervisor => User.IsInRole("administrador") || User.IsInRole("superadmin");

        public async Task OnGetAsync(CancellationToken ct)
        {
            if (tenantProvider.TenantId is null)
            {
                SemTenant = true;
                return;
            }

            Produtos = (await catalogoService.ListarProdutosAsync(ct)).ToList();
            PrecosDiferenciados = (await catalogoService.ListarPrecosDiferenciadosAsync(ct)).ToList();
            Clientes = (await clienteService.ListarAsync(ct)).ToList();
            ComandasAbertas = (await comandaService.ListarAbertasAsync(ct)).ToList();
        }

        public async Task<JsonResult> OnPostAbrirAsync([FromBody] AbrirComandaRequest? request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request?.NumeroComanda))
            {
                return new JsonResult(new { sucesso = false, mensagem = "Informe o número/identificação da comanda." })
                { StatusCode = StatusCodes.Status400BadRequest };
            }

            try
            {
                var comanda = await comandaService.AbrirAsync(request.NumeroComanda, ct);
                return new JsonResult(new { sucesso = true, comanda });
            }
            catch (ComandaInvalidaException ex)
            {
                return new JsonResult(new { sucesso = false, mensagem = ex.Message })
                { StatusCode = StatusCodes.Status409Conflict };
            }
        }

        public async Task<JsonResult> OnGetDetalheAsync(Guid id, CancellationToken ct)
        {
            var detalhe = await comandaService.ObterDetalheAsync(id, ct);
            if (detalhe is null)
            {
                return new JsonResult(new { sucesso = false, mensagem = "Comanda não encontrada." })
                { StatusCode = StatusCodes.Status404NotFound };
            }

            return new JsonResult(new { sucesso = true, comanda = detalhe });
        }

        public async Task<JsonResult> OnPostAdicionarItemAsync([FromBody] AdicionarItemRequest? request, CancellationToken ct)
        {
            if (request is null || !Guid.TryParse(request.ComandaId, out var comandaId) ||
                !Guid.TryParse(request.ProdutoId, out var produtoId) || request.Quantidade <= 0)
            {
                return new JsonResult(new { sucesso = false, mensagem = "Item inválido." })
                { StatusCode = StatusCodes.Status400BadRequest };
            }

            var tipoPreco = request.TipoPreco?.ToUpperInvariant() switch
            {
                "CAIXA" => TipoPreco.Caixa,
                "ATACADO" => TipoPreco.Atacado,
                _ => TipoPreco.Balcao
            };

            try
            {
                var item = await comandaService.AdicionarItemAsync(comandaId, produtoId, request.Quantidade, tipoPreco, ct);
                return new JsonResult(new { sucesso = true, item });
            }
            catch (ComandaInvalidaException ex)
            {
                return new JsonResult(new { sucesso = false, mensagem = ex.Message })
                { StatusCode = StatusCodes.Status409Conflict };
            }
            catch (EstoqueInsuficienteException ex)
            {
                return new JsonResult(new
                {
                    sucesso = false,
                    mensagem = $"Estoque insuficiente para o produto {ex.ProdutoId}: disponível {ex.QuantidadeDisponivel}, solicitado {ex.QuantidadeSolicitada}."
                })
                { StatusCode = StatusCodes.Status409Conflict };
            }
        }

        public async Task<JsonResult> OnPostRemoverItemAsync([FromBody] RemoverItemRequest? request, CancellationToken ct)
        {
            if (request is null || !Guid.TryParse(request.ComandaId, out var comandaId) || !Guid.TryParse(request.ItemId, out var itemId))
            {
                return new JsonResult(new { sucesso = false, mensagem = "Item inválido." })
                { StatusCode = StatusCodes.Status400BadRequest };
            }

            if (!await AutorizadoAsync(request.SupervisorEmail, request.SupervisorSenha, ct))
            {
                return new JsonResult(new { sucesso = false, mensagem = "Autorização de supervisor inválida." })
                { StatusCode = StatusCodes.Status403Forbidden };
            }

            try
            {
                await comandaService.RemoverItemAsync(comandaId, itemId, ct);
                return new JsonResult(new { sucesso = true });
            }
            catch (ComandaInvalidaException ex)
            {
                return new JsonResult(new { sucesso = false, mensagem = ex.Message })
                { StatusCode = StatusCodes.Status409Conflict };
            }
        }

        public async Task<JsonResult> OnPostCancelarAsync([FromBody] CancelarComandaRequest? request, CancellationToken ct)
        {
            if (request is null || !Guid.TryParse(request.ComandaId, out var comandaId))
            {
                return new JsonResult(new { sucesso = false, mensagem = "Comanda inválida." })
                { StatusCode = StatusCodes.Status400BadRequest };
            }

            if (!await AutorizadoAsync(request.SupervisorEmail, request.SupervisorSenha, ct))
            {
                return new JsonResult(new { sucesso = false, mensagem = "Autorização de supervisor inválida." })
                { StatusCode = StatusCodes.Status403Forbidden };
            }

            try
            {
                await comandaService.CancelarAsync(comandaId, ct);
                return new JsonResult(new { sucesso = true });
            }
            catch (ComandaInvalidaException ex)
            {
                return new JsonResult(new { sucesso = false, mensagem = ex.Message })
                { StatusCode = StatusCodes.Status409Conflict };
            }
        }

        public async Task<JsonResult> OnPostFecharAsync([FromBody] FecharComandaRequest? request, CancellationToken ct)
        {
            if (request is null || !Guid.TryParse(request.ComandaId, out var comandaId))
            {
                return new JsonResult(new { sucesso = false, mensagem = "Comanda inválida." })
                { StatusCode = StatusCodes.Status400BadRequest };
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
                return new JsonResult(new { sucesso = false, mensagem = "Abra o turno de caixa antes de fechar comandas." })
                { StatusCode = StatusCodes.Status409Conflict };
            }

            // Snapshot dos itens ANTES de fechar (nomes de produto pro recibo) - depois do
            // fechamento a comanda muda de status e ObterDetalheAsync ainda funcionaria, mas
            // é mais simples já ter isso em mãos assim que o fechamento confirmar sucesso.
            var detalheAntesDoFechamento = await comandaService.ObterDetalheAsync(comandaId, ct);

            try
            {
                var resultado = await comandaService.FecharAsync(
                    comandaId, turnoAberto.Id, request.MetodoPagamento, request.EmitirNotaFiscal, clienteId, ct);

                var reciboImpresso = await TentarImprimirReciboAsync(resultado, request.MetodoPagamento, detalheAntesDoFechamento, ct);

                return new JsonResult(new
                {
                    sucesso = true,
                    mensagem = "Comanda fechada com sucesso.",
                    vendaId = resultado.VendaId,
                    totalVenda = resultado.TotalVenda,
                    reciboImpresso
                });
            }
            catch (ComandaInvalidaException ex)
            {
                return new JsonResult(new { sucesso = false, mensagem = ex.Message })
                { StatusCode = StatusCodes.Status409Conflict };
            }
            catch (EstoqueInsuficienteException ex)
            {
                return new JsonResult(new
                {
                    sucesso = false,
                    mensagem = $"Estoque insuficiente para o produto {ex.ProdutoId}: disponível {ex.QuantidadeDisponivel}, solicitado {ex.QuantidadeSolicitada}."
                })
                { StatusCode = StatusCodes.Status409Conflict };
            }
            catch (LimiteCreditoExcedidoException ex)
            {
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

        // Impressão nunca bloqueia o fechamento: já está tudo gravado no banco antes daqui.
        // Preço/quantidade vêm de resultado.Itens (o que foi REALMENTE aplicado na transação
        // da venda); o nome do produto vem do snapshot da comanda tirado antes de fechar.
        private async Task<bool> TentarImprimirReciboAsync(
            ResultadoVendaDto resultado, string metodoPagamento, ComandaDetalheDto? detalheAntesDoFechamento, CancellationToken ct)
        {
            var nomesPorId = detalheAntesDoFechamento?.Itens.ToDictionary(i => i.ProdutoId, i => i.ProdutoNome)
                ?? new Dictionary<Guid, string>();

            var itensRecibo = resultado.Itens
                .Select(i => new ItemReciboDto(nomesPorId.GetValueOrDefault(i.ProdutoId, "Produto"), i.Quantidade, i.PrecoUnitarioAplicado, i.Subtotal))
                .ToList();

            var loja = await tenantService.ObterAsync(tenantProvider.RequireTenantId(), ct);

            var recibo = new ReciboVendaDto(
                loja?.NomeFantasia ?? "Empório Gege", resultado.VendaId, DateTime.UtcNow, itensRecibo,
                resultado.TotalVenda, metodoPagamento, detalheAntesDoFechamento?.NumeroComanda);

            return await impressoraReciboService.ImprimirAsync(recibo, ct);
        }

        private async Task<bool> AutorizadoAsync(string? supervisorEmail, string? supervisorSenha, CancellationToken ct)
        {
            if (DispensaAutorizacaoSupervisor)
                return true;

            if (string.IsNullOrWhiteSpace(supervisorEmail) || string.IsNullOrWhiteSpace(supervisorSenha))
                return false;

            return await supervisorAutorizacaoService.AutorizarAsync(UsuarioId.ToString(), supervisorEmail, supervisorSenha, ct);
        }

        public record AbrirComandaRequest(string NumeroComanda);

        public record AdicionarItemRequest(string ComandaId, string ProdutoId, int Quantidade, string? TipoPreco);

        public record RemoverItemRequest(string ComandaId, string ItemId, string? SupervisorEmail, string? SupervisorSenha);

        public record CancelarComandaRequest(string ComandaId, string? SupervisorEmail, string? SupervisorSenha);

        public record FecharComandaRequest(string ComandaId, bool EmitirNotaFiscal, string MetodoPagamento, string? ClienteId);
    }
}
