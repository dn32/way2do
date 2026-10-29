using Microsoft.AspNetCore.Identity;
using Way2Do.Api.Data;

namespace Way2Do.Api.Auth;

/// <summary>
/// Faz o login de usuário inexistente pagar o mesmo custo de verificação de senha que o de usuário existente, para
/// que o tempo de resposta não revele quais usuários existem. O hash de referência sai do mesmo hasher injetado que
/// verifica as senhas reais, então segue qualquer mudança de configuração dele (iterações, compatibilidade).
/// </summary>
public sealed class LoginTimingGuard(IPasswordHasher<User> hasher)
{
    private static readonly User Placeholder = new() { UserName = "-", PasswordHash = "-" };
    private readonly string _referenceHash = hasher.HashPassword(Placeholder, Guid.NewGuid().ToString());

    public void SpendVerificationCost(string password) =>
        hasher.VerifyHashedPassword(Placeholder, _referenceHash, password);
}
