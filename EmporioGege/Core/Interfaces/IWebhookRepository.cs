using EmporioGege.Core.Entities;
using EmporioGege.Core.Enums;

namespace EmporioGege.Core.Interfaces
{
    // ATENÇÃO: ao contrário dos outros repositórios/serviços desta app, este NÃO filtra
    // por ITenantProvider — ele é o próprio mecanismo que resolve qual tenant corresponde
    // a um token de URL (equivalente ao papel do Login para sessões de usuário) e a
    // varredura de pendentes é uma rotina de manutenção interna que roda para todos os
    // tenants (BackgroundService, não uma requisição de um tenant específico).
    public interface IWebhookRepository
    {
        Task<IntegracaoWebhook?> BuscarIntegracaoAtivaPorTokenAsync(string tokenUrl, CancellationToken ct = default);

        // Retorna null quando já existe um registro para (tenantId, canal, eventoExternoId) —
        // é o mecanismo de idempotência contra reentrega do mesmo evento pelo parceiro.
        Task<Guid?> RegistrarRecebimentoAsync(Guid tenantId, CanalIntegracao canal, string eventoExternoId, string payloadJson, CancellationToken ct = default);

        Task<IReadOnlyList<Guid>> ListarPendentesAsync(CancellationToken ct = default);
        Task<int> RecolocarInterrompidosAsync(CancellationToken ct = default);

        // Claim atômico (UPDATE ... WHERE status = 'RECEBIDO') — evita que o catch-up de
        // startup e o leitor da fila em memória processem o mesmo evento em paralelo e
        // dupliquem a baixa de estoque. Retorna false se outro worker já reivindicou.
        Task<bool> TentarReivindicarAsync(Guid id, CancellationToken ct = default);

        Task<WebhookRecebido?> ObterAsync(Guid id, CancellationToken ct = default);

        Task MarcarProcessadoAsync(Guid id, Guid tenantId, CancellationToken ct = default);

        Task MarcarErroAsync(Guid id, Guid tenantId, string erro, CancellationToken ct = default);
    }
}
