using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using ImperiumRDP.Shared;
using ImperiumRDP.Shared.Protocol;
using ImperiumRDP.Viewer.Net;

namespace ImperiumRDP.Viewer.UI;

public sealed class SessionForm : Form, IMessageFilter
{
    private readonly Machine _machine;
    private readonly ViewerSettings _settings;
    private ClientConnection _conn;

    private readonly ScreenPanel _screen = new();
    private readonly ToolStripStatusLabel _stPing = new("Пинг: —");
    private readonly ToolStripStatusLabel _stSpeed = new("0 КБ/с");
    private readonly ToolStripStatusLabel _stFps = new("0 к/с");
    private readonly ToolStripStatusLabel _stStatus = new("Подключение...");

    private CheckBox _chkInput;
    private TrackBar _trackQuality;
    private NumericUpDown _numFps;
    private Label _lblQuality;

    private System.Windows.Forms.Timer _pingTimer, _statsTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastPing = -1;
    private long _lastBytes;
    private int _tileCount;
    private DateTime _lastMouseMove = DateTime.MinValue;
    private FormBorderStyle _savedStyle;
    private FormWindowState _savedState;

    private int _vx, _vy, _vw, _vh;

    // дочерние окна (создаются по требованию)
    private ShellForm _shellForm;
    private ProcessesForm _procForm;
    private FilesForm _filesForm;
    private InfoForm _infoForm;

    // приём файла от агента
    private string _pendingPullLocal;
    private FileStream _pullStream;

    // H.264-декодирование (ffmpeg-процесс)
    private Decoder.FfmpegDecoder _h264;

    // uplink: сессия поверх готового соединения
    private readonly ClientConnection _uplinkConn;
    private readonly MainForm _mainForm;

    // TODO: передача звука удалённого ПК (WASAPI capture на агенте + Opus + playback на панели).
    // TODO: буфер обмена файлами (сейчас — только текст).
    // адаптивное качество
    private bool _autoQuality = true;
    private int _curFps, _curQuality;

    public SessionForm(Machine machine, ViewerSettings settings, MainForm mainForm = null, ClientConnection uplinkConn = null)
    {
        _machine = machine;
        _settings = settings;
        _mainForm = mainForm;
        _uplinkConn = uplinkConn;
        Text = $"ImperiumRDP — {machine.HostName} ({machine.Ip})";
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1100, 700);

        BuildToolbar();
        BuildStatus();

        _screen.Dock = DockStyle.Fill;
        _screen.InputEnabled = true;
        Controls.Add(_screen);
        _screen.BringToFront();

        _screen.RemoteMouseMove += OnRemoteMouseMove;
        _screen.RemoteMouseButton += OnRemoteMouseButton;
        _screen.RemoteWheel += OnRemoteWheel;
        _screen.RemoteKey += OnRemoteKey;
        _screen.RemoteChar += OnRemoteChar;
        _screen.EscapeRequested += () => { if (_screen.IsFullscreen) ToggleFullscreen(); };

        Application.AddMessageFilter(this);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            if (_uplinkConn != null)
            {
                BindConnection(_uplinkConn);
            }
            else
            {
                if (!await TryConnect()) return;
            }

            _pingTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _pingTimer.Tick += (_, _) => _conn?.Send(new MPing { Stamp = Stopwatch.GetTimestamp() });
            _pingTimer.Start();

            _statsTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _statsTimer.Tick += (_, _) => UpdateStats();
            _statsTimer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось подключиться:\n" + ex.Message, "ImperiumRDP",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void BindConnection(ClientConnection conn)
    {
        _conn = conn;
        _curFps = (int)_numFps.Value;
        _curQuality = _trackQuality.Value;
        _recvHandler = m =>
        {
            if (m is MVideoData v) { HandleVideo(v); return; }
            if (m is MCursorState cs)
            {
                _screen.SetCursorState(cs.X, cs.Y, _vw, _vh, cs.Visible);
                return;
            }
            if (m is MCursorShape sh)
            {
                _screen.SetCursorShape(sh.Width, sh.Height, sh.HotX, sh.HotY, sh.Bgra);
                return;
            }
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() => OnMessage(m));
        };
        _closedHandler = reason => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => _ = TryReconnect(reason)); };
        _conn.Received += _recvHandler;
        _conn.Closed += _closedHandler;
        _conn.StartLoops();

        _conn.Send(new MRequestKeyframe());
        _conn.Send(new MSetOptions { Fps = (int)_numFps.Value, Quality = _trackQuality.Value });
        _stStatus.Text = $"Подключено: {_conn.Hello.Host} / пользователь {_conn.Hello.User}";
    }
    private Action<Msg>? _recvHandler;
    private Action<string>? _closedHandler;

    private async Task<bool> TryConnect()
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                _stStatus.Text = attempt == 1 ? "Подключение..." : $"Подключение (попытка {attempt}/3)...";
                var conn = await ClientConnection.ConnectAsync(_machine.Ip, _machine.Port, _settings.Password);
                BindConnection(conn);
                return true;
            }
            catch (Exception ex)
            {
                _stStatus.Text = "Не удалось подключиться: " + ex.Message;
                await Task.Delay(1500);
            }
        }
        Close();
        return false;
    }

    /// <summary>Автопереподключение: сохраняет окно и дочерние панели, обновляет канал.</summary>
    private async Task TryReconnect(string reason)
    {
        if (IsDisposed || _isReconnecting) return;
        _isReconnecting = true;
        try
        {
            _stStatus.Text = $"Соединение потеряно ({reason}). Переподключение...";
            var old = _conn;
            _conn = null;

            for (int attempt = 1; attempt <= 20 && !IsDisposed; attempt++)
            {
                await Task.Delay(3000);
                try
                {
                    ClientConnection fresh = null;
                    if (_machine.Uplink && _machine.HostName.Length > 0)
                    {
                        // ждём, пока агент клуба восстановит свой uplink к панели
                        fresh = _mainForm?.WaitForUplink(_machine.HostName, TimeSpan.FromSeconds(1));
                    }
                    else
                    {
                        fresh = await ClientConnection.ConnectAsync(_machine.Ip, _machine.Port, _settings.Password);
                    }
                    if (fresh != null)
                    {
                        BindConnection(fresh);
                        old?.Dispose();
                        _stStatus.Text = "Переподключено.";
                        _isReconnecting = false;
                        return;
                    }
                    _stStatus.Text = $"Переподключение... попытка {attempt}/20";
                }
                catch { /* тикаем дальше */ }
            }
            MessageBox.Show(this, "Не удалось восстановить соединение.", "ImperiumRDP",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
        finally { _isReconnecting = false; }
    }
    private bool _isReconnecting;

    private void BuildToolbar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(6, 4, 6, 4),
            BackColor = Color.FromArgb(40, 40, 46),
        };

        _chkInput = new CheckBox
        {
            Text = "Управление",
            Checked = true,
            ForeColor = Color.White,
            AutoSize = true,
            Margin = new Padding(4, 8, 12, 2),
        };
        _chkInput.CheckedChanged += (_, _) =>
        {
            _screen.InputEnabled = _chkInput.Checked;
            _stStatus.Text = _chkInput.Checked ? "Управление включено" : "Просмотр (управление выключено)";
        };

        _lblQuality = new Label { Text = "Качество:", ForeColor = Color.White, AutoSize = true, Margin = new Padding(4, 10, 2, 2) };
        _trackQuality = new TrackBar
        {
            Minimum = 10, Maximum = 90, Value = Math.Clamp(_settings.Quality, 10, 90),
            Width = 110, TickStyle = TickStyle.None, Margin = new Padding(2, 2, 8, 2),
        };
        _trackQuality.ValueChanged += (_, _) => SendOptions();

        var lblFps = new Label { Text = "FPS:", ForeColor = Color.White, AutoSize = true, Margin = new Padding(4, 10, 2, 2) };
        _numFps = new NumericUpDown
        {
            Minimum = 2, Maximum = 144, Value = Math.Clamp(_settings.Fps, 2, 144),
            Width = 55, Margin = new Padding(2, 4, 10, 2),
        };
        _numFps.ValueChanged += (_, _) => SendOptions();

        bar.Controls.Add(_chkInput);
        bar.Controls.Add(_lblQuality);
        bar.Controls.Add(_trackQuality);
        bar.Controls.Add(lblFps);
        bar.Controls.Add(_numFps);

        AddButton(bar, "Ключевой кадр", () => _conn?.Send(new MRequestKeyframe()));
        AddButton(bar, "Буфер → ПК", SendClipboardToRemote);
        AddButton(bar, "Буфер ← ПК", GetClipboardFromRemote);
        AddButton(bar, "Файлы", ShowFiles);
        AddButton(bar, "Процессы", ShowProcesses);
        AddButton(bar, "CMD", ShowShell);
        AddButton(bar, "Инфо", () => _conn?.Send(new MSysInfoReq()));

        var sep = new Label { Text = "|", ForeColor = Color.Gray, AutoSize = true, Margin = new Padding(10, 10, 10, 2) };
        bar.Controls.Add(sep);

        AddButton(bar, "Перезагрузка", () => Power(0, "перезагрузить"));
        AddButton(bar, "Выключить", () => Power(1, "выключить"));
        AddButton(bar, "Смена юзера", () => Power(2, "разлогинить"));
        AddButton(bar, "Во весь экран", ToggleFullscreen);
        AddButton(bar, "Отключить", Close);

        Controls.Add(bar);
    }

    private void SendOptions()
    {
        _lblQuality.Text = $"Качество ({_trackQuality.Value}):";
        _settings.Quality = _trackQuality.Value;
        _settings.Fps = (int)_numFps.Value;
        if (_autoQuality)
            _conn?.Send(new MSetOptions { Fps = Math.Min(_curFps, (int)_numFps.Value), Quality = Math.Min(_curQuality, _trackQuality.Value) });
        else
            _conn?.Send(new MSetOptions { Fps = (int)_numFps.Value, Quality = _trackQuality.Value });
    }

    private void AddButton(FlowLayoutPanel bar, string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(2, 3, 2, 2) };
        b.Click += (_, _) => onClick();
        bar.Controls.Add(b);
    }

    private void BuildStatus()
    {
        var strip = new StatusStrip();
        strip.Items.Add(_stStatus);
        strip.Items.Add(new ToolStripStatusLabel("|") { Spring = true, TextAlign = ContentAlignment.MiddleRight });
        strip.Items.Add(_stFps);
        strip.Items.Add(_stSpeed);
        strip.Items.Add(_stPing);
        Controls.Add(strip);
    }

    // ---------- обработка сообщений агента ----------

    private void OnMessage(Msg m)
    {
        if (IsDisposed || !IsHandleCreated) return;
        switch (m)
        {
            case MScreenInfo si:
                (_vx, _vy, _vw, _vh) = (si.Vx, si.Vy, si.Vw, si.Vh);
                _screen.ApplyScreenInfo(si.Vw, si.Vh);
                break;

            case MTileBatch tb:
                if (_vw == 0 || tb.Vw != _vw || tb.Vh != _vh)
                {
                    (_vx, _vy, _vw, _vh) = (tb.Vx, tb.Vy, tb.Vw, tb.Vh);
                    _screen.ApplyScreenInfo(tb.Vw, tb.Vh);
                }
                _screen.ApplyTiles(tb);
                _tileCount++;
                break;

            case MPong pong:
                _lastPing = (Stopwatch.GetTimestamp() - pong.Stamp) * 1000.0 / Stopwatch.Frequency;
                break;

            case MSysInfo info:
                ShowInfo(info.Json);
                break;

            case MProcList pl:
                ShowProcesses();
                _procForm?.UpdateList(pl.Json);
                break;

            case MShellOutput so:
                ShowShell();
                _shellForm?.Append(so.Text, so.IsErr);
                break;

            case MClipboardData cd:
                try { Clipboard.SetText(cd.Text); _stStatus.Text = "Буфер обмена ПК скопирован в ваш буфер"; }
                catch (Exception ex) { _stStatus.Text = "Буфер: " + ex.Message; }
                break;

            case MFilePullOk fp:
                StartPull(fp.RemotePath, fp.Size);
                break;

            case MFileData fd:
                OnPullData(fd);
                break;

            case MFilePullDeny deny:
                MessageBox.Show(this, "Не удалось скачать: " + deny.Error, "ImperiumRDP",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _filesForm?.DownloadFinished(false, deny.Error);
                break;

            case MStatus st:
                _stStatus.Text = st.Text;
                if (st.Type == MsgType.Ok && st.Text.StartsWith("Файл сохранён")) _filesForm?.UploadFinished(true, st.Text);
                break;

            default: break;
        }
    }

    /// <summary>H.264-кадр: ставим в очередь декодеру, кадры приходят событием.</summary>
    private void HandleVideo(MVideoData v)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (_h264 == null)
            {
                int w = Math.Max(_vw, 2), h = Math.Max(_vh, 2);
                _h264 = new Decoder.FfmpegDecoder(w, h);
                _h264.FrameReady += bgra =>
                {
                    try { BeginInvoke(() => { if (!IsDisposed) { _screen.UploadFullFrame(w, h, bgra); _tileCount++; } }); }
                    catch { }
                };
                _h264.Start();
            }
            _h264.Push(v.Data);
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            try { BeginInvoke(() => _stStatus.Text = "Декодирование: " + msg); } catch { }
        }
    }

    private void Power(byte action, string verb)
    {
        if (MessageBox.Show(this, $"Действительно {verb} ПК «{_machine.HostName}»?", "Подтверждение",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _conn?.Send(new MPower { Action = action });
        _stStatus.Text = $"Команда питания отправлена ({verb})";
    }

    private void SendClipboardToRemote()
    {
        string text = "";
        try { text = Clipboard.GetText(); } catch { }
        if (string.IsNullOrEmpty(text)) { _stStatus.Text = "Ваш буфер обмена пуст"; return; }
        _conn?.Send(new MClipboardSet { Text = text });
    }

    private void GetClipboardFromRemote()
    {
        _stStatus.Text = "Запрос буфера обмена...";
        _conn?.Send(new MEmpty(MsgType.ClipboardGet));
    }

    // ---------- ввод ----------

    private void OnRemoteMouseMove(float fx, float fy)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastMouseMove).TotalMilliseconds < 8) return;
        _lastMouseMove = now;
        _conn?.Send(new MMouseMove { X = _vx + (int)(fx * _vw), Y = _vy + (int)(fy * _vh) });
    }

    private void OnRemoteMouseButton(byte button, bool down, float fx, float fy)
        => _conn?.Send(new MMouseButton { Button = button, Down = down, X = _vx + (int)(fx * _vw), Y = _vy + (int)(fy * _vh) });

    private void OnRemoteWheel(int delta, float fx, float fy)
        => _conn?.Send(new MMouseWheel { Delta = delta, X = _vx + (int)(fx * _vw), Y = _vy + (int)(fy * _vh) });

    private void OnRemoteKey(int vk, bool down) => _conn?.Send(new MKey { Vk = vk, Down = down });

    private void OnRemoteChar(char ch) => _conn?.Send(new MText { Chars = ch.ToString() });

    // фильтр системных клавиш (Alt-комбинации) + отсечение автоповтора
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    public bool PreFilterMessage(ref Message m)
    {
        if (!_screen.InputEnabled) return false;
        if (ActiveForm != this) return false;
        if (ActiveControl != _screen) return false;

        if (m.Msg == WM_SYSKEYDOWN || m.Msg == WM_SYSKEYUP)
        {
            var vk = (Keys)((long)m.WParam & 0xFFFF);
            OnRemoteKey((int)vk, m.Msg == WM_SYSKEYDOWN);
            return true;
        }
        if (m.Msg == WM_KEYDOWN)
        {
            bool repeat = ((long)m.LParam & (1 << 30)) != 0;
            if (repeat) return true; // OnKeyDown уже отправил нажатие
        }
        return false;
    }

    // ---------- дочерние окна ----------

    private void ShowShell()
    {
        if (_shellForm is null || _shellForm.IsDisposed)
        {
            _shellForm = new ShellForm(line => _conn?.Send(new MShellInput { Line = line }), CloseShell);
            _shellForm.Show(this);
            _conn?.Send(new MEmpty(MsgType.ShellStart));
        }
        else _shellForm.Activate();
    }

    private void CloseShell() => _conn?.Send(new MEmpty(MsgType.ShellStop));

    private void ShowProcesses()
    {
        if (_procForm is null || _procForm.IsDisposed)
        {
            _procForm = new ProcessesForm(
                refresh: () => _conn?.Send(new MEmpty(MsgType.ProcListReq)),
                kill: pid => _conn?.Send(new MKillProc { Pid = pid }),
                run: cmd => _conn?.Send(new MRunProgram { Command = cmd }));
            _procForm.Show(this);
            _conn?.Send(new MEmpty(MsgType.ProcListReq));
        }
        else _procForm.Activate();
    }

    private void ShowFiles()
    {
        if (_filesForm is null || _filesForm.IsDisposed)
        {
            _filesForm = new FilesForm(this);
            _filesForm.Show(this);
        }
        else _filesForm.Activate();
    }

    private void ShowInfo(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<SysInfoDto>(json);
            if (_infoForm is null || _infoForm.IsDisposed)
            {
                _infoForm = new InfoForm();
                _infoForm.Show(this);
            }
            _infoForm.Show(dto);
            _infoForm.Activate();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Инфо"); }
    }

    internal ClientConnection Connection => _conn;

    // ---------- приём файла ----------

    internal void BeginPull(string remotePath, string localPath)
    {
        _pendingPullLocal = localPath;
        _conn?.Send(new MFilePull { RemotePath = remotePath });
    }

    private void StartPull(string remotePath, long size)
    {
        try
        {
            _pullStream?.Dispose();
            _pullStream = new FileStream(_pendingPullLocal ?? Path.GetTempFileName(), FileMode.Create, FileAccess.Write, FileShare.None);
            _stStatus.Text = $"Скачивание {remotePath} ({size / 1024} КБ)...";
            _filesForm?.DownloadStarted(remotePath, size);
        }
        catch (Exception ex)
        {
            _filesForm?.DownloadFinished(false, ex.Message);
        }
    }

    private void OnPullData(MFileData fd)
    {
        try
        {
            if (_pullStream != null)
            {
                _pullStream.Write(fd.Chunk, 0, fd.Chunk.Length);
                if (fd.Last)
                {
                    _pullStream.Dispose(); _pullStream = null;
                    _stStatus.Text = "Файл скачан";
                    _filesForm?.DownloadFinished(true, null);
                }
                else _filesForm?.DownloadProgress(fd.Chunk.Length);
            }
        }
        catch (Exception ex) { _filesForm?.DownloadFinished(false, ex.Message); }
    }

    // ---------- прочее ----------

    private void UpdateStats()
    {
        if (_conn == null) return;
        long bytes = _conn.TotalBytesReceived + _conn.TotalBytesSent;
        double kbs = (bytes - _lastBytes) / 1024.0 / 0.5;
        _lastBytes = bytes;
        _stSpeed.Text = $"{kbs:F0} КБ/с";
        _stFps.Text = $"{_tileCount * 2} к/с";
        _stPing.Text = _lastPing >= 0 ? $"Пинг: {_lastPing:F0} мс" : "Пинг: —";
        _tileCount = 0;

        // TODO: передача звука удалённого ПК (WASAPI capture на агенте + Opus + playback на панели).
    // TODO: буфер обмена файлами (сейчас — только текст).
    // адаптивное качество: держим канал комфортным, не превышая ручные ползунки
        if (_autoQuality && _lastPing >= 0)
        {
            if (_lastPing > 180 && _curQuality > 20)
            {
                _curQuality = Math.Max(20, _curQuality - 10);
                _curFps = Math.Max(10, _curFps - 10);
                SendOptions();
            }
            else if (_lastPing < 70)
            {
                if (_curQuality < _trackQuality.Value) { _curQuality = Math.Min(_trackQuality.Value, _curQuality + 5); SendOptions(); }
                if (_curFps < (int)_numFps.Value) { _curFps = Math.Min((int)_numFps.Value, _curFps + 5); SendOptions(); }
            }
        }
    }

    private void ToggleFullscreen()
    {
        if (FormBorderStyle == FormBorderStyle.None)
        {
            FormBorderStyle = _savedStyle;
            WindowState = _savedState;
            _screen.IsFullscreen = false;
        }
        else
        {
            _savedStyle = FormBorderStyle;
            _savedState = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _screen.IsFullscreen = true;
            _screen.Focus();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        Application.RemoveMessageFilter(this);
        _pingTimer?.Stop(); _statsTimer?.Stop();
        try { _pullStream?.Dispose(); } catch { }
        try { _h264?.Dispose(); } catch { }
        foreach (var f in new Form[] { _shellForm, _procForm, _filesForm, _infoForm })
        { if (f != null && !f.IsDisposed) f.Close(); }

        if (_conn is { Borrowed: true })
        {
            // канал принадлежит uplink-серверу — он живёт дальше для следующих сессий
            if (_recvHandler != null) _conn.Received -= _recvHandler;
            if (_closedHandler != null) _conn.Closed -= _closedHandler;
            try { _conn.Send(new MSetOptions { Fps = 2, Quality = 20 }); } catch { }
            _mainForm?.SessionClosed(_machine);
        }
        else
        {
            _conn?.Dispose();
        }
    }
}
