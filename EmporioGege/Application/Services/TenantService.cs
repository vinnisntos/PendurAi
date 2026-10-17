using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Application.Services
{
    public class TenantService(IDbConnectionFactory connectionFactory) : ITenantService
    {
        private const string Selecao = """
            SELECT id AS Id, nome_fantasia AS NomeFantasia, nome_representante AS NomeRepresentante,
                   cpf_rg_dono AS CpfRgDono, cnpj AS Cnpj, cidade_estado AS CidadeEstado,
                   telefone_empresa AS TelefoneEmpresa, telefone_dono AS TelefoneDono,
                   email_empresa AS EmailEmpresa, email_dono AS EmailDono,
                   status_licenca AS StatusLicenca, data_expiracao AS DataExpiracao, created_at AS CreatedAt,
                   asaas_subscription_id AS AsaasSubscriptionId
            FROM tenants
            """;

        public async Task<IReadOnlyList<TenantDto>> ListarAsync(CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhas = await connection.QueryAsync<TenantDto>(new CommandDefinition(
                $"{Selecao} ORDER BY nome_fantasia", cancellationToken: ct));

            return linhas.AsList();
        }

        public async Task<TenantDto?> ObterAsync(Guid id, CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.QuerySingleOrDefaultAsync<TenantDto>(new CommandDefinition(
                $"{Selecao} WHERE id = @Id", new { Id = id }, cancellationToken: ct));
        }

        public async Task<Guid> SalvarAsync(SalvarTenantDto dto, CancellationToken ct = default)
        {
            var tenantId = dto.Id ?? Guid.NewGuid();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);

            if (dto.Id is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tenants (
                        id, nome_fantasia, nome_representante, cpf_rg_dono, cnpj, cidade_estado,
                        telefone_empresa, telefone_dono, email_empresa, email_dono, status_licenca,
                        data_expiracao, created_at
                    )
                    VALUES (
                        @Id, @NomeFantasia, @NomeRepresentante, @CpfRgDono, @Cnpj, @CidadeEstado,
                        @TelefoneEmpresa, @TelefoneDono, @EmailEmpresa, @EmailDono, @StatusLicenca,
                        @DataExpiracao, now()
                    )
                    """,
                    new
                    {
                        Id = tenantId, dto.NomeFantasia, dto.NomeRepresentante, dto.CpfRgDono, dto.Cnpj, dto.CidadeEstado,
                        dto.TelefoneEmpresa, dto.TelefoneDono, dto.EmailEmpresa, dto.EmailDono, dto.StatusLicenca, dto.DataExpiracao
                    }, cancellationToken: ct));
            }
            else
            {
                var linhasAfetadas = await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE tenants SET
                        nome_fantasia = @NomeFantasia, nome_representante = @NomeRepresentante,
                        cpf_rg_dono = @CpfRgDono, cnpj = @Cnpj, cidade_estado = @CidadeEstado,
                        telefone_empresa = @TelefoneEmpresa, telefone_dono = @TelefoneDono,
                        email_empresa = @EmailEmpresa, email_dono = @EmailDono,
                        status_licenca = @StatusLicenca, data_expiracao = @DataExpiracao
                    WHERE id = @Id
                    """,
                    new
                    {
                        Id = tenantId, dto.NomeFantasia, dto.NomeRepresentante, dto.CpfRgDono, dto.Cnpj, dto.CidadeEstado,
                        dto.TelefoneEmpresa, dto.TelefoneDono, dto.EmailEmpresa, dto.EmailDono, dto.StatusLicenca, dto.DataExpiracao
                    }, cancellationToken: ct));

                if (linhasAfetadas == 0)
                    throw new InvalidOperationException($"Loja {tenantId} não encontrada.");
            }

            return tenantId;
        }

        public async Task AtualizarStatusLicencaAsync(Guid id, string statusLicenca, CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhasAfetadas = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE tenants SET status_licenca = @StatusLicenca WHERE id = @Id",
                new { StatusLicenca = statusLicenca, Id = id }, cancellationToken: ct));

            if (linhasAfetadas == 0)
                throw new InvalidOperationException($"Loja {id} não encontrada.");
        }

        // Visão "God Mode" do superadmin: contagem/soma de TODOS os tenants, de propósito sem
        // filtro de tenant_id (mesma exceção documentada na interface).
        public async Task<int> ContarUsuariosTotalAsync(CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.QuerySingleAsync<int>(new CommandDefinition(
                "SELECT COUNT(*)::int FROM profiles", cancellationToken: ct));
        }

        public async Task<decimal> ObterFaturamentoGlobalAsync(CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.QuerySingleAsync<decimal>(new CommandDefinition(
                "SELECT COALESCE(SUM(total_venda), 0) FROM vendas WHERE status = 'FECHADA'", cancellationToken: ct));
        }
    }
}
