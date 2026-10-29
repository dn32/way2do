using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Way2Do.Consumer;

/// <summary>
/// Token de entrada <c>T</c> e identificador do espaço <c>H = SHA-256(T)</c>. O consumidor não guarda <c>T</c>:
/// recalcula <c>H</c> a cada evento, e por isso as URLs sobrevivem ao reinício. A derivação é de mão única: quem tem
/// o painel não descobre a URL de entrada.
/// </summary>
public static partial class SpaceKeys
{
    /// <summary>256 bits aleatórios em base64url sem padding.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 do texto ASCII do token, em hex minúsculo.</summary>
    public static string SpaceIdOf(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    public static bool IsValidToken(string? token) => token is not null && TokenFormat().IsMatch(token);

    public static bool IsValidSpaceId(string? spaceId) => spaceId is not null && SpaceIdFormat().IsMatch(spaceId);

    /// <summary>Forma abreviada do espaço, a única que vai para o log (nunca o token nem o caminho completo).</summary>
    public static string Short(string spaceId) => spaceId[..8];

    // \z em vez de $: o $ aceitaria uma quebra de linha no fim.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{43}\z")]
    private static partial Regex TokenFormat();

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex SpaceIdFormat();
}
