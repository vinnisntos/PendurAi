using Postgrest.Attributes;
using Postgrest.Models;

namespace EmporioGege.Models
{
    [Table("profiles")]
    public class Profile : BaseModel
    {
        [PrimaryKey("id", false)]
        public string Id { get; set; } = default!;

        [Column("tenant_id")]
        public string? TenantId { get; set; } // Aceita nulo se for SuperAdmin

        [Column("nome")]
        public string Nome { get; set; } = default!;

        [Column("role")]
        public string Role { get; set; } = "vendedor";

        [Column("ativo")]
        public bool Ativo { get; set; } = true;

        [Column("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }
}