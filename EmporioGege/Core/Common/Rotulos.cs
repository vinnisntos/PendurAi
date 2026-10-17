namespace EmporioGege.Core.Common
{
    public static class Rotulos
    {
        public static string FormaPagamento(string? codigo) => codigo?.ToUpperInvariant() switch
        {
            "DINHEIRO" => "Dinheiro",
            "DEBITO" => "Débito",
            "CREDITO" => "Crédito",
            "PIX" => "Pix",
            "FIADO" => "Fiado",
            null or "" => "-",
            _ => codigo
        };
    }
}
