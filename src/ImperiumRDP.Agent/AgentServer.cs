using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using ImperiumRDP.Agent.Admin;
using ImperiumRDP.Agent.Capture;
using ImperiumRDP.Agent.Input;
using ImperiumRDP.Agent.Nvenc;
using ImperiumRDP.Shared.Crypto;
using ImperiumRDP.Shared.Protocol;

namespace ImperiumRDP.Agent;

public sealed class AgentServer
{
    private readonly AgentConfig _cfg;
    private readonly ICaptureSource _engine;
    private TcpListener _listener;
    private volatile bool _accepting;

    private readonly object _sessionsLock = new();
    private readonly List<AgentSession> _sessions = new();

    public AgentServer(AgentConfig cfg)
    {
        _cfg = cfg;
        // приоритет: NVENC (H.264, GPU) → JPEG-тайлы (GDI)
        ICaptureSource engine = NvencEngine.TryCreate(Log.Write);
        _engine = engine ?? new CaptureEngine(Log.Write);
        _engine.SetOptions(cfg.Fps, cfg.Quality);
    }

    public void Start()
    {
        _engine.Start();
        var listener = new TcpListener(IPAddress.Any, _cfg.Port);
        listener.Start();
        _listener = listener;
        _accepting = true;
        Log.Write($"Агент запущен. Порт: {_cfg.Port}. Экран: {_engine.ScreenRect.Width}x{_engine.ScreenRect.Height}");

        var acceptThread = new Thread(() =>
        {
            while (_accepting)
            {
                try
                {
                    var client = listener.AcceptTcpClient();
                    Task.Run(() => HandleClient(client));
                }
                catch (Exception ex) { if (_accepting) { Log.Write($"Accept: {ex.Message}"); Thread.Sleep(500); } }
            }
        })
        { IsBackground = true, Name = "AgentAccept" };
        acceptThread.Start();

        // Uplink: агент сам подключается к панели администратора (исходящее соединение —
        // работает из-за NAT провайдера клуба, проброс портов не нужен)
        if (!string.IsNullOrWhiteSpace(_cfg.Uplink))
        {
            _ = Task.Run(UplinkLoopAsync);
        }
    }

    /// <summary>Бесконечный цикл исходящего соединения с панелью: обрыв → переподключение с паузой.</summary>
    private async Task UplinkLoopAsync()
    {
        var delay = TimeSpan.FromSeconds(3);
        var lastAttemptLog = DateTime.MinValue;
        while (_accepting)
        {
            try
            {
                var parts = _cfg.Uplink.Split(':', 2);
                string host = parts[0].Trim();
                int port = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 48500;

                using var tcp = new TcpClient();
                tcp.NoDelay = true;
                tcp.SendBufferSize = 512 * 1024;
                tcp.ReceiveBufferSize = 128 * 1024;
                EnableKeepAlive(tcp);
                var connectTask = tcp.ConnectAsync(host, port);
                if (!connectTask.Wait(TimeSpan.FromSeconds(10)))
                    throw new IOException("таймаут подключения");

                var hello = new MHello
                {
                    Host = Environment.MachineName,
                    User = Environment.UserName,
                    Os = Environment.OSVersion.VersionString,
                    Mac = NetworkMac(),
                    Salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16),
                    Challenge = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16),
                    Version = 1,
                };
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var task = SecureChannel.ConnectAsync(tcp.GetStream(), _cfg.Password, 15000, cts.Token);
                if (!task.Wait(TimeSpan.FromSeconds(20)))
                    throw new IOException("таймаут аутентификации");
                var channel = task.Result;

                // передаём свою идентификацию — в uplink-рукопожатии нет места для имени агента
                await channel.SendAsync((byte)MsgType.Hello, hello.Encode(), cts.Token);

                Log.Write($"Uplink установлен: {host}:{port}");
                delay = TimeSpan.FromSeconds(3);
                RunSessionAsync(channel, $"uplink:{host}").GetAwaiter().GetResult();
                Log.Write("Uplink закрыт панелью, переподключение...");
            }
            catch (Exception ex)
            {
                if ((DateTime.Now - lastAttemptLog).TotalSeconds > 60)
                {
                    Log.Write($"Uplink: {ex.Message} (повторы каждые {delay.TotalSeconds:F0} с)");
                    lastAttemptLog = DateTime.Now;
                }
            }
            await Task.Delay(delay);
            if (delay < TimeSpan.FromSeconds(60)) delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 60));
        }
    }

    /// <summary>MAC основного физического адаптера (для Wake-on-LAN).</summary>
    internal static string NetworkMac()
    {
        try
        {
            var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                            && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                .OrderByDescending(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet)
                .FirstOrDefault();
            if (nic != null)
                return string.Join("-", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
        }
        catch { }
        return "";
    }

    /// <summary>Останов (для GUI-хаба): закрывает слушатель, сессии и движок захвата.</summary>
    public void Stop()
    {
        _accepting = false;
        try { _listener?.Stop(); } catch { }
        lock (_sessionsLock)
        {
            foreach (var s in _sessions.ToArray()) { try { s.Dispose(); } catch { } }
            _sessions.Clear();
        }
        try { _engine.Dispose(); } catch { }
        Log.Write("Агент остановлен");
    }

    private static void EnableKeepAlive(TcpClient client)
    {
        try
        {
            var s = client.Client;
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
        }
        catch { }
    }

    private async Task HandleClient(TcpClient client)
    {
        string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            client.NoDelay = true;
            client.SendBufferSize = 512 * 1024;
            client.ReceiveBufferSize = 128 * 1024;
            EnableKeepAlive(client);
            var stream = client.GetStream();

            var hello = new MHello
            {
                Host = Environment.MachineName,
                User = Environment.UserName,
                Os = Environment.OSVersion.VersionString,
                Mac = NetworkMac(),
                Salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16),
                Challenge = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16),
                Version = 1,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var (channel, badPassword) = await SecureChannel.AcceptAsync(stream, _cfg.Password, hello, cts.Token);
            if (channel == null)
            {
                if (badPassword) Log.Write($"Отклонён (неверный пароль): {endpoint}");
                // TODO: считать попытки по IP и блокировать адрес после N неудач;
                // TODO: при желании — белый список адресов/имён панели (сейчас принимает любого с верным паролем).
                client.Close();
                return;
            }

            try
            {
                await RunSessionAsync(channel, endpoint);
            }
            finally
            {
                client.Close();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Клиент {endpoint}: {ex.Message}");
            try { client.Close(); } catch { }
        }
    }

    /// <summary>Жизненный цикл одной сессии поверх готового канала (LAN или uplink).
    /// Захват экрана включается лениво — по первому запросу видео от панели.</summary>
    private async Task RunSessionAsync(SecureChannel channel, string endpoint)
    {
        int count;
        lock (_sessionsLock) count = _sessions.Count;
        if (count >= _cfg.MaxClients)
        {
            using var busyCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await channel.SendAsync((byte)MsgType.Error,
                new MStatus(MsgType.Error, "Достигнут лимит одновременных подключений").Encode(), busyCts.Token);
            channel.Dispose();
            return;
        }

        var session = new AgentSession(endpoint, channel, _engine);
        lock (_sessionsLock) _sessions.Add(session);
        Log.Write($"Администратор подключён: {endpoint}");

        IDisposable unsub = null;
        try
        {
            session.Run();
            await session.ReceiveLoopAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"Сессия {endpoint}: {ex.Message}");
        }
        finally
        {
            unsub?.Dispose();
            lock (_sessionsLock) _sessions.Remove(session);
            session.Dispose();
            channel.Dispose();
            Log.Write($"Отключён: {endpoint}");
        }
    }
}

public sealed class AgentSession : IDisposable
{
    private const string _version = "1.0.0";
    private readonly string _endpoint;
    private readonly SecureChannel _channel;
    private readonly ICaptureSource _engine;
    private InputInjector _input;
    private ShellServer _shell;
    private FileStream _recvFile;
    private string _recvPath;

    // два канала: тайлы можно терять (следующий кадр всё исправит), служебные сообщения — никогда
    private readonly Channel<(byte type, byte[] payload)> _tiles = Channel.CreateBounded<(byte, byte[])>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Channel<(byte type, byte[] payload)> _ctrl = Channel.CreateBounded<(byte, byte[])>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private long _tileBytes; // защита от раздувания памяти очередью тайлов
    private readonly CancellationTokenSource _cts = new();
    private IDisposable _engineSub;
    private volatile bool _videoStarted;

    /// <summary>Ленивое включение захвата: панель получает видео только когда запросила его.</summary>
    private void EnsureVideoStarted()
    {
        if (_videoStarted) return;
        _videoStarted = true;
        _engineSub = _engine.Subscribe(OnEngineMessage);
        _engine.RequestKeyframe();
    }

    public AgentSession(string endpoint, SecureChannel channel, ICaptureSource engine)
    {
        _endpoint = endpoint;
        _channel = channel;
        _engine = engine;
    }

    private InputInjector Input
    {
        get
        {
            var r = _engine.ScreenRect;
            if (r.Width <= 0) r = new Rectangle(0, 0, 1, 1);
            if (_input == null || _inputRect != r)
            {
                _input = new InputInjector(r);
                _inputRect = r;
            }
            return _input;
        }
    }
    private Rectangle _inputRect;

    /// <summary>Вызывается из потоков движка/приёма. Тайлы — теряемые, остальное — с блокировкой (противодавление).</summary>
    public void OnEngineMessage(MsgType type, byte[] payload)
    {
        if (type == MsgType.TileBatch)
        {
            long queued = Interlocked.Add(ref _tileBytes, payload.LongLength);
            if (queued > 64_000_000)
            {
                Interlocked.Add(ref _tileBytes, -payload.LongLength);
                return; // канал не справляется — пропускаем кадр, придёт ключевой
            }
            _tiles.Writer.TryWrite(((byte)type, payload));
        }
        else
        {
            try { _ctrl.Writer.WriteAsync(((byte)type, payload), _cts.Token).AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
        }
    }

    public void Run()
    {
        Task.Run(async () =>
        {
            try
            {
                var ct = _cts.Token;
                var ctrlWait = _ctrl.Reader.WaitToReadAsync(ct).AsTask();
                var tileWait = _tiles.Reader.WaitToReadAsync(ct).AsTask();
                while (!ct.IsCancellationRequested)
                {
                    await Task.WhenAny(ctrlWait, tileWait);
                    // приоритет служебным сообщениям (файлы, ответы) над видео
                    if (ctrlWait.IsCompleted)
                    {
                        if (ctrlWait.IsCompletedSuccessfully && ctrlWait.Result)
                            while (_ctrl.Reader.TryRead(out var m))
                                await _channel.SendAsync(m.type, m.payload, ct);
                        ctrlWait = _ctrl.Reader.WaitToReadAsync(ct).AsTask();
                    }
                    if (tileWait.IsCompleted)
                    {
                        if (tileWait.IsCompletedSuccessfully && tileWait.Result)
                            while (_tiles.Reader.TryRead(out var m))
                            {
                                Interlocked.Add(ref _tileBytes, -m.payload.LongLength);
                                await _channel.SendAsync(m.type, m.payload, ct);
                            }
                        tileWait = _tiles.Reader.WaitToReadAsync(ct).AsTask();
                    }
                    if (ctrlWait.IsCanceled || tileWait.IsCanceled) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Write($"Отправка {_endpoint}: {ex.Message}");
                try { _cts.Cancel(); } catch { }
            }
        }, _cts.Token);
    }

    public async Task ReceiveLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            var frame = await _channel.ReceiveAsync(ct);
            if (frame is null) break;
            var msg = MsgCodec.Decode((MsgType)frame.Value.type, frame.Value.payload);
            if (msg != null)
                await HandleAsync(msg, ct);
        }
    }

    private void Reply(Msg m) => OnEngineMessage(m.Type, m.Encode());
    private void ReplyOk(string info = "OK") => Reply(new MStatus(MsgType.Ok, info));
    private void ReplyError(string err) => Reply(new MStatus(MsgType.Error, err));

    private async Task HandleAsync(Msg msg, CancellationToken ct)
    {
        try
        {
            switch (msg)
            {
                case MPing ping: Reply(new MPong { Stamp = ping.Stamp }); break;
                case MRequestKeyframe: EnsureVideoStarted(); _engine.RequestKeyframe(); break;
                case MSetOptions opt: EnsureVideoStarted(); _engine.SetOptions(opt.Fps, opt.Quality); break;

                case MMouseMove m: Input.MouseMove(m.X, m.Y); break;
                case MMouseButton b: Input.MouseMove(b.X, b.Y); Input.MouseButton(b.Button, b.Down); break;
                case MMouseWheel w: Input.MouseMove(w.X, w.Y); Input.MouseWheel(w.Delta); break;
                case MKey k: Input.Key(k.Vk, k.Down); break;
                case MText t: Input.Text(t.Chars); break;

                case MSysInfoReq:
                {
                    var r = _engine.ScreenRect;
                    var dto = SysInfoBuilder.Build(_version, $"{r.Width}x{r.Height} @({r.X},{r.Y})");
                    Reply(new MSysInfo(JsonSerializer.Serialize(dto)));
                    break;
                }

                case MProcListReq:
                    Reply(new MProcList(JsonSerializer.Serialize(ProcessAdmin.List())));
                    break;

                case MKillProc kp:
                    try { ProcessAdmin.Kill(kp.Pid); ReplyOk($"Процесс {kp.Pid} завершён"); }
                    catch (Exception ex) { ReplyError($"Не удалось завершить {kp.Pid}: {ex.Message}"); }
                    break;

                case MRunProgram rp:
                    try { ProcessAdmin.Run(rp.Command); ReplyOk("Запущено"); }
                    catch (Exception ex) { ReplyError($"Запуск не удался: {ex.Message}"); }
                    break;

                case MShellStart:
                    _shell ??= new ShellServer();
                    _shell.Start((text, isErr) => Reply(new MShellOutput { Text = text, IsErr = isErr }));
                    ReplyOk("Shell запущен");
                    break;

                case MShellInput si: _shell?.WriteLine(si.Line); break;
                case MShellStop: _shell?.Dispose(); _shell = null; break;

                case MFilePushStart fs:
                    try
                    {
                        _recvPath = Environment.ExpandEnvironmentVariables(fs.RemotePath);
                        var dir = Path.GetDirectoryName(_recvPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        _recvFile?.Dispose();
                        _recvFile = new FileStream(_recvPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        Log.Write($"Приём файла: {_recvPath} ({fs.Size} байт)");
                    }
                    catch (Exception ex) { ReplyError($"Не удалось создать файл: {ex.Message}"); }
                    break;

                case MFileData fd:
                    try
                    {
                        if (_recvFile != null)
                        {
                            await _recvFile.WriteAsync(fd.Chunk, ct);
                            if (fd.Last)
                            {
                                await _recvFile.FlushAsync(ct);
                                _recvFile.Dispose(); _recvFile = null;
                                Log.Write($"Файл сохранён: {_recvPath}");
                                ReplyOk($"Файл сохранён: {_recvPath}");
                            }
                        }
                    }
                    catch (Exception ex) { ReplyError($"Ошибка записи файла: {ex.Message}"); }
                    break;

                case MFilePull fp:
                    try
                    {
                        string path = Environment.ExpandEnvironmentVariables(fp.RemotePath);
                        if (!File.Exists(path)) { Reply(new MFilePullDeny { Error = "Файл не найден" }); break; }
                        var fi = new FileInfo(path);
                        Reply(new MFilePullOk { RemotePath = path, Size = fi.Length });
                        await Task.Delay(100, ct); // даём клиенту подготовиться к приёму
                        using (var src = File.OpenRead(path))
                        {
                            byte[] buf = new byte[256 * 1024];
                            int n;
                            while ((n = await src.ReadAsync(buf, ct)) > 0)
                            {
                                bool last = src.Position >= src.Length;
                                var chunk = new byte[n];
                                Buffer.BlockCopy(buf, 0, chunk, 0, n);
                                Reply(new MFileData { Chunk = chunk, Last = last });
                            }
                            if (fi.Length == 0) Reply(new MFileData { Chunk = Array.Empty<byte>(), Last = true });
                        }
                        Log.Write($"Файл отправлен: {path}");
                    }
                    catch (Exception ex) { Reply(new MFilePullDeny { Error = ex.Message }); }
                    break;

                case MPower p:
                    switch (p.Action)
                    {
                        case 0: PowerAdmin.Reboot(); ReplyOk("Перезагрузка через 15 сек (отмена: shutdown /a)"); break;
                        case 1: PowerAdmin.Shutdown(); ReplyOk("Выключение через 15 сек (отмена: shutdown /a)"); break;
                        case 2: PowerAdmin.Logoff(); ReplyOk("Выход пользователя"); break;
                    }
                    break;

                case MClipboardSet cs:
                    bool setOk = await AgentClipboard.SetTextAsync(cs.Text);
                    if (setOk) ReplyOk("Текст помещён в буфер обмена ПК");
                    else ReplyError("Не удалось открыть буфер обмена");
                    break;

                case MClipboardGet:
                    string text = await AgentClipboard.GetTextAsync();
                    Reply(new MClipboardData { Text = text });
                    break;

                default: break; // неизвестное — игнорируем
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Обработка команды {msg.Type}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _engineSub?.Dispose(); } catch { }
        try { _recvFile?.Dispose(); } catch { }
        _shell?.Dispose();
    }
}
