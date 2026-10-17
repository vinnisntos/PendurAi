using EmporioGege.Application.Services;
using EmporioGege.Core.Entities;
using EmporioGege.Core.Enums;

namespace EmporioGege.Tests;

public class LedgerTests
{
    private static readonly Guid Tenant = Guid.NewGuid(), Caixa = Guid.NewGuid();

    // Monta uma cadeia como o sistema grava (horário em 100ns) e devolve como o banco relê (microssegundos).
    private static List<CaixaLedgerEntry> Cadeia(params decimal[] valores)
    {
        var lista = new List<CaixaLedgerEntry>();
        string? anterior = null;
        var t = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        foreach (var v in valores)
        {
            var e = new CaixaLedgerEntry { TenantId = Tenant, CaixaId = Caixa, Valor = v, TipoOperacao = TipoOperacaoLedger.Credito, CriadoEm = t, HashAnterior = anterior };
            e.HashVerificacao = LedgerOperacoes.CalcularHash(e);
            anterior = e.HashVerificacao;
            e.CriadoEm = e.CriadoEm.AddTicks(-(e.CriadoEm.Ticks % 10));
            lista.Add(e);
            t = t.AddSeconds(3).AddTicks(7);
        }
        return lista;
    }

    [Fact]
    public void Cadeia_intacta_confere() => Assert.True(LedgerOperacoes.CadeiaIntegra(Cadeia(10m, 5m, 2.5m)));

    [Fact]
    public void Valor_alterado_e_detectado()
    {
        var c = Cadeia(10m, 5m, 2.5m);
        c[1].Valor = 500m;
        Assert.False(LedgerOperacoes.CadeiaIntegra(c));
    }

    [Fact]
    public void Lancamento_apagado_no_meio_e_detectado()
    {
        var c = Cadeia(10m, 5m, 2.5m);
        c.RemoveAt(1);
        Assert.False(LedgerOperacoes.CadeiaIntegra(c));
    }

    [Fact]
    public void Caixa_sem_lancamentos_confere() => Assert.True(LedgerOperacoes.CadeiaIntegra([]));
}
