using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace EmporioGege.Application.Services
{
    public class CadastroLojaService(
        IDbConnectionFactory connectionFactory,
        ITenantService tenantService,
        IBillingNotificationService billingNotificationService,
        IConfiguration configuration,
        ILogger<CadastroLojaService> logger) : ICadastroLojaService
    {
        // Preços fixos por plano, resolvidos aqui no servidor - nunca aceitos de um campo do
        // formulário público (ver comentário em CadastroLojaDtos.cs). Mesmos valores sugeridos
        // usados em Pages/Index.cshtml e em SuperAdmin/Adegas/Editar.
        private static readonly Dictionary<string, decimal> PrecosPlano = new()
        {
            ["start"] = 119.00m,
            ["pro"] = 199.00m,
            ["enterprise"] = 349.00m
        };

        // Dias de teste grátis, sem cobrança - a assinatura Asaas só é criada quando o lojista
        // clica em "Assinar agora" (Admin/Index) ou quando o trial vence sem ação (ver
        // Infrastructure/Faturamento/TrialExpiradoProcessor).
        public const int DiasTrial = 7;

        public async Task<CadastroLojaResultado> CadastrarAsync(CadastrarLojaDto dto, CancellationToken ct = default)
        {
            if (!PrecosPlano.TryGetValue(dto.Plano, out var valorMensalidade))
                return new CadastroLojaResultado(false, null, "Plano inválido.", "validacao");

            await using (var connection = await connectionFactory.CreateOpenConnectionAsync(ct))
            {
                // Guard contra reenvio duplicado (F5, back-button) - não é uma constraint de
                // unicidade real no banco, só uma checagem barata pra evitar criar um segundo
                // tenant órfão quando o mesmo formulário é enviado de novo.
                var jaExiste = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
                    "SELECT 1 FROM tenants WHERE email_dono = @EmailDono OR cnpj = @Cnpj LIMIT 1",
                    new { dto.EmailDono, dto.Cnpj }, cancellationToken: ct));

                if (jaExiste is not null)
                {
                    return new CadastroLojaResultado(false, null,
                        "Já existe um cadastro em andamento com esse e-mail ou CNPJ. Confirme seu e-mail ou entre em contato com o suporte.",
                        "duplicado");
                }
            }

            // 1) Cria o tenant como "trial" - acesso liberado na hora, sem cobrança, por
            // DiasTrial dias (ver Login.cshtml.cs). DataExpiracao aqui já é a data real de fim
            // do trial (diferente do "pendente" antigo, onde era só um placeholder) - o job
            // TrialExpiradoProcessor usa ela pra saber quando converter em cobrança.
            Guid tenantId;
            var dataFimTrial = DateTime.UtcNow.AddDays(DiasTrial);
            try
            {
                tenantId = await tenantService.SalvarAsync(new SalvarTenantDto(
                    null, dto.NomeFantasia, dto.NomeRepresentante, dto.CpfRgDono, dto.Cnpj, dto.CidadeEstado,
                    null, dto.TelefoneDono, null, dto.EmailDono, "trial", dataFimTrial), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao criar tenant no autocadastro (CNPJ {Cnpj}).", dto.Cnpj);
                return new CadastroLojaResultado(false, null, "Não foi possível registrar sua loja. Tente novamente em instantes.", "tenant");
            }

            // 2) Persiste plano/valor/forma de pagamento já aqui, mesmo antes de existir
            // qualquer cobrança de verdade - não fatal se falhar, mas SÃO os dados que
            // IniciarCobrancaTrialAsync lê depois pra criar a assinatura (fim do trial ou
            // "Assinar agora"), então uma falha aqui deixa a loja sem forma de converter
            // automaticamente (só manualmente via SuperAdmin/Adegas/Editar).
            try
            {
                await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE tenants SET plano = @Plano, valor_mensalidade = @ValorMensalidade,
                           forma_pagamento_preferida = @TipoCobranca
                    WHERE id = @TenantId
                    """,
                    new { dto.Plano, ValorMensalidade = valorMensalidade, dto.TipoCobranca, TenantId = tenantId }, cancellationToken: ct));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao persistir plano/valor no autocadastro (tenant {TenantId}) - não fatal.", tenantId);
            }

            // 3) Cria o login (Supabase Auth) - cliente isolado, mesmo padrão de
            // FuncionarioService.CriarAsync, já que aqui também não existe sessão/tenant
            // ambiente (o "chamador" é um visitante anônimo, não um admin logado).
            Guid usuarioId;
            try
            {
                var url = configuration["Supabase:Url"]!;
                var key = configuration["Supabase:Key"]!;
                var clienteIsolado = new Supabase.Client(url, key, new Supabase.SupabaseOptions { AutoConnectRealtime = false });

                var sessao = await clienteIsolado.Auth.SignUp(dto.EmailDono, dto.Senha);
                if (sessao?.User?.Id is null)
                {
                    await RemoverTenantOrfaoAsync(tenantId);
                    return new CadastroLojaResultado(false, null,
                        "Não conseguimos criar seu login. Verifique se o e-mail já está cadastrado ou use uma senha mais forte, e tente de novo.",
                        "supabase");
                }

                usuarioId = Guid.Parse(sessao.User.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao criar login Supabase no autocadastro (tenant {TenantId}).", tenantId);
                await RemoverTenantOrfaoAsync(tenantId);
                return new CadastroLojaResultado(false, null,
                    "Houve um erro ao criar seu login. Tente de novo em instantes; se persistir, fale com o suporte.",
                    "supabase");
            }

            // 4) Vincula o login criado ao tenant como administrador.
            try
            {
                await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO profiles (id, tenant_id, nome, role, updated_at)
                    VALUES (@Id, @TenantId, @Nome, 'administrador', now())
                    """,
                    new { Id = usuarioId, TenantId = tenantId, Nome = dto.NomeRepresentante }, cancellationToken: ct));
            }
            catch (Exception ex)
            {
                // Loja e login existem, mas sem o vínculo o dono não consegue acessar nada -
                // fica registrado aqui pra alguém linkar manualmente (INSERT de uma linha só).
                logger.LogError(ex, "Falha ao vincular perfil no autocadastro (tenant {TenantId}, usuário {UsuarioId}).", tenantId, usuarioId);
                return new CadastroLojaResultado(false, tenantId,
                    "Sua loja e seu login foram criados, mas houve um problema ao finalizar o vínculo. Entre em contato com o suporte informando o CNPJ para finalizarmos manualmente.",
                    "perfil");
            }

            // 5) Sem cobrança nenhuma criada agora - o trial é grátis de verdade. Só avisa por
            // e-mail (best-effort: EnviarBoasVindasTrialAsync nunca lança, ver
            // BillingNotificationService) que o teste começou e como assinar quando quiser.
            await billingNotificationService.EnviarBoasVindasTrialAsync(tenantId, DiasTrial, ct);

            return new CadastroLojaResultado(true, tenantId,
                $"Cadastro recebido! Confirme seu e-mail pelo link que enviamos e faça login - você tem {DiasTrial} dias grátis pra usar o PendurAi sem nenhuma cobrança. Quando quiser assinar, é só clicar em \"Assinar agora\" dentro do painel.",
                null);
        }

        // Sem login criado ninguém usa a loja, e o guard de duplicidade (e-mail/CNPJ) travava a
        // nova tentativa com "cadastro em andamento". Ainda não existe nada ligado ao tenant aqui.
        private async Task RemoverTenantOrfaoAsync(Guid tenantId)
        {
            try
            {
                await using var connection = await connectionFactory.CreateOpenConnectionAsync(CancellationToken.None);
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM tenants WHERE id = @TenantId AND status_licenca = 'trial' AND asaas_customer_id IS NULL",
                    new { TenantId = tenantId }));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao remover tenant órfão {TenantId} do autocadastro.", tenantId);
            }
        }
    }
}
