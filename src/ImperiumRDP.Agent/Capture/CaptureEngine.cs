using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using ImperiumRDP.Shared.Protocol;

namespace ImperiumRDP.Agent.Capture;

/// <summary>
/// Захват экрана (виртуальный рабочий стол, все мониторы) с поблочным сравнением
/// и JPEG-кодированием изменившихся блоков 64x64. Кадры публикуются подписчикам (сессиям).
/// </summary>
public sealed class CaptureEngine : ICaptureSource
{
    public const int TileSize = 64;
    public const byte CodecId = 0;
    public int Codec => CodecId;

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int Flags; public IntPtr hCursor; public POINT pt; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    private const uint DI_NORMAL = 3;
    private const int CURSOR_SHOWING = 1;

    private readonly object _subLock = new();
    private List<Action<MsgType, byte[]>> _subs = new();

    private volatile int _fps = 15;
    private volatile int _quality = 60;
    private volatile bool _keyframe = true;
    private readonly ManualResetEventSlim _wake = new(false);
    private volatile bool _running = true;
    private Thread _thread;

    private Rectangle _screen;              // координаты виртуального рабочего стола
    private Bitmap _frame;                  // текущий кадр (размером экрана)
    private byte[] _bufA, _bufB;            // двойной буфер для сравнения
    private bool _prevValid;
    private readonly Stopwatch _keyframeTimer = Stopwatch.StartNew();
    private readonly Action<string> _log;

    private EncoderParameter _qualityParam;
    private readonly EncoderParameters _encParams = new(1);
    private readonly ImageCodecInfo _jpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");

    public CaptureEngine(Action<string> log)
    {
        _log = log;
        _qualityParam = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)_quality);
        _encParams.Param[0] = _qualityParam;
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "CaptureEngine" };
        _thread.Start();
    }

    public Rectangle ScreenRect => _screen;

    public IDisposable Subscribe(Action<MsgType, byte[]> handler)
    {
        lock (_subLock) { _subs = new List<Action<MsgType, byte[]>>(_subs) { handler }; }
        _keyframe = true;
        _wake.Set();
        return new Unsub(this, handler);
    }

    private sealed class Unsub : IDisposable
    {
        private CaptureEngine _e; private readonly Action<MsgType, byte[]> _h;
        public Unsub(CaptureEngine e, Action<MsgType, byte[]> h) { _e = e; _h = h; }
        public void Dispose()
        {
            lock (_e._subLock) { var list = new List<Action<MsgType, byte[]>>(_e._subs); list.Remove(_h); _e._subs = list; }
            _e = null;
        }
    }

    public void SetOptions(int fps, int quality)
    {
        if (fps is >= 1 and <= 60) _fps = fps;
        if (quality is >= 10 and <= 95)
        {
            _quality = quality;
            lock (this)
            {
                _qualityParam.Dispose();
                _qualityParam = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                _encParams.Param[0] = _qualityParam;
            }
            _keyframe = true; _wake.Set();
        }
    }

    public void RequestKeyframe() { _keyframe = true; _wake.Set(); }

    private static Rectangle QueryScreenRect() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    private void Loop()
    {
        while (_running)
        {
            try
            {
                if (Volatile.Read(ref _subs).Count == 0)
                {
                    _wake.Wait(300);
                    _wake.Reset();
                    continue;
                }

                int delay = Math.Max(1000 / Math.Max(1, _fps), 10);
                var sw = Stopwatch.StartNew();

                var rect = QueryScreenRect();
                if (rect.Width <= 0 || rect.Height <= 0) { Thread.Sleep(500); continue; }

                if (_frame is null || _frame.Width != rect.Width || _frame.Height != rect.Height)
                {
                    _frame?.Dispose();
                    _frame = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppRgb);
                    _bufA = new byte[(long)rect.Width * rect.Height * 4];
                    _bufB = new byte[(long)rect.Width * rect.Height * 4];
                    _prevValid = false;
                    _keyframe = true;
                    _screen = rect;
                    PublishInfo();
                }
                else if (_screen.X != rect.X || _screen.Y != rect.Y)
                {
                    _screen = rect;
                    _keyframe = true;
                    PublishInfo();
                }

                CaptureFrame();
                var tiles = DiffTiles(forceAll: false);
                // активный экран — ключевой кадр каждые 3 с; неподвижный — раз в 15 с (экономия канала)
                bool keyDue = _keyframe || !_prevValid ||
                    _keyframeTimer.ElapsedMilliseconds > (tiles.Count > 0 ? 3000 : 15000);
                if (keyDue) tiles = DiffTiles(forceAll: true);

                if (tiles.Count > 0)
                {
                    var msg = EncodeBatch(tiles);
                    _prevValid = true;
                    if (keyDue) { _keyframeTimer.Restart(); _keyframe = false; }
                    List<Action<MsgType, byte[]>> subs;
                    lock (_subLock) subs = _subs;
                    if (subs.Count > 0)
                    {
                        byte[] payload = msg.Encode();
                        foreach (var s in subs) { try { s(MsgType.TileBatch, payload); } catch { } }
                    }
                }

                int rest = delay - (int)sw.ElapsedMilliseconds;
                if (rest > 0) _wake.Wait(rest);
                else _wake.Reset();
                _wake.Reset();
            }
            catch (Exception ex)
            {
                _log($"CaptureEngine: {ex.Message}");
                Thread.Sleep(500);
            }
        }
    }

    private void PublishInfo()
    {
        var info = new MScreenInfo { Vx = _screen.X, Vy = _screen.Y, Vw = _screen.Width, Vh = _screen.Height };
        byte[] payload = info.Encode();
        List<Action<MsgType, byte[]>> subs;
        lock (_subLock) subs = _subs;
        foreach (var s in subs) { try { s(MsgType.ScreenInfo, payload); } catch { } }
    }

    private void CaptureFrame()
    {
        using var g = Graphics.FromImage(_frame);
        g.CopyFromScreen(_screen.X, _screen.Y, 0, 0, new Size(_screen.Width, _screen.Height));
        DrawCursor(g);
    }

    private void DrawCursor(Graphics g)
    {
        try
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci) || ci.Flags != CURSOR_SHOWING || ci.hCursor == IntPtr.Zero) return;
            var ii = default(ICONINFO);
            int hx = 0, hy = 0;
            if (GetIconInfo(ci.hCursor, out ii))
            {
                if (!ii.fIcon) { hx = ii.xHotspot; hy = ii.yHotspot; }
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            IntPtr hdc = g.GetHdc();
            try
            {
                DrawIconEx(hdc, ci.pt.X - _screen.X - hx, ci.pt.Y - _screen.Y - hy,
                    ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
            }
            finally { g.ReleaseHdc(); }
        }
        catch { /* курсор не критичен */ }
    }

    /// <summary>Сравнивает текущий кадр с предыдущим по блокам; возвращает индексы изменившихся.</summary>
    private List<(int tx, int ty)> DiffTiles(bool forceAll)
    {
        var data = _frame.LockBits(new Rectangle(0, 0, _frame.Width, _frame.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int w = _frame.Width, h = _frame.Height, stride = data.Stride;
            long bufLen = (long)w * h * 4;
            byte[] cur = _bufA;
            if (stride == w * 4)
            {
                Marshal.Copy(data.Scan0, cur, 0, (int)bufLen);
            }
            else
            {
                for (int y = 0; y < h; y++)
                {
                    IntPtr rowPtr = IntPtr.Add(data.Scan0, y * stride);
                    Marshal.Copy(rowPtr, cur, y * w * 4, w * 4);
                }
            }

            var result = new List<(int, int)>(64);
            int cols = (w + TileSize - 1) / TileSize;
            int rows = (h + TileSize - 1) / TileSize;

            if (forceAll || !_prevValid)
            {
                for (int ty = 0; ty < rows; ty++)
                    for (int tx = 0; tx < cols; tx++)
                        result.Add((tx, ty));
                return result;
            }

            byte[] prev = _bufB;
            for (int ty = 0; ty < rows; ty++)
            {
                int py = ty * TileSize;
                int ph = Math.Min(TileSize, h - py);
                for (int tx = 0; tx < cols; tx++)
                {
                    int px = tx * TileSize;
                    int pw = Math.Min(TileSize, w - px);
                    bool dirty = false;
                    for (int row = 0; row < ph; row++)
                    {
                        long off = ((long)(py + row) * w + px) * 4;
                        if (!cur.AsSpan((int)off, pw * 4).SequenceEqual(prev.AsSpan((int)off, pw * 4)))
                        { dirty = true; break; }
                    }
                    if (dirty) result.Add((tx, ty));
                }
            }
            return result;
        }
        finally { _frame.UnlockBits(data); }
    }

    private MTileBatch EncodeBatch(List<(int tx, int ty)> tiles)
    {
        var msg = new MTileBatch
        {
            Vx = _screen.X, Vy = _screen.Y, Vw = _screen.Width, Vh = _screen.Height,
            Tiles = new List<MTileBatch.Tile>(tiles.Count)
        };
        foreach (var (tx, ty) in tiles)
        {
            int px = tx * TileSize, py = ty * TileSize;
            int tw = Math.Min(TileSize, _frame.Width - px);
            int th = Math.Min(TileSize, _frame.Height - py);
            using var tileBmp = _frame.Clone(new Rectangle(px, py, tw, th), PixelFormat.Format32bppRgb);
            using var ms = new MemoryStream(4096);
            lock (this) { tileBmp.Save(ms, _jpegCodec, _encParams); }
            msg.Tiles.Add(new MTileBatch.Tile { Tx = tx, Ty = ty, Data = ms.ToArray() });
        }
        // буферы меняются местами: текущий становится эталоном для следующего сравнения
        (_bufA, _bufB) = (_bufB, _bufA);
        return msg;
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        try { _thread?.Join(1500); } catch { }
        _frame?.Dispose();
        _qualityParam?.Dispose();
        _encParams?.Dispose();
    }
}
