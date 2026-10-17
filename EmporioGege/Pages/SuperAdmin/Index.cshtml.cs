using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Authorization;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Pages.SuperAdmin
{
    // Segurança máxima: Só entra quem tem a role superadmin definida na policy do Program.cs
    [Authorize(Policy = "SuperAdminOnly")]
    public class IndexModel(ITenantService tenantService, ILogger<IndexModel> logger) : PageModel
    {
        public int TotalLojas { get; set; }
        public int TotalUsuarios { get; set; }
        public int LicencasAtivas { get; set; }
        public decimal FaturamentoGlobal { get; set; }
        public IReadOnlyList<TenantDto> Lojas { get; set; } = [];

        // Trial vencido que o TrialExpiradoProcessor não conseguiu converter em cobrança (ex.: Asaas
        // recusou os dados). Sem esta lista ninguém ficava sabendo e a loja ficava travada.
        public IReadOnlyList<TenantDto> TrialsSemCobranca { get; set; } = [];

        public async Task OnGetAsync(CancellationToken ct)
        {
            try
            {
                var lojas = await tenantService.ListarAsync(ct);
                Lojas = lojas;
                TotalLojas = lojas.Count;
                LicencasAtivas = lojas.Count(l => l.StatusLicenca == "ativo");
                TrialsSemCobranca = lojas.Where(l => l.StatusLicenca == "trial" && l.DataExpiracao < DateTime.UtcNow.AddHours(-2)).ToList();
                TotalUsuarios = await tenantService.ContarUsuariosTotalAsync(ct);
                FaturamentoGlobal = await tenantService.ObterFaturamentoGlobalAsync(ct);
            }
            catch (Exception ex)
            {
                // Nunca derrubar a tela do superadmin por uma falha pontual de leitura -
                // mas logar de verdade, senão o erro passa em branco (mesmo problema do bug #9).
                logger.LogError(ex, "Falha ao carregar o dashboard geral (God Mode) do superadmin.");
            }
        }
    }
}
