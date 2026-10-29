using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Way2Do.Api.Data;

namespace Way2Do.Api.Auth;

public sealed record IssuedToken(string Value, DateTimeOffset ExpiresAt);

public sealed class TokenService(AuthSettings settings, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _credentials = settings.SigningCredentials;

    public IssuedToken Issue(User user)
    {
        var now = clock.GetUtcNow();
        var expires = now.Add(AuthSettings.SessionLifetime);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = AuthSettings.Issuer,
            Audience = AuthSettings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Name, user.UserName),
            ]),
            SigningCredentials = _credentials,
        });
        return new IssuedToken(token, expires);
    }
}
