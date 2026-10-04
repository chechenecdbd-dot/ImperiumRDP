using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using ImperiumRDP.Shared;
using ImperiumRDP.Shared.Protocol;

namespace ImperiumRDP.Viewer.Net;

/// <summary>Сканер подсети: находит агентов ImperiumRDP (TCP-подключение + приветствие).</summary>
public static class Scanner
{
    /// <summary>Подсети всех активных IPv4-адаптеров в формате 192.168.1.0/24.</summary>
    public static List<string> DetectSubnets()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(a.Address)) continue;
                    var mask = a.IPv4Mask;
                    if (mask == null) continue;
                    int bits = MaskToCidr(mask);
                    if (bits < 8 || bits > 30) continue;
                    string cidr = $"{a.Address}/{bits}";
                    if (!result.Contains(cidr)) result.Add(cidr);
                }
            }
        }
        catch { }
        return result;
    }

    private static int MaskToCidr(IPAddress mask)
    {
        int bits = 0;
        foreach (byte b in mask.GetAddressBytes())
            bits += System.Numerics.BitOperations.PopCount(b);
        return bits;
    }

    /// <summary>
    /// Скан диапазона. Форматы: "192.168.1.0/24", "192.168.1.5-192.168.1.120", "192.168.1.".
    /// </summary>
    public static async Task<List<Machine>> ScanAsync(string spec, int port, Action<Machine> onFound, CancellationToken ct)
    {
        var ips = Enumerate(spec);
        using var sem = new SemaphoreSlim(200);
        var tasks = ips.Select(async ip =>
        {
            await sem.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var m = await ProbeAsync(ip, port, 700, ct);
                if (m != null) onFound(m);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { sem.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks);
        return new List<Machine>();
    }

    public static IEnumerable<string> Enumerate(string spec)
    {
        spec = spec.Trim();
        if (spec.Contains('/')) // CIDR
        {
            var parts = spec.Split('/');
            var baseIp = IPAddress.Parse(parts[0]);
            int bits = int.Parse(parts[1]);
            if (bits >= 32) { yield return parts[0]; yield break; } // одиночный IP x.x.x.x/32
            uint mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);
            uint ipU = IpToUint(baseIp);
            uint network = ipU & mask;
            uint count = bits >= 31 ? 1u : (uint.MaxValue >> bits) - 1;
            if (count > 4094) count = 4094; // защита от /8
            for (uint i = 1; i <= count; i++)
                yield return UintToIp(network + i);
        }
        else if (spec.Contains('-')) // диапазон a.b.c.x-a.b.c.y (или полные IP)
        {
            var parts = spec.Split('-');
            var a = IPAddress.Parse(parts[0].Trim());
            var b = IPAddress.Parse(parts[1].Trim());
            uint ua = IpToUint(a), ub = IpToUint(b);
            if (ub < ua) (ua, ub) = (ub, ua);
            if (ub - ua > 4094) ub = ua + 4094;
            for (uint u = ua; u <= ub; u++) yield return UintToIp(u);
        }
        else if (spec.EndsWith('.')) // префикс "192.168.1."
        {
            for (int i = 1; i <= 254; i++) yield return spec + i;
        }
        else yield return spec; // одиночный IP
    }

    private static uint IpToUint(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static string UintToIp(uint u) => $"{(u >> 24) & 255}.{(u >> 16) & 255}.{(u >> 8) & 255}.{u & 255}";

    /// <summary>Подключение и чтение Hello (агент отправляет его сразу). null — не наш агент.</summary>
    public static async Task<Machine> ProbeAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await tcp.ConnectAsync(ip, port, cts.Token);
            var hello = await ReadHelloAsync(tcp, cts.Token);
            if (hello == null) return null;
            return new Machine
            {
                Ip = ip,
                Port = port,
                HostName = hello.Host,
                UserName = hello.User,
                Os = hello.Os,
                LastSeen = DateTime.Now,
            };
        }
        catch { return null; }
    }

    internal static async Task<MHello> ReadHelloAsync(TcpClient tcp, CancellationToken ct)
    {
        var frame = await Shared.Net.Framing.ReadFrameAsync(tcp.GetStream(), ct);
        if (frame is null || frame.Value.type != (byte)MsgType.Hello) return null;
        var hello = new MHello();
        using var r = new BinaryReader(new MemoryStream(frame.Value.payload), Encoding.UTF8);
        hello.Read(r);
        return hello.Magic == "IRDP1" ? hello : null;
    }
}
