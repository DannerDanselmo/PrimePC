using System.Net;
using System.Net.Sockets;

namespace PrimePC.Gaming;

/// <summary>
/// Descobre o servidor do jogo: o endereço da internet para o qual o processo mais envia pacotes (UDP de preferência,
/// que é o que os jogos online usam). Endereços da rede local, de loopback e multicast são ignorados.
/// </summary>
public sealed class NetworkTracker
{
    private readonly Dictionary<(int Pid, IPAddress Address), Entry> _entries = new();
    private readonly object _lock = new();

    public void OnSend(int pid, IPAddress? address, bool udp, double nowMs)
    {
        if (pid <= 4 || address is null)
            return;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (!IsPublic(address))
            return;
        lock (_lock)
        {
            if (!_entries.TryGetValue((pid, address), out var e))
                _entries[(pid, address)] = e = new Entry();
            e.Score++;
            e.LastMs = nowMs;
            e.Udp |= udp;
        }
    }

    /// <summary>Endereço com mais tráfego recente (últimos 5 s) do processo, ou nulo.</summary>
    public IPAddress? TopEndpoint(int pid, double nowMs)
    {
        lock (_lock)
        {
            return _entries
                .Where(kv => kv.Key.Pid == pid && kv.Value.LastMs >= nowMs - 5000)
                .OrderByDescending(kv => kv.Value.Udp)
                .ThenByDescending(kv => kv.Value.Score)
                .Select(kv => kv.Key.Address)
                .FirstOrDefault();
        }
    }

    /// <summary>Reduz o peso do tráfego antigo e esquece endereços sem tráfego (chamar a cada segundo).</summary>
    public void Prune(double nowMs)
    {
        lock (_lock)
        {
            foreach (var (key, e) in _entries.ToList())
            {
                if (e.LastMs < nowMs - 15_000)
                    _entries.Remove(key);
                else
                    e.Score *= 0.5;
            }
        }
    }

    /// <summary>IPv4 dos eventos de rede do Windows: inteiro cujos bytes (little-endian) já estão na ordem do endereço.</summary>
    public static IPAddress FromInt32(int value) => new(BitConverter.GetBytes(value));

    public static bool IsPublic(IPAddress a)
    {
        if (IPAddress.IsLoopback(a))
            return false;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)); // CGNAT
        }

        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return !(a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || a.IsIPv6UniqueLocal || a.Equals(IPAddress.IPv6Any));
        return false;
    }

    private sealed class Entry
    {
        public double Score;
        public double LastMs;
        public bool Udp;
    }
}
