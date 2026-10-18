namespace EmporioGege.Application.DTOs
{
    public record CriarClienteAsaasDto(string Nome, string? CpfCnpj, string? Email, string? Telefone);

    public record CriarClienteAsaasResultado(bool Sucesso, string? ClienteId, string? MensagemErro);

    public record CriarAssinaturaAsaasDto(
        string ClienteId, decimal Valor, string Ciclo, string TipoCobranca, string Descricao, DateOnly ProximoVencimento);

    // InvoiceUrl: link hospedado pela própria Asaas onde o pagador conclui o pagamento
    // (boleto/PIX/cartão conforme o TipoCobranca escolhido) - vem de uma segunda chamada
    // (ver IAsaasClient.ObterLinkCobrancaAsync), não da resposta de criação da assinatura,
    // que não traz esse campo.
    public record CriarAssinaturaAsaasResultado(bool Sucesso, string? AssinaturaId, string? Status, string? MensagemErro, string? InvoiceUrl = null);

    // ReferenciaExterna vira o externalReference da cobrança - é por ele que o webhook do Asaas
    // sabe que o pagamento é de um pedido online, e não da assinatura de uma loja.
    public record CriarCobrancaPixDto(string ClienteId, decimal Valor, string Descricao, DateOnly Vencimento, string ReferenciaExterna);

    public record CriarCobrancaPixResultado(
        bool Sucesso, string? CobrancaId, string? PixCopiaECola, string? PixQrCodeBase64, DateTime? PixExpiraEm, string? MensagemErro);

    // Leitura: estado atual da assinatura da loja (pra tela SuperAdmin/Adegas/Editar e pro
    // painel do próprio lojista em Admin/Index).
    public record AssinaturaTenantDto(
        string? Plano, decimal? ValorMensalidade, string StatusLicenca, DateTime DataExpiracao,
        string? AsaasCustomerId, string? AsaasSubscriptionId, string? AsaasInvoiceUrl);
}
