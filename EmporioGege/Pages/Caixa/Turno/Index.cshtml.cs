using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Enums;
using EmporioGege.Core.Exceptions;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Pages.Caixa.Turno
{
    [Authorize(Policy = "CaixaOnly")]
    public class IndexModel(ITurnoService turnoService, ICaixaLedgerService caixaLedgerService, ISupervisorAutorizacaoService supervisorAutorizacao) : PageModel
    {
        public TurnoAtualDto? TurnoAtual { get; private set; }

        public IReadOnlyList<LancamentoTurnoDto> Lancamentos { get; private set; } = [];

        public ResultadoFechamentoTurnoDto? ResultadoFechamento { get; private set; }

        [TempData]
        public string? MensagemErro { get; set; }

        [TempData]
        public string? MensagemSucesso { get; set; }

        [BindProperty]
        [Range(0, double.MaxValue, ErrorMessage = "Saldo inicial não pode ser negativo.")]
        public decimal SaldoInicialInput { get; set; }

        [BindProperty]
        [Range(0.01, double.MaxValue, ErrorMessage = "Informe um valor maior que zero.")]
        public decimal ValorLancamento { get; set; }

        [BindProperty]
        public string TipoLancamento { get; set; } = "SANGRIA";

        // string? (não string): com Nullable ligado, string não-nula vira obrigatória em todo POST da página.
        [BindProperty]
        public string? SupervisorEmail { get; set; }

        [BindProperty]
        public string? SupervisorSenha { get; set; }

        public bool ExigeSupervisor => User.IsInRole("vendedor");

        [BindProperty]
        [Required(ErrorMessage = "Informe o motivo do lançamento.")]
        [MinLength(3, ErrorMessage = "Motivo muito curto.")]
        public string MotivoLancamento { get; set; } = default!;

        [BindProperty]
        [Range(0, double.MaxValue, ErrorMessage = "Saldo informado não pode ser negativo.")]
        public decimal SaldoInformadoInput { get; set; }

        private Guid UsuarioId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        public async Task OnGetAsync(CancellationToken ct)
        {
            await CarregarEstadoAsync(ct);
        }

        public async Task<IActionResult> OnPostAbrirAsync(CancellationToken ct)
        {
            // Checa só o campo desse handler - o PageModel tem 3 forms/handlers (Abrir,
            // Lancamento, Fechar) compartilhando [BindProperty]; ModelState.IsValid geral
            // reprovaria aqui por causa de campos de OUTRO form (ex.: MotivoLancamento
            // obrigatório), mesmo esse aqui estando correto.
            if (SaldoInicialInput < 0)
            {
                MensagemErro = "Informe um saldo inicial válido.";
                return RedirectToPage();
            }

            try
            {
                await turnoService.AbrirTurnoAsync(UsuarioId, SaldoInicialInput, ct);
                MensagemSucesso = "Turno aberto com sucesso.";
            }
            catch (TurnoInvalidoException ex)
            {
                MensagemErro = ex.Message;
            }

            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostLancamentoAsync(CancellationToken ct)
        {
            if (ValorLancamento <= 0 || string.IsNullOrWhiteSpace(MotivoLancamento) || MotivoLancamento.Trim().Length < 3)
            {
                MensagemErro = "Informe um valor e um motivo válidos para o lançamento.";
                return RedirectToPage();
            }

            var turnoAberto = await turnoService.ObterTurnoAbertoAsync(UsuarioId, ct);
            if (turnoAberto is null)
            {
                MensagemErro = "Não há turno aberto para registrar essa movimentação.";
                return RedirectToPage();
            }

            var tipoOperacao = TipoLancamento == "SUPRIMENTO" ? TipoOperacaoLedger.Credito : TipoOperacaoLedger.Debito;

            // Dinheiro saindo da gaveta pela mão do vendedor precisa de um administrador junto.
            if (tipoOperacao == TipoOperacaoLedger.Debito && ExigeSupervisor
                && (string.IsNullOrWhiteSpace(SupervisorEmail) || string.IsNullOrEmpty(SupervisorSenha)
                    || !await supervisorAutorizacao.AutorizarAsync(UsuarioId.ToString(), SupervisorEmail, SupervisorSenha, ct)))
            {
                MensagemErro = "Sangria precisa da autorização de um administrador (e-mail e senha).";
                return RedirectToPage();
            }

            // Sem teto, a sangria deixava o saldo do caixa negativo. A mensagem não diz quanto há na
            // gaveta, pra não quebrar a contagem cega do fechamento.
            if (tipoOperacao == TipoOperacaoLedger.Debito && ValorLancamento > await turnoService.ObterSaldoDinheiroAsync(turnoAberto.Id, ct))
            {
                MensagemErro = "A sangria é maior que o dinheiro registrado no caixa. Confira o valor.";
                return RedirectToPage();
            }

            await caixaLedgerService.AdicionarLancamentoAsync(
                new LancamentoLedgerDto(turnoAberto.Id, ValorLancamento, tipoOperacao, MotivoLancamento), ct);

            MensagemSucesso = TipoLancamento == "SUPRIMENTO" ? "Suprimento registrado no ledger." : "Sangria registrada no ledger.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostFecharAsync(CancellationToken ct)
        {
            var turnoAberto = await turnoService.ObterTurnoAbertoAsync(UsuarioId, ct);
            if (turnoAberto is null)
            {
                MensagemErro = "Não há turno aberto para fechar.";
                return RedirectToPage();
            }

            if (SaldoInformadoInput < 0)
            {
                MensagemErro = "Informe o valor contado no caixa para fechar o turno.";
                return RedirectToPage();
            }

            try
            {
                ResultadoFechamento = await turnoService.FecharTurnoAsync(turnoAberto.Id, SaldoInformadoInput, ct);
            }
            catch (TurnoInvalidoException ex)
            {
                MensagemErro = ex.Message;
                return RedirectToPage();
            }

            TurnoAtual = null;
            Lancamentos = [];
            return Page();
        }

        private async Task CarregarEstadoAsync(CancellationToken ct)
        {
            TurnoAtual = await turnoService.ObterTurnoAbertoAsync(UsuarioId, ct);
            if (TurnoAtual is not null)
                Lancamentos = await turnoService.ListarLancamentosAsync(TurnoAtual.Id, ct);
        }
    }
}
