namespace EmporioGege.Application.DTOs
{
    public record CardapioProdutoDto(bool ExibirCardapio, string? Descricao, string? FotoUrl);

    public record FotoProdutoDto(byte[] Conteudo, string ContentType, DateTime AtualizadoEm);

    // Token/segredo só aparecem por inteiro logo depois de gerados (ver GerarCredenciaisAsync).
    public record ConfiguracaoCardapioDto(string? Slug, string? WhatsApp, bool IntegracaoAtiva, string? TokenFinal, string? CallbackUrl);

    public record CredenciaisBotDto(string Token, string Segredo);

    public record PedidoOnlineResumoDto(Guid Id, DateTime CriadoEm, string ClienteNome, string ClienteTelefone, decimal Total, string Status, string? Erro);

    public record ItemCardapioPublicoDto(Guid Id, string Nome, string? Descricao, string? FotoUrl, decimal Preco, bool Esgotado);

    public record CardapioPublicoDto(string NomeLoja, string? WhatsApp, IReadOnlyList<ItemCardapioPublicoDto> Itens);
}
