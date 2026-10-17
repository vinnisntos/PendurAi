using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Application.Services
{
    public class FuncionarioService(IDbConnectionFactory connectionFactory, ITenantProvider tenantProvider, IConfiguration configuration) : IFuncionarioService
    {
        public async Task<IReadOnlyList<FuncionarioDto>> ListarAsync(CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhas = await connection.QueryAsync<FuncionarioDto>(new CommandDefinition(
                """
                SELECT p.id AS Id, p.nome AS Nome, p.role AS Role, u.email AS Email, p.ativo AS Ativo
                FROM profiles p
                JOIN auth.users u ON u.id = p.id
                WHERE p.tenant_id = @TenantId
                ORDER BY p.nome
                """,
                new { TenantId = tenantId }, cancellationToken: ct));

            return linhas.AsList();
        }

        // Desliga sem apagar: as vendas e movimentações continuam apontando pro profile.
        public async Task DefinirAtivoAsync(Guid id, bool ativo, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhas = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE profiles SET ativo = @Ativo, updated_at = now() WHERE id = @Id AND tenant_id = @TenantId AND role <> 'superadmin'",
                new { Ativo = ativo, Id = id, TenantId = tenantId }, cancellationToken: ct));
            if (linhas == 0)
                throw new InvalidOperationException("Funcionário não encontrado.");
        }

        public async Task CriarAsync(CriarFuncionarioDto dto, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            // Cliente Supabase isolado — NÃO o injetado por DI (esse é Scoped e, se já tiver
            // sessão do admin logado carregada no meio da requisição, um SignUp nele trocaria
            // a sessão ativa pela do funcionário recém-criado). Instância própria e descartável.
            var url = configuration["Supabase:Url"]!;
            var key = configuration["Supabase:Key"]!;
            var clienteIsolado = new Supabase.Client(url, key, new Supabase.SupabaseOptions { AutoConnectRealtime = false });

            var sessao = await clienteIsolado.Auth.SignUp(dto.Email, dto.Senha);
            if (sessao?.User?.Id is null)
                throw new InvalidOperationException("Não foi possível criar o usuário — verifique se o e-mail já está cadastrado ou se a senha é forte o suficiente.");

            var novoUsuarioId = Guid.Parse(sessao.User.Id);

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO profiles (id, tenant_id, nome, role, updated_at)
                VALUES (@Id, @TenantId, @Nome, @Role, now())
                """,
                new { Id = novoUsuarioId, TenantId = tenantId, dto.Nome, dto.Role }, cancellationToken: ct));
        }

        public async Task AtualizarAsync(Guid id, string nome, string role, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhasAfetadas = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE profiles SET nome = @Nome, role = @Role, updated_at = now() WHERE id = @Id AND tenant_id = @TenantId",
                new { Nome = nome, Role = role, Id = id, TenantId = tenantId }, cancellationToken: ct));

            if (linhasAfetadas == 0)
                throw new InvalidOperationException($"Funcionário {id} não encontrado para o tenant {tenantId}.");
        }
    }
}
