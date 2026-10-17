namespace EmporioGege.Application.DTOs
{
    public record FuncionarioDto(Guid Id, string Nome, string Role, string Email, bool Ativo = true);
}
