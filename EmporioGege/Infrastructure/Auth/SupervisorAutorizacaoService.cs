using EmporioGege.Core.Interfaces;
using EmporioGege.Models;

namespace EmporioGege.Infrastructure.Auth
{
    // Autorização pontual ("supervisor override" de PDV): valida as credenciais de um
    // administrador/superadmin pra liberar uma ação sensível que o caixa (vendedor) não
    // pode fazer sozinho, sem trocar a sessão/cookie de quem está logado no terminal.
    // Usa exatamente o mesmo mecanismo do login (Pages/Auth/Login.cshtml.cs): SignIn no
    // Supabase Auth + leitura do Profile via Postgrest, tudo dentro do mesmo request —
    // não é uma leitura "fora do request de login" que o cliente Supabase não suportaria.
    public class SupervisorAutorizacaoService(
        Supabase.Client supabase, ITenantProvider tenantProvider,
        SupervisorTentativaLimiter tentativaLimiter, ILogger<SupervisorAutorizacaoService> logger) : ISupervisorAutorizacaoService
    {
        public async Task<bool> AutorizarAsync(string solicitanteId, string email, string senha, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(senha))
                return false;

            if (!tentativaLimiter.PodeTentar(solicitanteId))
            {
                logger.LogWarning("Limite de tentativas de autorização de supervisor excedido para {SolicitanteId}.", solicitanteId);
                return false;
            }

            try
            {
                var sessao = await supabase.Auth.SignIn(email, senha);
                if (sessao?.User is null)
                    return false;

                var perfil = await supabase.From<Profile>().Where(x => x.Id == sessao.User.Id).Single();
                if (perfil is null || !perfil.Ativo) // admin desligado não autoriza mais nada
                    return false;

                if (perfil.Role == "superadmin")
                    return true;

                if (perfil.Role != "administrador")
                    return false;

                var tenantAtual = tenantProvider.TenantId;
                return tenantAtual is not null && perfil.TenantId == tenantAtual.Value.ToString();
            }
            catch
            {
                // Credencial inválida, usuário sem perfil, ou falha de rede com o Supabase Auth
                // — em qualquer caso, nunca autoriza por causa de uma exceção não tratada.
                return false;
            }
        }
    }
}
