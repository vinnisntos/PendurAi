using EmporioGege.Application.DTOs;

namespace EmporioGege.Core.Interfaces
{
    // Pedidos feitos pelo bot de WhatsApp. O pedido só vira venda (e baixa estoque) quando o
    // Pix é confirmado - até lá é só uma cobrança em aberto, sem mexer no estoque.
    public interface IPedidoOnlineService
    {
        Task<IReadOnlyList<ItemCardapioDto>> ListarCardapioAsync(CancellationToken ct = default);

        Task<PedidoOnlineDto> CriarPedidoAsync(CriarPedidoOnlineDto dto, CancellationToken ct = default);

        Task<PedidoOnlineDto?> ObterPedidoAsync(Guid id, CancellationToken ct = default);

        Task<int> ContarAguardandoPagamentoAsync(CancellationToken ct = default);

        // Chamado pelo webhook do Asaas, sem tenant na requisição: o tenant vem do próprio pedido.
        // Idempotente - reentrega do mesmo evento não gera venda nem baixa de estoque em dobro.
        Task ConfirmarPagamentoAsync(Guid pedidoId, string cobrancaId, CancellationToken ct = default);

        Task ExpirarAsync(Guid pedidoId, string cobrancaId, CancellationToken ct = default);
    }
}
