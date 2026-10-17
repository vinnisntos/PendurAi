using Dapper;
using EmporioGege.Core.Entities;
using EmporioGege.Core.Enums;
using EmporioGege.Core.Interfaces;
using Npgsql;

namespace EmporioGege.Infrastructure.Webhooks
{
    public class WebhookRepository(IDbConnectionFactory connectionFactory) : IWebhookRepository
    {
        public async Task<IntegracaoWebhook?> BuscarIntegracaoAtivaPorTokenAsync(string tokenUrl, CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var registro = await connection.QuerySingleOrDefaultAsync<IntegracaoWebhookRow>(
                new CommandDefinition(
                    """
                    SELECT id AS Id, tenant_id AS TenantId, canal AS Canal, token_url AS TokenUrl, segredo_hmac AS SegredoHmac, ativo AS Ativo
                    FROM integracoes_webhook
                    WHERE token_url = @TokenUrl AND ativo = true
                    """,
                    new { TokenUrl = tokenUrl }, cancellationToken: ct));

            return registro is null ? null : new IntegracaoWebhook
            {
                Id = registro.Id,
                TenantId = registro.TenantId,
                Canal = ParseCanal(registro.Canal),
                TokenUrl = registro.TokenUrl,
                SegredoHmac = registro.SegredoHmac,
                Ativo = registro.Ativo
            };
        }

        public async Task<Guid?> RegistrarRecebimentoAsync(Guid tenantId, CanalIntegracao canal, string eventoExternoId, string payloadJson, CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var id = Guid.NewGuid();

            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO webhooks_recebidos (id, tenant_id, canal, evento_externo_id, payload, status, recebido_em)
                    VALUES (@Id, @TenantId, @Canal, @EventoExternoId, @Payload::jsonb, 'RECEBIDO', now())
                    """,
                    new { Id = id, TenantId = tenantId, Canal = ToTexto(canal), EventoExternoId = eventoExternoId, Payload = payloadJson },
                    cancellationToken: ct));
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                // Reentrega do mesmo evento pelo parceiro — idempotência via constraint única.
                return null;
            }

            return id;
        }

        // Ao iniciar não há nada processando: o que ficou PROCESSANDO foi interrompido por um
        // restart/deploy. Volta pra fila; o índice único de vendas impede venda duplicada.
        public async Task<int> RecolocarInterrompidosAsync(CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE webhooks_recebidos SET status = 'RECEBIDO' WHERE status = 'PROCESSANDO'", cancellationToken: ct));
        }

        public async Task<IReadOnlyList<Guid>> ListarPendentesAsync(CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var ids = await connection.QueryAsync<Guid>(
                new CommandDefinition(
                    "SELECT id FROM webhooks_recebidos WHERE status = 'RECEBIDO' ORDER BY recebido_em",
                    cancellationToken: ct));
            return ids.AsList();
        }

        public async Task<bool> TentarReivindicarAsync(Guid id, CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhasAfetadas = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE webhooks_recebidos SET status = 'PROCESSANDO' WHERE id = @Id AND status = 'RECEBIDO'",
                new { Id = id }, cancellationToken: ct));
            return linhasAfetadas == 1;
        }

        public async Task<WebhookRecebido?> ObterAsync(Guid id, CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var registro = await connection.QuerySingleOrDefaultAsync<WebhookRecebidoRow>(
                new CommandDefinition(
                    """
                    SELECT id AS Id, tenant_id AS TenantId, canal AS Canal, evento_externo_id AS EventoExternoId, payload::text AS Payload, status AS Status
                    FROM webhooks_recebidos
                    WHERE id = @Id
                    """,
                    new { Id = id }, cancellationToken: ct));

            return registro is null ? null : new WebhookRecebido
            {
                Id = registro.Id,
                TenantId = registro.TenantId,
                Canal = ParseCanal(registro.Canal),
                EventoExternoId = registro.EventoExternoId,
                Payload = registro.Payload,
                Status = ParseStatus(registro.Status)
            };
        }

        public Task MarcarProcessadoAsync(Guid id, Guid tenantId, CancellationToken ct = default) =>
            AtualizarStatusAsync(id, tenantId, "PROCESSADO", erro: null, ct);

        public Task MarcarErroAsync(Guid id, Guid tenantId, string erro, CancellationToken ct = default) =>
            AtualizarStatusAsync(id, tenantId, "ERRO", erro, ct);

        private async Task AtualizarStatusAsync(Guid id, Guid tenantId, string status, string? erro, CancellationToken ct)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE webhooks_recebidos
                SET status = @Status, erro = @Erro, processado_em = now()
                WHERE id = @Id AND tenant_id = @TenantId
                """,
                new { Status = status, Erro = erro, Id = id, TenantId = tenantId },
                cancellationToken: ct));
        }

        private static CanalIntegracao ParseCanal(string canal) => canal switch
        {
            "ZEDELIVERY" => CanalIntegracao.ZeDelivery,
            _ => throw new NotSupportedException($"Canal de integração desconhecido: {canal}")
        };

        private static string ToTexto(CanalIntegracao canal) => canal switch
        {
            CanalIntegracao.ZeDelivery => "ZEDELIVERY",
            _ => throw new NotSupportedException($"Canal de integração desconhecido: {canal}")
        };

        private static StatusWebhook ParseStatus(string status) => status switch
        {
            "RECEBIDO" => StatusWebhook.Recebido,
            "PROCESSANDO" => StatusWebhook.Processando,
            "PROCESSADO" => StatusWebhook.Processado,
            "ERRO" => StatusWebhook.Erro,
            _ => throw new NotSupportedException($"Status de webhook desconhecido: {status}")
        };

        private sealed record IntegracaoWebhookRow(Guid Id, Guid TenantId, string Canal, string TokenUrl, string SegredoHmac, bool Ativo);

        private sealed record WebhookRecebidoRow(Guid Id, Guid TenantId, string Canal, string EventoExternoId, string Payload, string Status);
    }
}
