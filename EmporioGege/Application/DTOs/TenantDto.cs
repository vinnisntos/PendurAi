namespace EmporioGege.Application.DTOs
{
    public record TenantDto(
        Guid Id,
        string NomeFantasia,
        string NomeRepresentante,
        string CpfRgDono,
        string Cnpj,
        string CidadeEstado,
        string? TelefoneEmpresa,
        string TelefoneDono,
        string? EmailEmpresa,
        string EmailDono,
        string StatusLicenca,
        DateTime DataExpiracao,
        DateTime CreatedAt,
        string? AsaasSubscriptionId = null);
}
