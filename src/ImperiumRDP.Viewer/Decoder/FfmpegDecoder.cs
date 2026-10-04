using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace ImperiumRDP.Viewer.Decoder;

/// <summary>
/// Декодер H.264 через ffmpeg (отдельный процесс, stdin/stdout pipes).
/// На вход — кадры Annex-B, на выход — BGRA фиксированного размера (width x height x 4).
/// </summary>
public sealed class FfmpegDecoder : IDisposable
{
    private Process _proc;
    private readonly int _width, _height;
    private readonly long _frameBytes;
    private readonly BlockingCollection<byte[]> _inQueue = new(new ConcurrentQueue<byte[]>(), 120);
    private Task _writer, _reader;
    private bool _disposed;

    /// <summary>Вызывается на каждый декодированный кадр (поток reader'а).</summary>
    public event Action<byte[]> FrameReady;

    public string LastStatus { get; private set; } = "";

    /// <summary>Жив ли ffmpeg.</summary>
    public bool IsAlive => _proc != null && !_proc.HasExited;
    public int ExitCode => _proc == null || !_proc.HasExited ? 0 : _proc.ExitCode;
    public int Queued => _inQueue.Count;

    /// <param name="width">Ожидаемая ширина (из ScreenInfo) — размер выходного кадра.</param>
    public FfmpegDecoder(int width, int height)
    {
        _width = width & ~1;
        _height = height & ~1;
        _frameBytes = (long)_width * _height * 4;
    }

    private static string ExePath()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        return File.Exists(local) ? local : "ffmpeg"; // fallback: из PATH (dev-машина)
    }

    public void Start()
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExePath(),
            Arguments = $"-hide_banner -loglevel error -probesize 1M -analyzeduration 0 -fflags nobuffer -flags low_delay -f h264 -i pipe:0 -f rawvideo -pix_fmt bgra -vf scale={_width}:{_height} -an -sn -dn pipe:1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.ASCII,
            // TODO: попробовать -hwaccel cuda для аппаратного декодирования на NVIDIA;
            // при отсутствии GPU нужен фолбэк на программное декодирование (текущий путь).
        };
        //PipeTransmissionMode.Message не нужен — просто байтовые потоки
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg не запустился");
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) LastStatus += e.Data + " "; };
        _proc.BeginErrorReadLine();

        _writer = Task.Run(WriterLoop);
        _reader = Task.Run(ReaderLoop);
    }

    private async Task WriterLoop()
    {
        try
        {
            var stdin = _proc.StandardInput.BaseStream;
            foreach (var frame in _inQueue.GetConsumingEnumerable())
            {
                await stdin.WriteAsync(frame, 0, frame.Length);
                await stdin.FlushAsync();
            }
            await stdin.FlushAsync();
        }
        catch (Exception ex)
        {
            LastStatus += "[writer: " + ex.Message + "]";
        }
    }

    private async Task ReaderLoop()
    {
        var stdout = _proc.StandardOutput.BaseStream;
        var frame = new byte[_frameBytes];
        try
        {
            while (!_disposed)
            {
                int off = 0;
                while (off < frame.Length)
                {
                    int n = await stdout.ReadAsync(frame.AsMemory(off, frame.Length - off));
                    if (n <= 0)
                    {
                        LastStatus += $"[stdout EOF, ffmpeg exit={(_proc.HasExited ? _proc.ExitCode.ToString() : "?")}]";
                        return; // ffmpeg закрылся
                    }
                    off += n;
                }
                FrameReady?.Invoke(frame);
                frame = new byte[_frameBytes]; // свой буфер на каждый кадр (уходит потребителю)
            }
        }
        catch (Exception ex)
        {
            LastStatus += "[reader: " + ex.Message + "]";
        }
    }

    /// <summary>Поставить кадр Annex-B в очередь входа (неблокирующе).</summary>
    public void Push(byte[] frame)
    {
        if (_disposed) return;
        if (!_inQueue.TryAdd(frame)) LastStatus = "входная очередь полна";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _inQueue.CompleteAdding(); } catch { }
        try { if (!_writer?.IsCompleted ?? false) _writer?.Wait(1000); } catch { }
        try { _proc?.Kill(entireProcessTree: true); } catch { }
        try { _proc?.Dispose(); } catch { }
    }
}
