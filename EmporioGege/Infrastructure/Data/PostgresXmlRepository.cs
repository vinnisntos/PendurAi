using System.Xml.Linq;
using Dapper;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace EmporioGege.Infrastructure.Data
{
    // Chaves do Data Protection na tabela data_protection_keys (migration 0016). No disco da VPS
    // elas podiam sumir a cada deploy, deslogando todos os caixas e quebrando formulários abertos.
    // Síncrono porque a interface do ASP.NET é síncrona; só roda ao carregar ou girar chaves.
    public class PostgresXmlRepository(string connectionString) : IXmlRepository
    {
        public IReadOnlyCollection<XElement> GetAllElements()
        {
            using var conexao = new NpgsqlConnection(connectionString);
            return conexao.Query<string>("SELECT xml FROM data_protection_keys ORDER BY criado_em")
                .Select(XElement.Parse)
                .ToList();
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            using var conexao = new NpgsqlConnection(connectionString);
            conexao.Execute(
                "INSERT INTO data_protection_keys (nome, xml) VALUES (@Nome, @Xml) ON CONFLICT (nome) DO UPDATE SET xml = EXCLUDED.xml",
                new { Nome = friendlyName, Xml = element.ToString(SaveOptions.DisableFormatting) });
        }
    }
}
