using Dapper;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Infrastructure.Faturamento
{
    // Varre periodicamente os tenants em trial vencido (data_expiracao no passado) e converte
    // automaticamente em cobrança de verdade - gera a assinatura no Asaas, move status_licenca
    // pra "pendente" (bloqueia login até o pagamento ser confirmado) e dispara o e-mail com o
    // link de pagamento. Sem isso, um lojista que nunca voltou ao painel durante o trial
    // ficaria com acesso liberado pra sempre (data_expiracao só é checada no login, e mesmo lá
    // "trial" nunca bloqueia sozinho - ver Login.cshtml.cs).
    //
    // Intervalo de 1h é suficiente pra esse caso de uso (não é uma cobrança em tempo real tipo
    // webhook) - mesmo padrão defensivo do ZeDeliveryWebhookProcessor: nenhuma exceção pode
    // escapar de ExecuteAsync, senão derruba a aplicação inteira.
    public class TrialExpiradoProcessor(IServiceScopeFactory scopeFactory, ILogger<TrialExpiradoProcessor> logger) : BackgroundService
    {
        private static readonly TimeSpan Intervalo = TimeSpan.FromHours(1);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(Intervalo);

            // Roda uma vez já na subida (não espera 1h pro primeiro ciclo) - importante pra
            // ambientes que reiniciam com frequência não deixarem trials vencidos acumulando.
            await ProcessarTrialsVencidosAsync(stoppingToken);

            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                    await ProcessarTrialsVencidosAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Encerramento normal do host.
            }
        }

        private async Task ProcessarTrialsVencidosAsync(CancellationToken ct)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();

                await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
                await RemoverCadastrosNaoConfirmadosAsync(connection, ct);

                var tenantsVencidos = await connection.QueryAsync<Guid>(new CommandDefinition(
                    "SELECT id FROM tenants WHERE status_licenca = 'trial' AND data_expiracao < now()",
                    cancellationToken: ct));

                foreach (var tenantId in tenantsVencidos)
                    await ConverterTrialAsync(tenantId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao varrer trials vencidos.");
            }
        }

        // Cadastro público cujo e-mail nunca foi confirmado em 48h: ninguém consegue entrar nele, e
        // ele travava o CNPJ/e-mail (alguém podia cadastrar o CNPJ de outra loja primeiro).
        private async Task RemoverCadastrosNaoConfirmadosAsync(System.Data.Common.DbConnection connection, CancellationToken ct)
        {
            var abandonados = await connection.QueryAsync<Guid>(new CommandDefinition(
                """
                SELECT t.id FROM tenants t
                WHERE t.status_licenca = 'trial' AND t.created_at < now() - interval '48 hours' AND t.asaas_customer_id IS NULL
                  AND EXISTS (SELECT 1 FROM profiles p WHERE p.tenant_id = t.id)
                  AND NOT EXISTS (SELECT 1 FROM profiles p JOIN auth.users u ON u.id = p.id
                                  WHERE p.tenant_id = t.id AND u.email_confirmed_at IS NOT NULL)
                  AND NOT EXISTS (SELECT 1 FROM vendas v WHERE v.tenant_id = t.id)
                """, cancellationToken: ct));

            foreach (var tenantId in abandonados)
            {
                try
                {
                    await using var transaction = await connection.BeginTransactionAsync(ct);
                    var usuarios = (await connection.QueryAsync<Guid>(new CommandDefinition(
                        "DELETE FROM profiles WHERE tenant_id = @TenantId RETURNING id", new { TenantId = tenantId }, transaction, cancellationToken: ct))).AsList();
                    await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM tenants WHERE id = @TenantId", new { TenantId = tenantId }, transaction, cancellationToken: ct));
                    await transaction.CommitAsync(ct);

                    // Best-effort: sem permissão no schema auth, o login fica órfão mas o CNPJ já foi liberado.
                    try
                    {
                        await connection.ExecuteAsync(new CommandDefinition(
                            "DELETE FROM auth.users WHERE id = ANY(@Ids) AND email_confirmed_at IS NULL", new { Ids = usuarios.ToArray() }, cancellationToken: ct));
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Não foi possível remover o login não confirmado do tenant {TenantId}.", tenantId);
                    }

                    logger.LogInformation("Cadastro não confirmado do tenant {TenantId} removido após 48h.", tenantId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Falha ao remover cadastro não confirmado do tenant {TenantId}.", tenantId);
                }
            }
        }

        private async Task ConverterTrialAsync(Guid tenantId, CancellationToken ct)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var faturamentoService = scope.ServiceProvider.GetRequiredService<IFaturamentoService>();
                var billingNotificationService = scope.ServiceProvider.GetRequiredService<IBillingNotificationService>();

                var resultado = await faturamentoService.IniciarCobrancaTrialAsync(tenantId, ct);
                if (!resultado.Sucesso)
                {
                    logger.LogWarning("Falha ao converter trial vencido do tenant {TenantId}: {Mensagem}", tenantId, resultado.MensagemErro);
                    return;
                }

                await billingNotificationService.EnviarInstrucoesPagamentoAsync(tenantId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro inesperado convertendo trial vencido do tenant {TenantId}.", tenantId);
            }
        }
    }
}
