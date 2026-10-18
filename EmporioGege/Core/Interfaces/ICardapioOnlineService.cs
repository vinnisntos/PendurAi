using EmporioGege.Application.DTOs;

namespace EmporioGege.Core.Interfaces
{
    // Cardápio online da loja: o que aparece pro bot de WhatsApp e na página pública /c/{slug}.
    public interface ICardapioOnlineService
    {
        Task<CardapioProdutoDto?> ObterProdutoAsync(Guid produtoId, CancellationToken ct = default);

        // foto null = mantém a atual; removerFoto apaga. Foto inválida => ArgumentException.
        Task SalvarProdutoAsync(Guid produtoId, bool exibirCardapio, string? descricao, byte[]? foto, bool removerFoto, CancellationToken ct = default);

        Task<ConfiguracaoCardapioDto> ObterConfiguracaoAsync(CancellationToken ct = default);

        Task SalvarConfiguracaoAsync(string? slug, string? whatsApp, string? callbackUrl, CancellationToken ct = default);

        // Cria (ou troca) o token da API do bot e o segredo do aviso de pagamento.
        Task<CredenciaisBotDto> GerarCredenciaisAsync(CancellationToken ct = default);

        Task<IReadOnlyList<PedidoOnlineResumoDto>> ListarPedidosAsync(int dias, CancellationToken ct = default);

        // Públicos (sem login/tenant): resolvem a loja pelo slug ou pelo próprio produto.
        Task<CardapioPublicoDto?> ObterCardapioPublicoAsync(string slug, CancellationToken ct = default);

        Task<FotoProdutoDto?> ObterFotoPublicaAsync(Guid produtoId, CancellationToken ct = default);
    }
}
