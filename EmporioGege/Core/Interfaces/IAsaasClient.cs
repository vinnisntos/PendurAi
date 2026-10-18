using EmporioGege.Application.DTOs;

namespace EmporioGege.Core.Interfaces
{
    // Cliente HTTP da API do Asaas (cobrança/assinatura da própria loja pra usar o PendurAi -
    // diferente do PDV, que é cobrança do CLIENTE FINAL da loja). Autenticação via header
    // "access_token" - um único token de conta serve pra toda operação (diferente do Focus
    // NFe, que usa um token por empresa cadastrada).
    public interface IAsaasClient
    {
        Task<CriarClienteAsaasResultado> CriarClienteAsync(CriarClienteAsaasDto dto, CancellationToken ct = default);

        Task<CriarAssinaturaAsaasResultado> CriarAssinaturaAsync(CriarAssinaturaAsaasDto dto, CancellationToken ct = default);

        // Busca a cobrança mais recente gerada pra uma assinatura e devolve o link hospedado
        // (invoiceUrl) onde o lojista paga - a criação da assinatura não retorna esse link
        // diretamente, só a Asaas gera a primeira cobrança de forma assíncrona (geralmente
        // quase imediata). Retorna null (não é erro) se a cobrança ainda não existir.
        Task<string?> ObterLinkCobrancaAsync(string subscriptionId, CancellationToken ct = default);

        // Pedido online (bot de WhatsApp): reaproveita o cliente Asaas pelo CPF em vez de criar
        // um novo a cada compra, e gera uma cobrança Pix avulsa já com o QR Code.
        Task<string?> BuscarClienteIdPorCpfCnpjAsync(string cpfCnpj, CancellationToken ct = default);

        Task<CriarCobrancaPixResultado> CriarCobrancaPixAsync(CriarCobrancaPixDto dto, CancellationToken ct = default);
    }
}
