using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Way2Do.Api.Auth;

/// <summary>Configuração de autenticação resolvida na subida da aplicação.</summary>
public sealed class AuthSettings
{
    public const string CookiePath = "/api";
    public const string Issuer = "way2do";
    public const string Audience = "way2do";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);
    private const int MinKeyBytes = 32;

    // Ambientes em que a chave do JWT pode faltar (gerada na subida). Qualquer outro — inclusive um nome digitado
    // errado — exige a chave, para não subir calado com sessões que caem a cada reinício.
    private static readonly HashSet<string> EnvironmentsWithOptionalKey =
        new(StringComparer.OrdinalIgnoreCase) { "Development", "Testing", "Local" };

    /// <summary>Única definição da chave: usada para emitir e para validar o token.</summary>
    public required SymmetricSecurityKey SecurityKey { get; init; }

    public SigningCredentials SigningCredentials => new(SecurityKey, SecurityAlgorithms.HmacSha256);

    /// <summary>Verdadeiro quando a chave foi gerada na subida (ambiente local sem chave configurada).</summary>
    public required bool SigningKeyGenerated { get; init; }

    /// <summary>Origens aceitas no cabeçalho <c>Origin</c> das requisições que alteram estado.</summary>
    public required IReadOnlySet<string> AllowedOrigins { get; init; }

    /// <summary>
    /// Cookie com <c>Secure</c> e prefixo <c>__Secure-</c>. Ligado por padrão (HTTPS, como na VPS); desligável só para
    /// o ambiente local em <c>http://localhost</c>, porque o Safari descarta cookie <c>Secure</c> recebido por HTTP.
    /// </summary>
    public required bool SecureCookie { get; init; }

    public string CookieName => SecureCookie ? "__Secure-way2do-session" : "way2do-session";

    public static AuthSettings FromConfiguration(IConfiguration config, IHostEnvironment env)
    {
        var configuredKey = config["Jwt:SigningKey"];
        byte[] key;
        var generated = false;
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            key = Encoding.UTF8.GetBytes(configuredKey);
            if (key.Length < MinKeyBytes)
                throw new InvalidOperationException($"Jwt:SigningKey precisa ter no mínimo {MinKeyBytes} bytes.");
        }
        else if (!EnvironmentsWithOptionalKey.Contains(env.EnvironmentName))
        {
            throw new InvalidOperationException(
                $"Jwt:SigningKey é obrigatória no ambiente '{env.EnvironmentName}' (opcional só em Development, Testing e Local).");
        }
        else
        {
            key = RandomNumberGenerator.GetBytes(MinKeyBytes);
            generated = true;
        }

        var origins = (config.GetSection("Auth:AllowedOrigins").Get<string[]>() ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim().TrimEnd('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Navegadores mandam Origin também em POST da mesma origem: sem lista, nem o próprio frontend consegue logar.
        if (origins.Count == 0)
            throw new InvalidOperationException("Auth:AllowedOrigins precisa de ao menos uma origem (a do frontend).");

        return new AuthSettings
        {
            SecurityKey = new SymmetricSecurityKey(key),
            SigningKeyGenerated = generated,
            AllowedOrigins = origins,
            SecureCookie = config.GetValue("Auth:SecureCookie", true),
        };
    }
}
