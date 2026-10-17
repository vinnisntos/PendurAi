using System.Globalization;
using System.Security.Claims;
using Dapper;
using EmporioGege.Core.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace EmporioGege.Infrastructure.Auth
{
    // O cookie vale 8h: sem isto, loja suspensa pelo webhook, funcionário removido ou rebaixado
    // continuava operando até o cookie expirar. Revalida no banco a cada poucos minutos.
    public static class ValidacaoSessao
    {
        private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(5);
        private const string ChaveUltimaValidacao = "pa.validado_em";

        public static async Task ValidarAsync(CookieValidatePrincipalContext context)
        {
            var agora = DateTimeOffset.UtcNow;
            if (context.Properties.Items.TryGetValue(ChaveUltimaValidacao, out var texto)
                && DateTimeOffset.TryParse(texto, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ultima)
                && agora - ultima < Intervalo)
                return;

            var principal = context.Principal!;
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var usuarioId))
            {
                await RejeitarAsync(context);
                return;
            }

            var connectionFactory = context.HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>();
            SessaoRow? linha;
            try
            {
                await using var conexao = await connectionFactory.CreateOpenConnectionAsync(context.HttpContext.RequestAborted);
                linha = await conexao.QuerySingleOrDefaultAsync<SessaoRow>(new CommandDefinition(
                    """
                    SELECT p.role AS Role, p.tenant_id AS TenantId, t.status_licenca AS StatusLicenca, p.ativo AS Ativo
                    FROM profiles p LEFT JOIN tenants t ON t.id = p.tenant_id
                    WHERE p.id = @UsuarioId
                    """,
                    new { UsuarioId = usuarioId }, cancellationToken: context.HttpContext.RequestAborted));
            }
            catch (Exception ex)
            {
                // Banco fora do ar não derruba quem já está logado; tenta de novo na próxima requisição.
                context.HttpContext.RequestServices.GetRequiredService<ILogger<CookieAuthenticationEvents>>()
                    .LogWarning(ex, "Não foi possível revalidar a sessão do usuário {UsuarioId}.", usuarioId);
                return;
            }

            var bloqueada = linha is null
                || !linha.Ativo
                || linha.Role != principal.FindFirstValue(ClaimTypes.Role)
                || (linha.Role != "superadmin" && linha.StatusLicenca is "suspenso" or "cancelado" or "pendente");

            if (bloqueada)
            {
                await RejeitarAsync(context);
                return;
            }

            context.Properties.Items[ChaveUltimaValidacao] = agora.ToString("O", CultureInfo.InvariantCulture);
            context.ShouldRenew = true;
        }

        private static async Task RejeitarAsync(CookieValidatePrincipalContext context)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }

        private sealed record SessaoRow(string Role, Guid? TenantId, string? StatusLicenca, bool Ativo);
    }
}
