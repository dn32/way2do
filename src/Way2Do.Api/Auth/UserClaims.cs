using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Way2Do.Api.Auth;

/// <summary>Leitura única da identidade da sessão a partir dos claims do JWT.</summary>
public static class UserClaims
{
    /// <summary>Id e nome do usuário; <c>null</c> se o token não tiver um <c>sub</c> válido.</summary>
    public static (Guid Id, string Name)? GetUser(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
            ? (id, principal.FindFirstValue(JwtRegisteredClaimNames.Name) ?? "")
            : null;
}
