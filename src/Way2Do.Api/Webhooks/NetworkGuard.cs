using System.Net;
using System.Net.Sockets;

namespace Way2Do.Api.Webhooks;

/// <summary>
/// Proteção contra SSRF (escopo, seção 6): o servidor só faz requisições para endereços públicos. A mesma regra vale
/// no cadastro e em cada conexão das entregas (<see cref="WebhookHttp"/>), para que uma troca de DNS depois do
/// cadastro (DNS rebinding) não a contorne.
/// </summary>
public sealed class NetworkGuard(bool allowPrivateNetworks)
{
    public bool AllowPrivateNetworks { get; } = allowPrivateNetworks;

    /// <summary>
    /// Resolve o host e devolve os endereços, ou lança <see cref="BlockedDestinationException"/> se algum deles for
    /// proibido (basta um: o cliente HTTP poderia escolher justamente esse).
    /// </summary>
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            addresses = [literal];
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, ct);
            }
            catch (SocketException)
            {
                throw new BlockedDestinationException("o endereço não foi encontrado (DNS)");
            }
        }

        if (addresses.Length == 0)
            throw new BlockedDestinationException("o endereço não foi encontrado (DNS)");
        foreach (var address in addresses)
        {
            if (Classify(address) is { } kind && !(AllowPrivateNetworks && kind is AddressKind.Private or AddressKind.Loopback))
                throw new BlockedDestinationException("o endereço aponta para uma rede interna, que não é permitida");
        }
        return addresses;
    }

    public enum AddressKind
    {
        /// <summary>Loopback (127/8, ::1).</summary>
        Loopback,

        /// <summary>Rede privada (10/8, 172.16/12, 192.168/16, CGNAT 100.64/10, fc00::/7).</summary>
        Private,

        /// <summary>Link-local (169.254/16, inclui o endpoint de metadados de nuvem; fe80::/10).</summary>
        LinkLocal,

        /// <summary>"Esta rede", multicast, documentação, reservados: nunca um consumidor de verdade.</summary>
        Reserved,
    }

    /// <summary>Tipo do endereço quando ele não é público; <c>null</c> para endereço público.</summary>
    public static AddressKind? Classify(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return Classify(address.MapToIPv4());

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return ClassifyV4(address.GetAddressBytes());

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return AddressKind.Reserved;

        var b = address.GetAddressBytes();
        if (address.Equals(IPAddress.IPv6Loopback))
            return AddressKind.Loopback;
        if (address.Equals(IPAddress.IPv6None)) // ::
            return AddressKind.Reserved;
        // IPv4 embutido: compatível (::/96, obsoleto), NAT64 (64:ff9b::/96) e 6to4 (2002::/16).
        if (b[..12].All(x => x == 0))
            return AddressKind.Reserved;
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(x => x == 0))
            return ClassifyV4(b[12..16]);
        if (b[0] == 0x20 && b[1] == 0x02)
            return ClassifyV4(b[2..6]);
        if ((b[0] & 0xfe) == 0xfc)
            return AddressKind.Private; // fc00::/7
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80)
            return AddressKind.LinkLocal; // fe80::/10
        if (b[0] == 0xff)
            return AddressKind.Reserved; // multicast
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8)
            return AddressKind.Reserved; // 2001:db8::/32, documentação
        return null;
    }

    private static AddressKind? ClassifyV4(byte[] b) => b switch
    {
        [0, ..] => AddressKind.Reserved, // "esta rede"
        [10, ..] => AddressKind.Private,
        [100, >= 64 and <= 127, ..] => AddressKind.Private, // CGNAT
        [127, ..] => AddressKind.Loopback,
        [169, 254, ..] => AddressKind.LinkLocal,
        [172, >= 16 and <= 31, ..] => AddressKind.Private,
        [192, 0, 0, _] => AddressKind.Reserved, // atribuições do IETF
        [192, 0, 2, _] => AddressKind.Reserved, // documentação
        [192, 168, ..] => AddressKind.Private,
        [198, 18 or 19, ..] => AddressKind.Reserved, // testes de desempenho
        [198, 51, 100, _] => AddressKind.Reserved, // documentação
        [203, 0, 113, _] => AddressKind.Reserved, // documentação
        [>= 224, ..] => AddressKind.Reserved, // multicast (224/4), reservado (240/4) e broadcast
        _ => null,
    };
}

/// <summary>Destino recusado pela proteção contra SSRF; a mensagem vai para o usuário.</summary>
public sealed class BlockedDestinationException(string message) : Exception(message);
