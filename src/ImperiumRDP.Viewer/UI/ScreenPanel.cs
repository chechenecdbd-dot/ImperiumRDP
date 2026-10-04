using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using ImperiumRDP.Shared.Protocol;

namespace ImperiumRDP.Viewer.UI;

/// <summary>
/// Холст удалённого экрана: рисует канву с сохранением пропорций и преобразует
/// локальный ввод в события с относительными координатами (доли ширины/высоты).
/// </summary>
public sealed class ScreenPanel : Control
{
    public const int TileSize = 64;

    private Bitmap _canvas;
    private Graphics _canvasG;
    private readonly object _sync = new();
    private RectangleF _dst;

    public bool InputEnabled { get; set; }
    public bool IsFullscreen { get; set; }
    public event Action EscapeRequested;

    // курсор: форма приходит редко, позиция — с каждым кадром (кодек H.264)
    private Bitmap _cursorBmp;
    private Point _cursorHot;
    private PointF _cursorPos;      // доли 0..1 удалённого экрана
    private bool _cursorVisible;

    // доли (0..1) координаты удалённого экрана
    public event Action<float, float> RemoteMouseMove;
    public event Action<byte, bool, float, float> RemoteMouseButton; // кнопка, down
    public event Action<int, float, float> RemoteWheel;
    public event Action<int, bool> RemoteKey;                        // VK, down
    public event Action<char> RemoteChar;                            // юникодный текст

    public ScreenPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        BackColor = Color.FromArgb(24, 24, 28);
    }

    public Size CanvasSize
    {
        get { lock (_sync) { return _canvas?.Size ?? Size.Empty; } }
    }

    /// <summary>Вызывается из сетевого потока (через BeginInvoke вызывающей формы).</summary>
    public void ApplyScreenInfo(int w, int h)
    {
        lock (_sync)
        {
            if (_canvas != null && _canvas.Width == w && _canvas.Height == h) return;
            _canvasG?.Dispose();
            _canvas?.Dispose();
            _canvas = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppRgb);
            _canvasG = Graphics.FromImage(_canvas);
        }
        Invalidate();
    }

    public void ApplyTiles(MTileBatch batch)
    {
        lock (_sync)
        {
            if (_canvas == null || _canvasG == null) return;
            foreach (var t in batch.Tiles)
            {
                using var ms = new MemoryStream(t.Data);
                using var bmp = new Bitmap(ms);
                _canvasG.DrawImage(bmp, t.Tx * TileSize, t.Ty * TileSize, bmp.Width, bmp.Height);
            }
        }
        Invalidate();
    }

    /// <summary>Полный кадр BGRA (H.264-путь): копия в постоянную канву без аллокаций.</summary>
    public void UploadFullFrame(int w, int h, byte[] bgra)
    {
        lock (_sync)
        {
            if (_canvas == null || _canvas.Width != w || _canvas.Height != h)
            {
                _canvasG?.Dispose();
                _canvas?.Dispose();
                _canvas = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                _canvasG = Graphics.FromImage(_canvas);
            }
            var rect = new Rectangle(0, 0, w, h);
            var bd = _canvas.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bd.Scan0, w * h * 4);
            }
            finally { _canvas.UnlockBits(bd); }
        }
        Invalidate();
    }

    /// <summary>Позиция курсора (доли экрана) — вызывается из сетевого потока.</summary>
    public void SetCursorState(int x, int y, int vw, int vh, bool visible)
    {
        if (vw <= 0 || vh <= 0) return;
        _cursorPos = new PointF((float)x / vw, (float)y / vh);
        _cursorVisible = visible;
        Invalidate();
    }

    /// <summary>Форма курсора (BGRA) — приходит при смене формы.</summary>
    public void SetCursorShape(int w, int h, int hotX, int hotY, byte[] bgra)
    {
        try
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bd.Scan0, w * h * 4);
            bmp.UnlockBits(bd);
            var old = _cursorBmp;
            _cursorBmp = bmp;
            _cursorHot = new Point(hotX, hotY);
            old?.Dispose();
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        lock (_sync)
        {
            if (_canvas == null) return;
            var g = e.Graphics;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            _dst = FitRect(ClientSize, _canvas.Size);
            g.DrawImage(_canvas, _dst);
            g.DrawRectangle(Pens.DimGray, _dst.X, _dst.Y, _dst.Width, _dst.Height);

            // курсор удалённого ПК поверх картинки
            if (_cursorVisible && _cursorBmp != null)
            {
                float cx = _dst.Left + _cursorPos.X * _dst.Width - _cursorHot.X;
                float cy = _dst.Top + _cursorPos.Y * _dst.Height - _cursorHot.Y;
                g.DrawImage(_cursorBmp, cx, cy, _cursorBmp.Width, _cursorBmp.Height);
            }
        }
    }

    private static RectangleF FitRect(Size client, Size image)
    {
        if (image.Width == 0 || image.Height == 0 || client.Width <= 0 || client.Height <= 0)
            return new RectangleF(0, 0, Math.Max(1, client.Width), Math.Max(1, client.Height));
        float scale = Math.Min((float)client.Width / image.Width, (float)client.Height / image.Height);
        float w = image.Width * scale, h = image.Height * scale;
        float x = (client.Width - w) / 2f, y = (client.Height - h) / 2f;
        return new RectangleF(x, y, w, h);
    }

    private bool TryMap(Point p, out float fx, out float fy)
    {
        fx = fy = 0;
        if (_dst.Width <= 0 || _dst.Height <= 0) return false;
        if (p.X < _dst.Left || p.X > _dst.Right || p.Y < _dst.Top || p.Y > _dst.Bottom) return false;
        fx = (p.X - _dst.Left) / _dst.Width;
        fy = (p.Y - _dst.Top) / _dst.Height;
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!InputEnabled) return;
        if (TryMap(e.Location, out var fx, out var fy)) RemoteMouseMove?.Invoke(fx, fy);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (!InputEnabled) return;
        if (TryMap(e.Location, out var fx, out var fy))
            RemoteMouseButton?.Invoke(ToRemoteButton(e.Button), true, fx, fy);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!InputEnabled) return;
        if (TryMap(e.Location, out var fx, out var fy))
            RemoteMouseButton?.Invoke(ToRemoteButton(e.Button), false, fx, fy);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!InputEnabled) return;
        if (TryMap(e.Location, out var fx, out var fy))
            RemoteWheel?.Invoke(e.Delta, fx, fy);
    }

    private static byte ToRemoteButton(MouseButtons b) => b switch
    {
        MouseButtons.Left => 0,
        MouseButtons.Right => 1,
        MouseButtons.Middle => 2,
        _ => 0,
    };

    protected override bool IsInputKey(Keys keyData)
    {
        // чтобы работали стрелки/Tab/Enter и не теряли фокус
        return InputEnabled && keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right
               or Keys.Tab or Keys.Enter or Keys.Escape or Keys.PageUp or Keys.PageDown
               or Keys.Home or Keys.End or Keys.Delete or Keys.Insert
               ? true
               : base.IsInputKey(keyData);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (!InputEnabled) return;
        // печатаемые символы отправляем юникодом — работает с любой раскладкой
        if (!char.IsControl(e.KeyChar))
        {
            RemoteChar?.Invoke(e.KeyChar);
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!InputEnabled || e.Handled) return;
        if (IsTextKey(e.KeyCode, e.Modifiers & ~Keys.Shift)) { e.Handled = true; return; }
        RemoteKey?.Invoke((int)e.KeyCode, false);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!InputEnabled) return;
        if (IsFullscreen && e.KeyCode == Keys.Escape)
        {
            EscapeRequested?.Invoke();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (IsTextKey(e.KeyCode, e.Modifiers & ~Keys.Shift))
        {
            e.Handled = true;   // символ придёт через OnKeyPress (юникод)
            e.SuppressKeyPress = true;
            return;
        }
        RemoteKey?.Invoke((int)e.KeyCode, true);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    /// <summary>Клавиши, дающие текст — их отправляем как юникод из OnKeyPress, а не скан-кодом.</summary>
    public static bool IsTextKey(Keys key, Keys modifiers)
    {
        if ((modifiers & (Keys.Control | Keys.Alt)) != 0) return false;
        bool printable =
            (key >= Keys.A && key <= Keys.Z) ||
            (key >= Keys.D0 && key <= Keys.D9) ||
            (key >= Keys.OemSemicolon && key <= Keys.OemBackslash) ||
            key == Keys.Space || key == Keys.Oemtilde || key == Keys.Oemcomma ||
            key == Keys.OemPeriod || key == Keys.OemMinus || key == Keys.Oemplus || key == Keys.OemQuotes;
        return printable;
    }
}
