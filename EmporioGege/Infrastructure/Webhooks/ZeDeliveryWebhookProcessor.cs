using System.Text.Json;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Enums;
using EmporioGege.Core.Interfaces;
using EmporioGege.Infrastructure.Tenancy;

namespace EmporioGege.Infrastructure.Webhooks
{
    public class ZeDeliveryWebhookProcessor(
        IServiceScopeFactory scopeFactory,
        IWebhookQueue webhookQueue,
        AmbientTenantContext ambientTenantContext,
        ILogger<ZeDeliveryWebhookProcessor> logger) : BackgroundService
    {
        private static readonly TimeSpan IntervaloVarreduraSeguranca = TimeSpan.FromSeconds(30);
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Recuperação de processo reiniciado: qualquer evento que ficou em RECEBIDO
            // (ex.: app caiu antes de processar) é pego aqui, não só pelos novos sinais da fila.
            try
            {
                using var escopo = scopeFactory.CreateScope();
                var recolocados = await escopo.ServiceProvider.GetRequiredService<IWebhookRepository>().RecolocarInterrompidosAsync(stoppingToken);
                if (recolocados > 0)
                    logger.LogWarning("{Quantidade} webhook(s) interrompido(s) por reinício voltaram para a fila.", recolocados);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao recolocar webhooks interrompidos.");
            }
            await ProcessarPendentesAsync(stoppingToken);

            var leituraFila = LerFilaEmBackgroundAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(IntervaloVarreduraSeguranca, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Rede de segurança: cobre o caso raro de um Sinalizar() perdido (fila cheia)
                // ou uma reivindicação anterior que falhou antes de concluir.
                await ProcessarPendentesAsync(stoppingToken);
            }

            await leituraFila;
        }

        private async Task LerFilaEmBackgroundAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var id in webhookQueue.LerAsync(ct))
                {
                    await ProcessarItemAsync(id, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Encerramento normal do host.
            }
        }

        private async Task ProcessarPendentesAsync(CancellationToken ct)
        {
            using var scope = scopeFactory.CreateScope();
            var repositorio = scope.ServiceProvider.GetRequiredService<IWebhookRepository>();

            IReadOnlyList<Guid> pendentes;
            try
            {
                pendentes = await repositorio.ListarPendentesAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao consultar webhooks pendentes.");
                return;
            }

            foreach (var id in pendentes)
                await ProcessarItemAsync(id, ct);
        }

        private async Task ProcessarItemAsync(Guid webhookId, CancellationToken ct)
        {
            // Rede de segurança externa: NENHUMA exceção pode escapar deste método. Um
            // BackgroundService com exceção não tratada em ExecuteAsync derruba a aplicação
            // inteira por padrão (BackgroundServiceExceptionBehavior.StopHost) — um erro
            // transitório processando UM webhook (ex.: banco indisponível por um instante)
            // não pode tirar login/PDV/tudo do ar junto.
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repositorio = scope.ServiceProvider.GetRequiredService<IWebhookRepository>();

                // Claim atômico: se outro caminho (catch-up vs. sinal da fila) já reivindicou
                // este evento, TentarReivindicarAsync retorna false e paramos aqui — sem isso
                // dois workers poderiam processar o mesmo pedido e baixar o estoque em dobro.
                if (!await repositorio.TentarReivindicarAsync(webhookId, ct))
                    return;

                var registro = await repositorio.ObterAsync(webhookId, ct);
                if (registro is null)
                {
                    logger.LogError("Webhook {WebhookId} reivindicado mas não encontrado ao carregar.", webhookId);
                    return;
                }

                using var escopoTenant = ambientTenantContext.BeginScope(registro.TenantId);

                try
                {
                    var payload = JsonSerializer.Deserialize<ZeDeliveryWebhookPayload>(registro.Payload, JsonOptions)
                        ?? throw new InvalidOperationException("Payload não pôde ser desserializado.");

                    // Via Dapper (não pelo cliente Supabase/Postgrest): o Client dentro deste
                    // BackgroundService nunca passa por login/SignIn, então rodaria sempre como
                    // anon — a busca por SKU nunca encontraria nada, mesmo com o produto existindo.
                    var catalogoService = scope.ServiceProvider.GetRequiredService<ICatalogoService>();
                    var vendaService = scope.ServiceProvider.GetRequiredService<IVendaService>();

                    // Resolve todos os SKUs antes de chamar o VendaService: se um item não
                    // bate com nenhum produto, falhamos o evento inteiro (fica ERRO, visível
                    // pra conferência manual) sem sequer tentar baixar estoque.
                    var itensParaVenda = new List<ItemVendaDto>();
                    foreach (var item in payload.Order.Items)
                    {
                        var produtoId = await catalogoService.BuscarProdutoIdPorCodigoBarrasAsync(item.Sku, ct)
                            ?? throw new InvalidOperationException(
                                $"SKU '{item.Sku}' do pedido {payload.Order.OrderId} não corresponde a nenhum produto cadastrado (tenant {registro.TenantId}).");

                        itensParaVenda.Add(new ItemVendaDto(produtoId, item.Quantity));
                    }

                    // TurnoId nulo: pedido de delivery não pertence a um turno de caixa físico,
                    // então não gera crédito no ledger (o dinheiro não passa pela gaveta desta loja).
                    await vendaService.FinalizarVendaAsync(
                        new FinalizarVendaDto(null, itensParaVenda, TipoOrigemVenda.ZeDelivery, false, null, ReferenciaExterna: payload.Order.OrderId), ct);

                    await repositorio.MarcarProcessadoAsync(webhookId, registro.TenantId, ct);
                }
                catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505" && ex.ConstraintName == "ux_vendas_referencia_externa")
                {
                    // O pedido já virou venda (reentrega ou evento de mudança de status do mesmo pedido).
                    logger.LogInformation("Pedido do webhook {WebhookId} já registrado como venda; evento ignorado.", webhookId);
                    await repositorio.MarcarProcessadoAsync(webhookId, registro.TenantId, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Falha ao processar webhook {WebhookId} do tenant {TenantId}.", webhookId, registro.TenantId);
                    try
                    {
                        await repositorio.MarcarErroAsync(webhookId, registro.TenantId, ex.Message, ct);
                    }
                    catch (Exception exMarcar)
                    {
                        // Se nem isso funcionar, o registro fica preso em PROCESSANDO (não
                        // reaparece em ListarPendentesAsync, que só olha status RECEBIDO) até
                        // intervenção manual — limitação conhecida, mas não derruba o host.
                        logger.LogError(exMarcar, "Falha ao marcar webhook {WebhookId} como ERRO.", webhookId);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro inesperado processando webhook {WebhookId}.", webhookId);
            }
        }
    }
}
