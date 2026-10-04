using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using ImperiumRDP.Shared;
using ImperiumRDP.Shared.Crypto;

namespace ImperiumRDP.Viewer.Net;

/// <summary>
/// Принимает исходящие соединения агентов клуба (uplink).
/// Панель может находиться за NAT — агенты подключаются к ней сами,
/// поэтому проброс портов в клубе не нужен. Достаточно доступности панели.
/// </summary>
public sealed class UplinkServer : IDisposable
{
    private readonly string _password;
    private TcpListener _listener;
    private Thread _thread;
    private volatile bool _running;

    /// <summary>hostname → живое подключение. Потокобезопасно.</summary>
    private readonly object _lock = new();
    private readonly Dictionary<string, UplinkAgent> _agents = new(StringComparer.OrdinalIgnoreCase);

    public event Action<UplinkAgent> AgentAdded;
    public event Action<string> AgentRemoved;

    public sealed class UplinkAgent
    {
        public string HostName = "";
        public string User = "";
        public string Os = "";
        public string Mac = "";
        public ClientConnection Connection = null!;
        public DateTime Since = DateTime.Now;
        public bool InSession;      // сессия открыта — сообщения идут в неё
    }

    public UplinkServer(string password) { _password = password; }

    public void Start(int port)
    {
        if (_running) return;
        _running = true;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "UplinkServer" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        lock (_lock)
        {
            foreach (var a in _agents.Values) { try { a.Connection.Dispose(); } catch { } }
            _agents.Clear();
        }
    }

    public UplinkAgent Get(string hostname)
    {
        lock (_lock) return _agents.TryGetValue(hostname, out var a) ? a : null;
    }

    public List<UplinkAgent> Snapshot()
    {
        lock (_lock) return _agents.Values.ToList();
    }

    internal void Remove(string hostname)
    {
        lock (_lock)
        {
            if (_agents.Remove(hostname))
                AgentRemoved?.Invoke(hostname);
        }
    }

    private async void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = HandleUplink(client);
            }
            catch (Exception ex)
            {
                if (_running) { try { await Task.Delay(500); } catch { } }
                else break;
                _ = ex;
            }
        }
    }

    private async Task HandleUplink(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            client.SendBufferSize = 512 * 1024;
            client.ReceiveBufferSize = 512 * 1024;

            var hello = new Shared.Protocol.MHello
            {
                Host = "panel",
                User = "",
                Os = "",
                Salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16),
                Challenge = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16),
                Version = 1,
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var (channel, badPassword) = await SecureChannel.AcceptAsync(client.GetStream(), _password, hello, cts.Token);
            if (channel == null)
            {
                client.Close();
                return;
            }

            // первым сообщением агент передаёт свою идентификацию (через шифрованный канал)
            var first = await channel.ReceiveAsync(cts.Token);
            string host = client.Client.RemoteEndPoint?.ToString() ?? "?";
            string user = "";
            string os = "";
            string mac = "";
            if (first is not null && first.Value.type == (byte)Shared.Protocol.MsgType.Hello)
            {
                var id = new Shared.Protocol.MHello();
                using var r = new BinaryReader(new MemoryStream(first.Value.payload));
                id.Read(r);
                if (!string.IsNullOrWhiteSpace(id.Host)) host = id.Host;
                user = id.User;
                os = id.Os;
                mac = id.Mac;
            }

            var agent = new UplinkAgent
            {
                HostName = host,
                Connection = ClientConnection.Attach(channel, this, host),
                Since = DateTime.Now,
                User = user,
                Os = os,
                Mac = mac,
            };

            lock (_lock)
            {
                if (_agents.TryGetValue(host, out var old))
                {
                    try { old.Connection.Dispose(); } catch { }
                }
                _agents[host] = agent;
            }
            AgentAdded?.Invoke(agent);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"UplinkServer: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            try { client.Close(); } catch { }
        }
    }

    public void Dispose() => Stop();
}
