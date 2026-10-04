using System.Net.Sockets;
using System.Threading.Channels;
using ImperiumRDP.Shared;
using ImperiumRDP.Shared.Crypto;
using ImperiumRDP.Shared.Protocol;

namespace ImperiumRDP.Viewer.Net;

/// <summary>Подключение к агенту: рукопожатие, приём и отправка сообщений.</summary>
public sealed class ClientConnection : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly SecureChannel _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<Msg> _out = Channel.CreateBounded<Msg>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    public MHello Hello { get; private set; }
    public event Action<Msg> Received;
    public event Action<string> Closed;
    public long TotalBytesSent => _channel?.TotalBytesSent ?? 0;
    public long TotalBytesReceived => _channel?.TotalBytesReceived ?? 0;

    public static async Task<ClientConnection> ConnectAsync(string ip, int port, string password, int timeoutMs = 6000)
    {
        var tcp = new TcpClient();
        using var connectCts = new CancellationTokenSource(timeoutMs);
        try
        {
            await tcp.ConnectAsync(ip, port, connectCts.Token);
        }
        catch (Exception)
        {
            tcp.Dispose();
            throw;
        }

        tcp.NoDelay = true;
        tcp.SendBufferSize = 512 * 1024;
        tcp.ReceiveBufferSize = 512 * 1024;
        EnableKeepAlive(tcp);

        var channel = await SecureChannel.ConnectAsync(tcp.GetStream(), password, timeoutMs + 4000, CancellationToken.None);
        var conn = new ClientConnection(tcp, channel);
        return conn;
    }

    private static void EnableKeepAlive(TcpClient tcp)
    {
        try
        {
            var s = tcp.Client;
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
        }
        catch { }
    }

    /// <summary>Запуск циклов приёма/отправки — вызывать после подписки на события.</summary>
    public void StartLoops()
    {
        _ = WriterLoop();
        _ = ReaderLoop();
    }

    private async Task WriterLoop()
    {
        try
        {
            await foreach (var m in _out.Reader.ReadAllAsync(_cts.Token))
                await _channel.SendAsync((byte)m.Type, m.Encode(), _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Close("Ошибка отправки: " + ex.Message); }
    }

    private async Task ReaderLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await _channel.ReceiveAsync(_cts.Token);
                if (frame is null) { Close("Агент закрыл соединение"); return; }
                var msg = MsgCodec.Decode((MsgType)frame.Value.type, frame.Value.payload);
                if (msg != null) Received?.Invoke(msg);
            }
        }
        catch (Exception ex) { Close("Ошибка приёма: " + ex.Message); }
    }

    public void Send(Msg m)
    {
        if (!_out.Writer.TryWrite(m)) _ = SendAsync(m);
    }

    /// <summary>Отправка с противодавлением — для потоков (файлы).</summary>
    public async Task SendAsync(Msg m) => await _out.Writer.WriteAsync(m, _cts.Token);

    /// <summary>true — канал принадлежит uplink-серверу; закрытие сессии его не убивает.</summary>
    public bool Borrowed { get; private set; }
    private UplinkServer? _owner;
    private string _uplinkHost;

    /// <summary>Подключение поверх уже установленного uplink-канала.</summary>
    public static ClientConnection Attach(SecureChannel channel, UplinkServer owner, string uplinkHost)
    {
        var conn = new ClientConnection(new TcpClient(), channel)
        {
            Borrowed = true,
            _owner = owner,
            _uplinkHost = uplinkHost,
        };
        return conn;
    }

    private ClientConnection(TcpClient tcp, SecureChannel channel)
    {
        _tcp = tcp;
        _channel = channel;
        Hello = channel.Hello;
    }

    private void Close(string reason)
    {
        if (_cts.IsCancellationRequested) return;
        try { _cts.Cancel(); } catch { }
        Closed?.Invoke(reason);
        if (Borrowed && _owner != null && _uplinkHost != null)
            _owner.Remove(_uplinkHost);   // uplink умер — машина уходит из списка
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _out.Writer.TryComplete();
        if (!Borrowed) _channel?.Dispose();
        try { _tcp.Close(); } catch { }
    }
}
