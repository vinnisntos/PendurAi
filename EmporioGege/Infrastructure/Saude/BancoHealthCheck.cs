using Dapper;
using EmporioGege.Core.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EmporioGege.Infrastructure.Saude
{
    // Usado pelo deploy: a home pode responder 200 mesmo com o banco fora do ar.
    public class BancoHealthCheck(IDbConnectionFactory connectionFactory) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        {
            try
            {
                await using var conexao = await connectionFactory.CreateOpenConnectionAsync(ct);
                await conexao.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
                return HealthCheckResult.Healthy();
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Banco indisponível", ex);
            }
        }
    }
}
