namespace EmporioGege.Application.DTOs
{
    public record ResultadoFechamentoTurnoDto(
        Guid TurnoId,
        decimal SaldoInicial,
        decimal TotalCreditos,
        decimal TotalDebitos,
        decimal SaldoSistema,
        decimal SaldoInformado,
        decimal Diferenca,
        bool LancamentosIntegros = true);
}
