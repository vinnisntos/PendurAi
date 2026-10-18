namespace EmporioGege.Application.DTOs
{
    // Contrato da API /api/v1 consumida pelo bot de atendimento no WhatsApp.

    public record ItemCardapioDto(Guid Id, string Nome, string? Descricao, string? FotoUrl, decimal Preco, int EstoqueDisponivel);

    public record ItemPedidoOnlineDto(Guid ProdutoId, int Quantidade);

    public record CriarPedidoOnlineDto(string ClienteNome, string ClienteTelefone, string ClienteCpf, List<ItemPedidoOnlineDto> Itens, string? Observacao = null);

    // Preço gravado no pedido = o que o cliente viu e pagou no Pix; é ele que vai pra venda.
    public record ItemPedidoGravadoDto(Guid ProdutoId, string Nome, int Quantidade, decimal PrecoUnitario);

    // PixQrCodeBase64 só vem na criação (não é guardado no banco - o copia e cola basta pra reenviar).
    public record PedidoOnlineDto(
        Guid Id, string Status, decimal Total, IReadOnlyList<ItemPedidoGravadoDto> Itens,
        string? PixCopiaECola, string? PixQrCodeBase64, DateTime? PixExpiraEm, DateTime CriadoEm, DateTime? PagoEm, string? Erro);

    public record MetricasLojaDto(
        DateOnly De, DateOnly Ate, decimal Faturamento, decimal LucroBruto, int TotalVendas, decimal TicketMedio,
        IReadOnlyList<ProdutoMaisVendidoDto> MaisVendidos, int ProdutosEstoqueCritico, int PedidosAguardandoPagamento);
}
