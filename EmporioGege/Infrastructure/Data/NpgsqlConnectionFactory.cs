using System.Data.Common;
using Dapper;
using EmporioGege.Core.Interfaces;
using Npgsql;

namespace EmporioGege.Infrastructure.Data
{
    // ATENÇÃO RLS: esta connection string conecta como o usuário 'postgres' do Supabase
    // (owner/superuser), que o Postgres sempre isenta de Row Level Security — as policies
    // criadas no Supabase Studio simplesmente não se aplicam aqui. Por isso, o isolamento
    // multi-tenant real desta camada depende 100% do @tenantId explícito em toda cláusula
    // WHERE dos repositórios/serviços (já aplicado no EstoqueService e no CaixaLedgerService).
    // O SET LOCAL app.current_tenant_id abaixo é defesa-em-profundidade / forward-compat:
    // se um dia a app passar a conectar com uma role restrita (não-owner) sujeita a RLS,
    // policies como `USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid)`
    // já vão funcionar sem mudar o código dos serviços.
    public class NpgsqlConnectionFactory(IConfiguration configuration) : IDbConnectionFactory
    {
        private readonly string _connectionString = LimitarPool(configuration.GetConnectionString("SupabasePostgres")
            ?? throw new InvalidOperationException("ConnectionStrings:SupabasePostgres não configurada."));

        // O pooler do Supabase em modo sessão aceita ~15 conexões; o padrão do Npgsql (100) estourava
        // isso sob carga e derrubava todas as lojas com 500. Só aplica se a string não definir um teto.
        private static string LimitarPool(string connectionString)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (!connectionString.Contains("Pool Size", StringComparison.OrdinalIgnoreCase))
                builder.MaxPoolSize = 10;
            return builder.ConnectionString;
        }

        public async Task<DbConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
        {
            var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            return connection;
        }

        // SET LOCAL não aceita parâmetros bind (@p) — só literais. Seguro aqui porque
        // tenantId é um Guid validado pelo runtime (nunca uma string bruta vinda do usuário),
        // logo não há superfície de SQL injection.
        public Task SetTenantContextAsync(DbConnection connection, DbTransaction transaction, Guid tenantId, CancellationToken ct = default) =>
            connection.ExecuteAsync(new CommandDefinition(
                $"SET LOCAL app.current_tenant_id = '{tenantId}'",
                transaction: transaction,
                cancellationToken: ct));
    }
}
