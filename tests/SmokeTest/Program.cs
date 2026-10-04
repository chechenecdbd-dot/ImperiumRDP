using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ImperiumRDP.Shared;
using ImperiumRDP.Shared.Protocol;
using ImperiumRDP.Viewer.Net;

// Сквозной тест: сам запускает агента в изолированном каталоге (IMPERIUMRDP_HOME),
// принимает его uplink через UplinkServer (как панель из дома) и проверяет
// видеопоток NVENC→ffmpeg и передачу файлов. Без ручной подготовки.

int failures = 0;
string home = Path.Combine(Path.GetTempPath(), "irdp_smoke_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(home);
Environment.SetEnvironmentVariable("IMPERIUMRDP_HOME", home);

string password = "SmokeTest_Pw_2026";
int lanPort = GetFreePort();
int uplinkPort = GetFreePort();
File.WriteAllText(Path.Combine(home, "agent.json"),
    $$"""{"port":{{lanPort}},"password":"SmokeTest_Pw_2026","fps":30,"quality":60,"uplink":"127.0.0.1:{{uplinkPort}}"}""");

string? agentExe = FindAgentExe();
if (agentExe == null) { Console.WriteLine("FAIL: агент не найден"); return 1; }

var agentProc = Process.Start(new ProcessStartInfo
{
    FileName = agentExe,
    Arguments = "--hidden",
    UseShellExecute = false,
    CreateNoWindow = true,
});
Console.WriteLine($"агент запущен (home={home})");

var uplink = new UplinkServer(password);
UplinkServer.UplinkAgent? joined = null;
uplink.AgentAdded += a => joined = a;
uplink.Start(uplinkPort);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

try
{
    // ждём, пока агент сам подключится (uplink)
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (joined == null && DateTime.UtcNow < deadline) await Task.Delay(300);
    if (joined == null)
    {
        Console.WriteLine("FAIL: агент не подключился по uplink за 30 с");
        agentProc.Refresh();
        Console.WriteLine($"  agentProc.HasExited={agentProc.HasExited}, exit={agentProc.ExitCode}");
        Console.WriteLine($"  home существует: {Directory.Exists(home)}");
        var logPath = Path.Combine(home, "agent.log");
        if (File.Exists(logPath)) Console.WriteLine("  agent.log:\n" + File.ReadAllText(logPath));
        return 1;
    }
    Console.WriteLine($"OK  uplink: агент «{joined.HostName}» подключился сам, без проброса портов");

    var conn = joined.Connection;

    var vidCh = Channel.CreateUnbounded<MVideoData>();
    var okCh = Channel.CreateUnbounded<MStatus>();
    var fileCh = Channel.CreateUnbounded<MFileData>();
    var cursorShapeCh = Channel.CreateUnbounded<bool>();

    bool sysInfo = false, pong = false, screenInfo = false;
    int h264Frames = 0, decodedFrames = 0;
    byte codec = 255;
    long bytes = 0;
    int vw = 0, vh = 0;

    var decoder = (ImperiumRDP.Viewer.Decoder.FfmpegDecoder?)null;
    int decW = 0;

    conn.Received += m =>
    {
        bytes += 0;
        switch (m)
        {
            case MVideoData v:
                Interlocked.Increment(ref h264Frames);
                using (var fs = new FileStream("stream.h264", FileMode.Append, FileAccess.Write))
                    fs.Write(v.Data, 0, v.Data.Length);
                if (vw > 0 && decW != vw)
                {
                    // создаём декодер только когда известен реальный размер экрана
                    decoder?.Dispose();
                    decW = vw;
                    decoder = new ImperiumRDP.Viewer.Decoder.FfmpegDecoder(vw, vh);
                    decoder.FrameReady += _ => Interlocked.Increment(ref decodedFrames);
                    decoder.Start();
                }
                if (vw > 0) decoder?.Push(v.Data);
                break;
            case MCursorShape: cursorShapeCh.Writer.TryWrite(true); break;
            case MSysInfo: sysInfo = true; break;
            case MScreenInfo si:
                codec = si.Codec; vw = si.Vw; vh = si.Vh; screenInfo = true;
                break;
            case MPong: pong = true; break;
            case MStatus st: okCh.Writer.TryWrite(st); break;
            case MFileData fd: fileCh.Writer.TryWrite(fd); break;
        }
    };
    conn.StartLoops();

    // запрашиваем видео
    conn.Send(new MRequestKeyframe());
    conn.Send(new MSetOptions { Fps = 15, Quality = 50 });
    conn.Send(new MSysInfoReq());
    conn.Send(new MPing { Stamp = Stopwatch.GetTimestamp() });
    conn.Send(new MClipboardSet { Text = "smoke-test" });

    var waitDeadline = DateTime.UtcNow.AddSeconds(12);
    while (DateTime.UtcNow < waitDeadline && (h264Frames < 3 || !sysInfo || !pong))
        await Task.Delay(200);

    // probesize 1M: докачиваем поток повтором последнего кадра, пока ffmpeg не начнёт выдавать
    if (decoder != null && decodedFrames == 0)
    {
        for (int i = 0; i < 40 && decodedFrames == 0 && !cts.IsCancellationRequested; i++)
        {
            if (File.Exists("stream.h264"))
            {
                var all = await File.ReadAllBytesAsync("stream.h264");
                decoder.Push(all);
            }
            await Task.Delay(200);
        }
        await Task.Delay(1500);
    }
    Console.WriteLine($"OK  ScreenInfo: {vw}x{vh}, кодек {(codec == 1 ? "H.264/NVENC" : codec == 0 ? "JPEG" : "?")}");
    Console.WriteLine($"OK  H.264 кадров: {h264Frames}, декодировано ffmpeg: {decodedFrames}, декодер вх={decW}");
    Console.WriteLine($"  ffmpeg stderr/alive: [{(decoder != null ? decoder.LastStatus : "")}] alive={(decoder != null && decoder.IsAlive)}");
    Console.WriteLine($"OK  SysInfo={sysInfo}, Pong={pong}, форм курсора={cursorShapeCh.Reader.Count}");
    if (!sysInfo) { Console.WriteLine("FAIL: SysInfo"); failures++; }
    if (!pong) { Console.WriteLine("FAIL: Pong"); failures++; }
    if (h264Frames < 1) { Console.WriteLine("FAIL: видеопоток не пошёл"); failures++; }
    else if (decodedFrames == 0) { Console.WriteLine("FAIL: кадры не декодируются"); failures++; }

    // --- файлы ---
    var data = new byte[300_000];
    new Random(42).NextBytes(data);
    string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
    await conn.SendAsync(new MFilePushStart { RemotePath = "%TEMP%\\irdp_smoke.bin", Size = data.Length });
    for (int off = 0; off < data.Length; off += 256 * 1024)
    {
        int len = Math.Min(256 * 1024, data.Length - off);
        var chunk = new byte[len];
        Buffer.BlockCopy(data, off, chunk, 0, len);
        await conn.SendAsync(new MFileData { Chunk = chunk, Last = off + len >= data.Length });
    }
    bool savedOk = false;
    var w2 = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < w2 && !savedOk)
    {
        var st = await okCh.Reader.ReadAsync(cts.Token);
        if (st.Text.StartsWith("Файл сохранён")) savedOk = true;
    }
    if (!savedOk) { Console.WriteLine("FAIL: файл не сохранён агентом"); failures++; }

    await conn.SendAsync(new MFilePull { RemotePath = "%TEMP%\\irdp_smoke.bin" });
    var back = new MemoryStream();
    bool done = false;
    w2 = DateTime.UtcNow.AddSeconds(15);
    while (DateTime.UtcNow < w2 && !done)
    {
        var fd = await fileCh.Reader.ReadAsync(cts.Token);
        back.Write(fd.Chunk, 0, fd.Chunk.Length);
        done = fd.Last;
    }
    string shaBack = done ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(back.ToArray())) : "";
    if (sha == shaBack) Console.WriteLine("OK  файл 300 КБ туда-обратно, SHA256 совпадает");
    else { Console.WriteLine("FAIL: файл/хэш"); failures++; }

    decoder.Dispose();
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
    failures++;
}
finally
{
    uplink.Dispose();
    try { agentProc?.Kill(entireProcessTree: true); } catch { }
    if (failures > 0) Console.WriteLine($"(дом агента сохранён для отладки: {home})");
    else try { Directory.Delete(home, true); } catch { }
}

Console.WriteLine(failures == 0 ? "=== SMOKE TEST PASSED ===" : $"=== SMOKE TEST FAILED ({failures}) ===");
return failures == 0 ? 0 : 1;

static string? FindAgentExe()
{
    string? dir = AppContext.BaseDirectory;
    for (int i = 0; i < 8 && dir != null; i++)
    {
        var candidate = Path.Combine(dir, "src", "ImperiumRDP.Agent", "bin", "Release", "net8.0-windows", "ImperiumRDP.Agent.exe");
        if (File.Exists(candidate)) return candidate;
        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
    }
    return null;
}

static int GetFreePort()
{
    var l = System.Net.Sockets.TcpListener.Create(0);
    l.Start();
    int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}
