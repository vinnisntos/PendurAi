using System.Globalization;
using System.Text;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Common;

namespace EmporioGege.Infrastructure.Impressao
{
    // Monta a sequência de bytes ESC/POS pro recibo. Comandos testados manualmente contra
    // uma impressora térmica Bluetooth (MPT-III/Leopardo, perfil SPP, porta serial virtual)
    // - ESC @ (inicializa), ESC E 1/0 (negrito on/off) e ESC a n (alinhamento) confirmados
    // funcionando nela.
    internal static class EscPosReciboFormatter
    {
        private const byte Esc = 0x1B;

        public static byte[] Montar(ReciboVendaDto recibo, int colunas)
        {
            var buffer = new List<byte>();

            Inicializar(buffer);

            Centralizar(buffer, true);
            Negrito(buffer, true);
            EscreverLinha(buffer, recibo.NomeLoja);
            Negrito(buffer, false);
            Centralizar(buffer, false);

            EscreverLinha(buffer, FusoHorarioBrasil.ParaLocal(recibo.DataVenda).ToString("dd/MM/yyyy HH:mm"));
            if (recibo.NumeroComanda is { } numeroComanda)
                EscreverLinha(buffer, $"Comanda: {numeroComanda}");

            EscreverLinha(buffer, new string('-', colunas));

            foreach (var item in recibo.Itens)
            {
                EscreverLinha(buffer, Truncar(item.ProdutoNome, colunas));
                var esquerda = $"  {item.Quantidade}x {FormatarMoeda(item.PrecoUnitario)}";
                EscreverLinha(buffer, LinhaDuasColunas(esquerda, FormatarMoeda(item.Subtotal), colunas));
            }

            EscreverLinha(buffer, new string('-', colunas));

            Negrito(buffer, true);
            EscreverLinha(buffer, LinhaDuasColunas("TOTAL", FormatarMoeda(recibo.Total), colunas));
            Negrito(buffer, false);

            EscreverLinha(buffer, $"Pagamento: {recibo.MetodoPagamento}");
            EscreverLinha(buffer, "");

            Centralizar(buffer, true);
            EscreverLinha(buffer, "Obrigado pela preferencia!");
            Centralizar(buffer, false);

            // Sem comando de corte (GS V): a MPT-III é portátil, sem guilhotina - só avança
            // papel o bastante pro cliente rasgar na régua serrilhada.
            for (var i = 0; i < 4; i++)
                buffer.Add((byte)'\n');

            return [.. buffer];
        }

        private static void Inicializar(List<byte> buffer) => buffer.AddRange([Esc, (byte)'@']);

        private static void Negrito(List<byte> buffer, bool ligado) => buffer.AddRange([Esc, (byte)'E', (byte)(ligado ? 1 : 0)]);

        private static void Centralizar(List<byte> buffer, bool ligado) => buffer.AddRange([Esc, (byte)'a', (byte)(ligado ? 1 : 0)]);

        private static void EscreverLinha(List<byte> buffer, string texto)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes(RemoverAcentos(texto)));
            buffer.Add((byte)'\n');
        }

        private static string Truncar(string texto, int largura) => texto.Length <= largura ? texto : texto[..largura];

        private static string LinhaDuasColunas(string esquerda, string direita, int largura)
        {
            if (esquerda.Length + direita.Length >= largura)
                esquerda = esquerda[..Math.Max(0, largura - direita.Length - 1)];

            var espacos = Math.Max(1, largura - esquerda.Length - direita.Length);
            return esquerda + new string(' ', espacos) + direita;
        }

        private static string FormatarMoeda(decimal valor) =>
            valor.ToString("C", CultureInfo.GetCultureInfo("pt-BR"));

        // Encoding.ASCII puro derruba qualquer acento - a página de código da impressora
        // não é confiável sem configurar CP860/CP1252 explicitamente (fora de escopo aqui).
        // Melhor imprimir "Guarana" do que um caractere de lixo/interrogação no lugar do "ná".
        private static string RemoverAcentos(string texto)
        {
            var normalizado = texto.Normalize(NormalizationForm.FormD);
            var semAcento = new StringBuilder();
            foreach (var c in normalizado)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    semAcento.Append(c);
            }
            return semAcento.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
