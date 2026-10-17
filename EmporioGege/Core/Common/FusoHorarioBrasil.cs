namespace EmporioGege.Core.Common
{
    /// <summary>
    /// Converte entre o dia/hora local da loja (Brasil) e UTC (usado em toda coluna
    /// timestamp do banco). Offset fixo de -3h em vez de TimeZoneInfo por horário de
    /// verão: o Brasil não observa DST desde 2019, e um offset fixo evita depender de
    /// IDs de fuso horário do SO, que divergem entre Windows ("E. South America
    /// Standard Time") e Linux ("America/Sao_Paulo").
    /// </summary>
    public static class FusoHorarioBrasil
    {
        public static readonly TimeSpan Offset = TimeSpan.FromHours(-3);

        public static DateTime AgoraLocal() => DateTime.UtcNow + Offset;

        public static DateOnly HojeLocal() => DateOnly.FromDateTime(AgoraLocal());

        /// <summary>Converte um instante do banco (UTC) pro horário da loja. Substitui ToLocalTime(),
        /// que depende do fuso do servidor (UTC na VPS) e mostrava tudo 3h adiantado.</summary>
        public static DateTime ParaLocal(DateTime instante) =>
            (instante.Kind == DateTimeKind.Local ? instante.ToUniversalTime() : DateTime.SpecifyKind(instante, DateTimeKind.Utc)) + Offset;

        /// <summary>Início (00:00) de um dia local, convertido pra UTC.</summary>
        public static DateTime InicioDoDiaLocalEmUtc(DateOnly diaLocal) =>
            DateTime.SpecifyKind(diaLocal.ToDateTime(TimeOnly.MinValue) - Offset, DateTimeKind.Utc);

        /// <summary>Segunda-feira da semana (local) de um dia qualquer - semana corrida
        /// segunda a domingo, mesmo padrão comercial usado no Brasil.</summary>
        public static DateOnly InicioDaSemanaLocal(DateOnly diaLocal)
        {
            var diasDesdeSegunda = ((int)diaLocal.DayOfWeek + 6) % 7; // domingo=0 vira 6, segunda=1 vira 0...
            return diaLocal.AddDays(-diasDesdeSegunda);
        }
    }
}
