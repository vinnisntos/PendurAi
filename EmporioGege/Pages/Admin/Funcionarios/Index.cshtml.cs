using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Pages.Admin.Funcionarios
{
    [Authorize(Policy = "AdminOnly")]
    public class IndexModel(IFuncionarioService funcionarioService) : PageModel
    {
        public IReadOnlyList<FuncionarioDto> Funcionarios { get; private set; } = [];

        public Guid UsuarioAtualId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        [TempData]
        public string? MensagemErro { get; set; }

        [TempData]
        public string? MensagemSucesso { get; set; }

        public async Task OnGetAsync(CancellationToken ct)
        {
            Funcionarios = await funcionarioService.ListarAsync(ct);
        }

        public async Task<IActionResult> OnPostDefinirAtivoAsync(Guid id, bool ativo, CancellationToken ct)
        {
            if (id == UsuarioAtualId)
            {
                MensagemErro = "Você não pode desativar o próprio acesso.";
                return RedirectToPage();
            }

            try
            {
                await funcionarioService.DefinirAtivoAsync(id, ativo, ct);
                MensagemSucesso = ativo ? "Acesso reativado." : "Acesso desativado. A pessoa sai do sistema em até 5 minutos.";
            }
            catch (InvalidOperationException ex)
            {
                MensagemErro = ex.Message;
            }
            return RedirectToPage();
        }
    }
}
