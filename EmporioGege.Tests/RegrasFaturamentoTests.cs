using EmporioGege.Application.Services;
using EmporioGege.Core.Common;
using EmporioGege.Core.Security;

namespace EmporioGege.Tests;

public class CnpjTests
{
    [Theory]
    [InlineData("11.222.333/0001-81")]
    [InlineData("11222333000181")]
    public void Aceita_cnpj_com_digitos_corretos(string cnpj) => Assert.True(Cnpj.EhValido(cnpj));

    [Theory]
    [InlineData("11.222.333/0001-82")]
    [InlineData("00.000.000/0000-00")]
    [InlineData("1122233300018")]
    [InlineData("")]
    [InlineData(null)]
    public void Recusa_cnpj_invalido(string? cnpj) => Assert.False(Cnpj.EhValido(cnpj));
}

public class CoberturaAssinaturaTests
{
    [Fact]
    public void Pagamento_cobre_o_mes_seguinte_mais_cinco_dias()
    {
        Assert.Equal(new DateTime(2026, 11, 15), AsaasWebhookService.CoberturaAte(new DateOnly(2026, 10, 10)));
    }

    [Fact]
    public void Atraso_de_fatura_antiga_fica_coberto_pelo_pagamento_seguinte()
    {
        // Pagou a fatura de novembro; um PAYMENT_OVERDUE atrasado da de outubro não pode suspender.
        var expiracaoAtual = AsaasWebhookService.CoberturaAte(new DateOnly(2026, 11, 10));
        Assert.True(AsaasWebhookService.CoberturaAte(new DateOnly(2026, 10, 10)) < expiracaoAtual);
    }
}

public class TokenFixoValidatorTests
{
    [Fact]
    public void Compara_token_exato() => Assert.True(TokenFixoValidator.IsValid("abc123", "abc123"));

    [Theory]
    [InlineData("abc124")]
    [InlineData("")]
    [InlineData(null)]
    public void Recusa_token_diferente(string? recebido) => Assert.False(TokenFixoValidator.IsValid(recebido, "abc123"));
}

public class FusoHorarioBrasilTests
{
    [Fact]
    public void Inicio_do_dia_local_e_tres_da_manha_em_utc()
    {
        var inicio = FusoHorarioBrasil.InicioDoDiaLocalEmUtc(new DateOnly(2026, 7, 15));
        Assert.Equal(new DateTime(2026, 7, 15, 3, 0, 0, DateTimeKind.Utc), inicio);
        Assert.Equal(DateTimeKind.Utc, inicio.Kind);
    }

    [Theory]
    [InlineData(2026, 10, 18, 2026, 10, 12)] // domingo -> segunda anterior
    [InlineData(2026, 10, 12, 2026, 10, 12)] // segunda -> ela mesma
    public void Semana_comeca_na_segunda(int a, int m, int d, int ea, int em, int ed) =>
        Assert.Equal(new DateOnly(ea, em, ed), FusoHorarioBrasil.InicioDaSemanaLocal(new DateOnly(a, m, d)));
}

public class HmacSignatureValidatorTests
{
    private static readonly byte[] Corpo = System.Text.Encoding.UTF8.GetBytes("{\"orderId\":\"1\"}");

    private static string Assinar(string segredo) =>
        Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(segredo), Corpo));

    [Fact]
    public void Aceita_assinatura_com_ou_sem_prefixo()
    {
        Assert.True(HmacSignatureValidator.IsValid(Corpo, Assinar("s3gredo"), "s3gredo"));
        Assert.True(HmacSignatureValidator.IsValid(Corpo, "sha256=" + Assinar("s3gredo").ToUpperInvariant(), "s3gredo"));
    }

    [Fact]
    public void Recusa_assinatura_de_outro_segredo() =>
        Assert.False(HmacSignatureValidator.IsValid(Corpo, Assinar("outro"), "s3gredo"));
}

public class ParaLocalTests
{
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)] // timestamp sem fuso vindo do Npgsql
    public void Converte_utc_para_horario_de_brasilia(DateTimeKind kind) =>
        Assert.Equal(new DateTime(2026, 7, 14, 23, 37, 0), FusoHorarioBrasil.ParaLocal(new DateTime(2026, 7, 15, 2, 37, 0, kind)));
}
