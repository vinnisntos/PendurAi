using EmporioGege.Application.Services;

namespace EmporioGege.Tests;

public class CardapioOnlineTests
{
    [Fact]
    public void Detecta_tipo_da_foto_pelo_conteudo()
    {
        Assert.Equal("image/jpeg", CardapioOnlineService.DetectarTipoImagem([0xFF, 0xD8, 0xFF, 0xE0, 0x00]));
        Assert.Equal("image/png", CardapioOnlineService.DetectarTipoImagem([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]));
        Assert.Equal("image/webp", CardapioOnlineService.DetectarTipoImagem("RIFF\0\0\0\0WEBPVP8 "u8));
    }

    [Fact]
    public void Recusa_arquivo_que_nao_e_imagem()
    {
        Assert.Null(CardapioOnlineService.DetectarTipoImagem("<svg onload=alert(1)>"u8));
        Assert.Null(CardapioOnlineService.DetectarTipoImagem([]));
    }

    [Theory]
    [InlineData("bolos-da-gabi", "bolos-da-gabi")]
    [InlineData("  Bolos da Gabi ", "bolos-da-gabi")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normaliza_slug(string? entrada, string? esperado) =>
        Assert.Equal(esperado, CardapioOnlineService.NormalizarSlug(entrada));

    [Theory]
    [InlineData("ab")]
    [InlineData("-bolos")]
    [InlineData("bolos/../admin")]
    [InlineData("bolos_gabi")]
    public void Recusa_slug_invalido(string entrada) =>
        Assert.Throws<ArgumentException>(() => CardapioOnlineService.NormalizarSlug(entrada));

    [Theory]
    [InlineData("(15) 99999-1234", "5515999991234")]
    [InlineData("+55 15 99999-1234", "5515999991234")]
    [InlineData("1533334444", "551533334444")]
    public void Normaliza_whatsapp_para_o_wa_me(string entrada, string esperado) =>
        Assert.Equal(esperado, CardapioOnlineService.NormalizarWhatsApp(entrada));

    [Theory]
    [InlineData("99999")]
    [InlineData("+44 20 7946 0958")]
    public void Recusa_whatsapp_invalido(string entrada) =>
        Assert.Throws<ArgumentException>(() => CardapioOnlineService.NormalizarWhatsApp(entrada));
}
