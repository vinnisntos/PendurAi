using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Application.Services
{
    public class FaturamentoService(IDbConnectionFactory connectionFactory, IAsaasClient asaasClient) : IFaturamentoService
    {
        public async Task<AssinaturaTenantDto?> ObterAsync(Guid tenantId, CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.QuerySingleOrDefaultAsync<AssinaturaTenantDto>(new CommandDefinition(
                """
                SELECT plano AS Plano, valor_mensalidade AS ValorMensalidade, status_licenca AS StatusLicenca,
                       data_expiracao AS DataExpiracao, asaas_customer_id AS AsaasCustomerId,
                       asaas_subscription_id AS AsaasSubscriptionId, asaas_invoice_url AS AsaasInvoiceUrl
                FROM tenants
                WHERE id = @TenantId
                """,
                new { TenantId = tenantId }, cancellationToken: ct));
        }

        public async Task<CriarAssinaturaAsaasResultado> CriarAssinaturaAsync(
            Guid tenantId, string plano, decimal valorMensalidade, string tipoCobranca, CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);

            // Duplo clique em "Assinar agora", ou o clique junto com o TrialExpiradoProcessor, criava
            // dois clientes e duas assinaturas cobrando a mesma loja. O lock de sessão serializa por loja.
            var chaveLock = $"asaas-assinatura:{tenantId}";
            var livre = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT pg_try_advisory_lock(hashtext(@Chave))", new { Chave = chaveLock }, cancellationToken: ct));
            if (!livre)
                return new CriarAssinaturaAsaasResultado(false, null, null, "Já existe uma criação de assinatura em andamento para esta loja.");

            try
            {
                return await CriarAssinaturaComLockAsync(connection, tenantId, plano, valorMensalidade, tipoCobranca, ct);
            }
            finally
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "SELECT pg_advisory_unlock(hashtext(@Chave))", new { Chave = chaveLock }, cancellationToken: CancellationToken.None));
            }
        }

        private async Task<CriarAssinaturaAsaasResultado> CriarAssinaturaComLockAsync(
            System.Data.Common.DbConnection connection, Guid tenantId, string plano, decimal valorMensalidade, string tipoCobranca, CancellationToken ct)
        {
            var loja = await connection.QuerySingleOrDefaultAsync<LojaResumoRow>(new CommandDefinition(
                """
                SELECT nome_fantasia AS NomeFantasia, cnpj AS Cnpj, email_dono AS EmailDono,
                       telefone_dono AS TelefoneDono, asaas_customer_id AS AsaasCustomerId,
                       asaas_subscription_id AS AsaasSubscriptionId
                FROM tenants WHERE id = @TenantId
                """,
                new { TenantId = tenantId }, cancellationToken: ct));

            if (loja is null)
                return new CriarAssinaturaAsaasResultado(false, null, null, $"Loja {tenantId} não encontrada.");

            // Criar outra sem cancelar a anterior deixava as duas cobrando.
            if (!string.IsNullOrEmpty(loja.AsaasSubscriptionId))
                return new CriarAssinaturaAsaasResultado(false, null, null,
                    $"Esta loja já tem a assinatura {loja.AsaasSubscriptionId} no Asaas. Cancele-a no painel do Asaas antes de criar outra.");

            var clienteId = loja.AsaasCustomerId;
            if (string.IsNullOrEmpty(clienteId))
            {
                var resultadoCliente = await asaasClient.CriarClienteAsync(
                    new CriarClienteAsaasDto(loja.NomeFantasia, loja.Cnpj, loja.EmailDono, loja.TelefoneDono), ct);

                if (!resultadoCliente.Sucesso)
                    return new CriarAssinaturaAsaasResultado(false, null, null, resultadoCliente.MensagemErro);

                clienteId = resultadoCliente.ClienteId!;
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE tenants SET asaas_customer_id = @ClienteId WHERE id = @TenantId",
                    new { ClienteId = clienteId, TenantId = tenantId }, cancellationToken: ct));
            }

            // Primeira cobrança daqui a 7 dias - dá tempo da loja receber o boleto/link antes
            // do vencimento (nextDueDate é OBRIGATÓRIO na criação, mesmo pra assinatura recorrente).
            var resultadoAssinatura = await asaasClient.CriarAssinaturaAsync(new CriarAssinaturaAsaasDto(
                clienteId, valorMensalidade, "MONTHLY", tipoCobranca, $"PendurAi - Plano {plano}",
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7))), ct);

            if (!resultadoAssinatura.Sucesso)
                return resultadoAssinatura;

            // Busca o link da primeira cobrança logo em seguida - a Asaas gera a cobrança de
            // forma assíncrona, então pode ainda não existir neste exato instante (não é
            // fatal: fica null, e quem chamou tenta mostrar/reenviar depois via SuperAdmin ou
            // um novo "Assinar agora").
            var invoiceUrl = await asaasClient.ObterLinkCobrancaAsync(resultadoAssinatura.AssinaturaId!, ct);

            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE tenants SET plano = @Plano, valor_mensalidade = @ValorMensalidade,
                       asaas_subscription_id = @AssinaturaId, asaas_invoice_url = @InvoiceUrl
                WHERE id = @TenantId
                """,
                new { Plano = plano, ValorMensalidade = valorMensalidade, AssinaturaId = resultadoAssinatura.AssinaturaId, InvoiceUrl = invoiceUrl, TenantId = tenantId },
                cancellationToken: ct));

            return resultadoAssinatura with { InvoiceUrl = invoiceUrl };
        }

        public async Task<CriarAssinaturaAsaasResultado> IniciarCobrancaTrialAsync(Guid tenantId, CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);

            var trial = await connection.QuerySingleOrDefaultAsync<TrialResumoRow>(new CommandDefinition(
                """
                SELECT status_licenca AS StatusLicenca, plano AS Plano, valor_mensalidade AS ValorMensalidade,
                       forma_pagamento_preferida AS FormaPagamentoPreferida
                FROM tenants WHERE id = @TenantId
                """,
                new { TenantId = tenantId }, cancellationToken: ct));

            if (trial is null)
                return new CriarAssinaturaAsaasResultado(false, null, null, $"Loja {tenantId} não encontrada.");

            // Guard: só converte quem ainda está em trial - evita recriar assinatura de quem
            // já pagou (ou já está pendente/suspenso por outro motivo) num duplo-clique ou
            // numa corrida entre o botão "Assinar agora" e o job de trial expirado.
            if (trial.StatusLicenca != "trial")
                return new CriarAssinaturaAsaasResultado(false, null, null, "Esta loja não está mais em período de teste.");

            if (string.IsNullOrWhiteSpace(trial.Plano) || trial.ValorMensalidade is not { } valor || valor <= 0)
                return new CriarAssinaturaAsaasResultado(false, null, null, "Plano/valor da loja não foram definidos no cadastro.");

            var resultado = await CriarAssinaturaAsync(tenantId, trial.Plano, valor, trial.FormaPagamentoPreferida ?? "PIX", ct);
            if (!resultado.Sucesso)
                return resultado;

            // Só agora, com a assinatura criada, o acesso passa a exigir pagamento -
            // "pendente" reaproveita o mesmo bloqueio de login já usado pelo cadastro
            // antigo (ver Login.cshtml.cs).
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE tenants SET status_licenca = 'pendente' WHERE id = @TenantId AND status_licenca = 'trial'",
                new { TenantId = tenantId }, cancellationToken: ct));

            return resultado;
        }

        private sealed record LojaResumoRow(string NomeFantasia, string Cnpj, string EmailDono, string TelefoneDono, string? AsaasCustomerId, string? AsaasSubscriptionId);

        private sealed record TrialResumoRow(string StatusLicenca, string? Plano, decimal? ValorMensalidade, string? FormaPagamentoPreferida);
    }
}
