using System.Text;
using System.Xml.Linq;
using ClosedXML.Excel;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EmporioGege.Infrastructure.Relatorios
{
    public class RelatorioExportService : IRelatorioExportService
    {
        public byte[] ExportarXlsx(RelatorioTabularDto relatorio)
        {
            using var workbook = new XLWorkbook();
            var planilha = workbook.Worksheets.Add(SanitizarNomeAba(relatorio.Titulo));

            for (var c = 0; c < relatorio.Colunas.Count; c++)
            {
                var celula = planilha.Cell(1, c + 1);
                celula.Value = relatorio.Colunas[c];
                celula.Style.Font.Bold = true;
            }

            for (var l = 0; l < relatorio.Linhas.Count; l++)
            {
                var linha = relatorio.Linhas[l];
                for (var c = 0; c < linha.Count; c++)
                    planilha.Cell(l + 2, c + 1).Value = linha[c];
            }

            planilha.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return stream.ToArray();
        }

        public byte[] ExportarPdf(RelatorioTabularDto relatorio)
        {
            var documento = Document.Create(container =>
            {
                container.Page(pagina =>
                {
                    pagina.Size(PageSizes.A4.Landscape());
                    pagina.Margin(24);
                    pagina.DefaultTextStyle(estilo => estilo.FontSize(9));

                    pagina.Header().Text(relatorio.Titulo).FontSize(16).Bold();

                    pagina.Content().PaddingTop(10).Table(tabela =>
                    {
                        tabela.ColumnsDefinition(colunas =>
                        {
                            foreach (var _ in relatorio.Colunas)
                                colunas.RelativeColumn();
                        });

                        foreach (var coluna in relatorio.Colunas)
                            tabela.Cell().BorderBottom(1).Padding(4).Text(coluna).Bold();

                        foreach (var linha in relatorio.Linhas)
                        {
                            foreach (var valor in linha)
                                tabela.Cell().BorderBottom(0.5f).Padding(4).Text(valor);
                        }
                    });

                    pagina.Footer().AlignCenter().Text(texto =>
                    {
                        texto.Span("Gerado em ");
                        texto.Span(EmporioGege.Core.Common.FusoHorarioBrasil.AgoraLocal().ToString("dd/MM/yyyy HH:mm"));
                    });
                });
            });

            return documento.GeneratePdf();
        }

        public byte[] ExportarXml(RelatorioTabularDto relatorio)
        {
            var raiz = new XElement("Relatorio",
                new XAttribute("titulo", relatorio.Titulo),
                new XElement("Linhas",
                    relatorio.Linhas.Select(linha =>
                        new XElement("Linha",
                            linha.Select((valor, i) => new XElement(
                                SanitizarNomeElemento(relatorio.Colunas.ElementAtOrDefault(i) ?? $"Coluna{i}"), valor))))));

            using var stream = new MemoryStream();
            raiz.Save(stream);
            return stream.ToArray();
        }

        public byte[] ExportarCsv(RelatorioTabularDto relatorio)
        {
            // Separador ";" (não ",") - padrão pt-BR: o Excel em português usa "," como
            // separador decimal, então "," como separador de campo quebraria a leitura de
            // qualquer coluna numérica ao abrir o CSV direto.
            var construtor = new StringBuilder();
            construtor.AppendLine(string.Join(";", relatorio.Colunas.Select(EscaparCsv)));
            foreach (var linha in relatorio.Linhas)
                construtor.AppendLine(string.Join(";", linha.Select(EscaparCsv)));

            // BOM UTF-8: sem isso o Excel abre acento/cedilha quebrado num CSV puro.
            var preambulo = Encoding.UTF8.GetPreamble();
            var corpo = Encoding.UTF8.GetBytes(construtor.ToString());
            return [.. preambulo, .. corpo];
        }

        private static string EscaparCsv(string valor)
        {
            if (valor.Contains(';') || valor.Contains('"') || valor.Contains('\n'))
                return $"\"{valor.Replace("\"", "\"\"")}\"";
            return valor;
        }

        // Excel proíbe / \ ? * [ ] : no nome da aba (ex.: títulos com datas "dd/MM/yyyy"
        // quebravam aqui) e limita a 31 caracteres - sanitiza antes de truncar.
        private static readonly char[] CaracteresInvalidosNomeAba = ['/', '\\', '?', '*', '[', ']', ':'];

        private static string SanitizarNomeAba(string titulo)
        {
            var limpo = new string(titulo.Select(c => CaracteresInvalidosNomeAba.Contains(c) ? '-' : c).ToArray());
            return limpo.Length > 31 ? limpo[..31] : limpo;
        }

        private static string SanitizarNomeElemento(string nome)
        {
            var limpo = new string([.. nome.Where(char.IsLetterOrDigit)]);
            return string.IsNullOrEmpty(limpo) || char.IsDigit(limpo[0]) ? $"C{limpo}" : limpo;
        }
    }
}
