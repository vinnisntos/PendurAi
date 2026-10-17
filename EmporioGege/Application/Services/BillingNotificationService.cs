using Dapper;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Application.Services
{
    public class BillingNotificationService(IDbConnectionFactory connectionFactory, IEmailSender emailSender, ILogger<BillingNotificationService> logger)
        : IBillingNotificationService
    {
        private static readonly Dictionary<string, string> NomesPlano = new()
        {
            ["start"] = "Start",
            ["pro"] = "Pro",
            ["enterprise"] = "Enterprise"
        };

        public async Task EnviarBoasVindasTrialAsync(Guid tenantId, int diasTrial, CancellationToken ct = default)
        {
            var loja = await ObterDadosLojaAsync(tenantId, ct);
            if (loja is null)
                return;

            var assunto = "Bem-vindo(a) ao PendurAi - seu teste grátis começou";
            var corpo = $"""
                <p>Olá, {H(loja.NomeRepresentante)}!</p>
                <p>Seu cadastro da loja <strong>{H(loja.NomeFantasia)}</strong> no PendurAi foi recebido com sucesso.</p>
                <p>Você tem <strong>{diasTrial} dias grátis</strong>, sem cobrança, pra usar o sistema à vontade - PDV, estoque, fiado de clientes e mais.</p>
                <p>Quando quiser assinar (ou quando o teste acabar), é só entrar no painel e clicar em "Assinar agora" - vamos te enviar o link de pagamento na hora.</p>
                <p>Qualquer dúvida, é só responder este e-mail.</p>
                """;

            await emailSender.EnviarAsync(loja.EmailDono, loja.NomeRepresentante, assunto, corpo, ct);
        }

        public async Task EnviarInstrucoesPagamentoAsync(Guid tenantId, CancellationToken ct = default)
        {
            var loja = await ObterDadosLojaAsync(tenantId, ct);
            if (loja is null)
                return;

            var nomePlano = loja.Plano is not null && NomesPlano.TryGetValue(loja.Plano, out var nome) ? nome : loja.Plano ?? "seu plano";
            var valorFormatado = loja.ValorMensalidade?.ToString("C", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")) ?? "";

            var assunto = "PendurAi - finalize sua assinatura";
            var botaoOuAviso = loja.AsaasInvoiceUrl is { Length: > 0 }
                ? EmporioGege.Infrastructure.Email.EmailLayout.Botao(H(loja.AsaasInvoiceUrl), "Pagar agora")
                : "<p>Estamos finalizando a geração da sua cobrança - você recebe o link em instantes, ou pode acessar o painel e clicar em \"Assinar agora\" novamente.</p>";

            var corpo = $"""
                <p>Olá, {H(loja.NomeRepresentante)}!</p>
                <p>Sua cobrança do plano <strong>{nomePlano}</strong> ({valorFormatado}/mês) já foi gerada.</p>
                {botaoOuAviso}
                <p>Assim que o pagamento for identificado, seu acesso é liberado automaticamente - sem precisar fazer mais nada.</p>
                """;

            await emailSender.EnviarAsync(loja.EmailDono, loja.NomeRepresentante, assunto, corpo, ct);
        }

        public async Task EnviarPagamentoConfirmadoAsync(Guid tenantId, CancellationToken ct = default)
        {
            var loja = await ObterDadosLojaAsync(tenantId, ct);
            if (loja is null)
                return;

            var assunto = "Pagamento confirmado - seu acesso ao PendurAi está liberado";
            var corpo = $"""
                <p>Olá, {H(loja.NomeRepresentante)}!</p>
                <p>Recebemos a confirmação do pagamento da <strong>{H(loja.NomeFantasia)}</strong>. Seu acesso já está liberado - pode entrar no sistema agora mesmo.</p>
                """;

            await emailSender.EnviarAsync(loja.EmailDono, loja.NomeRepresentante, assunto, corpo, ct);
        }

        // Nome da loja/representante vem do cadastro público: sem escapar, qualquer um mandaria HTML
        // e links arbitrários saindo do nosso domínio de e-mail.
        private static string H(string? texto) => System.Net.WebUtility.HtmlEncode(texto ?? "");

        private async Task<DadosLojaRow?> ObterDadosLojaAsync(Guid tenantId, CancellationToken ct)
        {
            try
            {
                await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
                return await connection.QuerySingleOrDefaultAsync<DadosLojaRow>(new CommandDefinition(
                    """
                    SELECT nome_fantasia AS NomeFantasia, nome_representante AS NomeRepresentante, email_dono AS EmailDono,
                           plano AS Plano, valor_mensalidade AS ValorMensalidade, asaas_invoice_url AS AsaasInvoiceUrl
                    FROM tenants WHERE id = @TenantId
                    """,
                    new { TenantId = tenantId }, cancellationToken: ct));
            }
            catch (Exception ex)
            {
                // Notificação é best-effort: uma falha aqui não pode derrubar o cadastro, o
                // webhook de pagamento ou o job de trial expirado que a chamaram.
                logger.LogError(ex, "Falha ao carregar dados da loja {TenantId} pra notificação de billing.", tenantId);
                return null;
            }
        }

        private sealed record DadosLojaRow(
            string NomeFantasia, string NomeRepresentante, string EmailDono,
            string? Plano, decimal? ValorMensalidade, string? AsaasInvoiceUrl);
    }
}
