using System.Text.Json;
using Dapper;
using EmporioGege.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace EmporioGege.Application.Services
{
    // Mapeia eventos de webhook do Asaas pra status_licenca do tenant. Diferente do webhook
    // Zé Delivery (que CRIA vendas e por isso precisa de fila durável + idempotência via
    // event id), aqui só SETAMOS um status - aplicar o mesmo evento duas vezes é inofensivo
    // por natureza, então não há fila própria: se o processamento falhar, devolvemos 500 e
    // deixamos a própria Asaas reentregar o evento (comportamento padrão deles).
    public class AsaasWebhookService(
        IDbConnectionFactory connectionFactory, IBillingNotificationService billingNotificationService, ILogger<AsaasWebhookService> logger)
        : IAsaasWebhookService
    {
        public async Task ProcessarAsync(string corpoJson, CancellationToken ct = default)
        {
            using var documento = JsonDocument.Parse(corpoJson);
            var raiz = documento.RootElement;
            var evento = raiz.TryGetProperty("event", out var eventoEl) ? eventoEl.GetString() : null;

            if (evento is null)
            {
                logger.LogWarning("Webhook Asaas recebido sem campo 'event'.");
                return;
            }

            string? subscriptionId = null;
            DateOnly? vencimentoPagamento = null;

            if (raiz.TryGetProperty("payment", out var paymentEl))
            {
                if (paymentEl.TryGetProperty("subscription", out var subEl) && subEl.ValueKind == JsonValueKind.String)
                    subscriptionId = subEl.GetString();

                if (paymentEl.TryGetProperty("dueDate", out var dueDateEl) && dueDateEl.ValueKind == JsonValueKind.String
                    && DateOnly.TryParse(dueDateEl.GetString(), out var dueDate))
                    vencimentoPagamento = dueDate;
            }
            else if (raiz.TryGetProperty("subscription", out var subObjEl) && subObjEl.TryGetProperty("id", out var subIdEl))
            {
                subscriptionId = subIdEl.GetString();
            }

            if (subscriptionId is null)
            {
                logger.LogWarning("Webhook Asaas ({Evento}) recebido sem subscription associada - ignorado.", evento);
                return;
            }

            var novoStatus = evento switch
            {
                "PAYMENT_CONFIRMED" or "PAYMENT_RECEIVED" => "ativo",
                "PAYMENT_OVERDUE" or "PAYMENT_REFUNDED" or "PAYMENT_CHARGEBACK_REQUESTED" => "suspenso",
                "SUBSCRIPTION_DELETED" or "SUBSCRIPTION_INACTIVATED" => "cancelado",
                _ => null
            };

            if (novoStatus is null)
                return; // Evento que não mapeamos pra status_licenca (ex.: PAYMENT_CREATED) - ignorado de propósito.

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            // Replay: o mesmo evento reenviado (por quem tiver o token, ou reentrega do Asaas depois
            // de já processado) não muda o status de novo. O registro e a mudança commitam juntos.
            var eventoId = raiz.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            if (eventoId is not null)
            {
                var novo = await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO asaas_eventos (id, evento) VALUES (@Id, @Evento) ON CONFLICT (id) DO NOTHING",
                    new { Id = eventoId, Evento = evento }, transaction, cancellationToken: ct));
                if (novo == 0)
                {
                    logger.LogInformation("Evento Asaas {EventoId} já processado; ignorado.", eventoId);
                    return;
                }
            }

            // Busca o tenant e o status ANTERIOR antes de atualizar - precisamos saber se essa
            // confirmação é a que libera o acesso pela primeira vez (ou reativa após suspensão)
            // pra decidir se manda o e-mail de "pagamento confirmado" (ver mais abaixo). Sem
            // isso, toda cobrança MENSAL recorrente (que também dispara PAYMENT_CONFIRMED)
            // reenviaria o mesmo e-mail de "acesso liberado" todo mês.
            var tenant = await connection.QuerySingleOrDefaultAsync<TenantParaWebhookRow>(new CommandDefinition(
                "SELECT id AS Id, status_licenca AS StatusLicencaAnterior, data_expiracao AS DataExpiracao FROM tenants WHERE asaas_subscription_id = @SubscriptionId",
                new { SubscriptionId = subscriptionId }, cancellationToken: ct));

            if (tenant is null)
            {
                logger.LogWarning("Webhook Asaas ({Evento}) referenciou subscription {SubscriptionId} sem tenant correspondente.", evento, subscriptionId);
                return;
            }

            // A cobrança paga cobre o mês seguinte ao vencimento (assinatura MONTHLY), mais 5 dias de
            // folga. Campo informativo: quem controla o acesso de loja com assinatura é status_licenca.
            DateTime? novaDataExpiracao = novoStatus == "ativo" && vencimentoPagamento is not null
                ? CoberturaAte(vencimentoPagamento.Value)
                : null;

            // Atraso de uma fatura que já foi coberta por um pagamento posterior (eventos chegam fora
            // de ordem): não suspende quem está em dia.
            if (evento == "PAYMENT_OVERDUE" && vencimentoPagamento is not null && CoberturaAte(vencimentoPagamento.Value) < tenant.DataExpiracao)
            {
                logger.LogInformation("PAYMENT_OVERDUE antigo ignorado para o tenant {TenantId}.", tenant.Id);
                return;
            }

            var sql = novaDataExpiracao is not null
                ? "UPDATE tenants SET status_licenca = @Status, data_expiracao = @DataExpiracao WHERE id = @TenantId"
                : novoStatus == "cancelado"
                    // Solta o vínculo pra loja poder receber uma assinatura nova (ver FaturamentoService).
                    ? "UPDATE tenants SET status_licenca = @Status, asaas_subscription_id = NULL, asaas_invoice_url = NULL WHERE id = @TenantId"
                    : "UPDATE tenants SET status_licenca = @Status WHERE id = @TenantId";

            await connection.ExecuteAsync(new CommandDefinition(
                sql, new { Status = novoStatus, DataExpiracao = novaDataExpiracao, TenantId = tenant.Id }, cancellationToken: ct));

            await transaction.CommitAsync(ct);

            if (novoStatus == "ativo" && tenant.StatusLicencaAnterior != "ativo")
                await billingNotificationService.EnviarPagamentoConfirmadoAsync(tenant.Id, ct);
        }

        public static DateTime CoberturaAte(DateOnly vencimento) =>
            vencimento.AddMonths(1).AddDays(5).ToDateTime(TimeOnly.MinValue);

        private sealed record TenantParaWebhookRow(Guid Id, string StatusLicencaAnterior, DateTime DataExpiracao);
    }
}
