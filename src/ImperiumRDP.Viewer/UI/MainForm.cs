using System.Drawing;
using System.Windows.Forms;
using ImperiumRDP.Shared;
using ImperiumRDP.Viewer.Net;

namespace ImperiumRDP.Viewer.UI;

public sealed class MainForm : Form
{
    private readonly ViewerSettings _settings = ViewerSettings.Load();
    private readonly ComboBox _subnet = new();
    private readonly NumericUpDown _port = new();
    private readonly ListView _list = new();
    private readonly Button _btnScan = new() { Text = "Сканировать" };
    private readonly Button _btnStop = new() { Text = "Стоп", Enabled = false };
    private readonly Button _btnConnect = new() { Text = "Подключиться" };
    private readonly Button _btnWol = new() { Text = "Включить (WoL)", Enabled = false };
    private readonly ToolStripStatusLabel _st = new("Готово");
    private CancellationTokenSource _scanCts;
    private UplinkServer? _uplink;
    private readonly Dictionary<string, ClientConnection> _sessionHeld = new(StringComparer.OrdinalIgnoreCase);

    public MainForm()
    {
        Text = "ImperiumRDP — панель администратора";
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 480);
        Size = new Size(940, 600);

        // --- верхняя панель ---
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };

        bar.Controls.Add(new Label { Text = "Подсеть / IP:", AutoSize = true, Margin = new Padding(3, 9, 3, 0) });
        _subnet.DropDownStyle = ComboBoxStyle.DropDown;
        _subnet.Width = 210;
        _subnet.Items.AddRange(Scanner.DetectSubnets().ToArray());
        _subnet.Text = _settings.LastSubnet;
        if (_subnet.Text.Length == 0 && _subnet.Items.Count > 0) _subnet.SelectedIndex = 0;
        bar.Controls.Add(_subnet);

        bar.Controls.Add(new Label { Text = "Порт:", AutoSize = true, Margin = new Padding(10, 9, 3, 0) });
        _port.Minimum = 1; _port.Maximum = 65535;
        _port.Value = _settings.Port;
        _port.Width = 70;
        bar.Controls.Add(_port);

        _btnScan.Click += (_, _) => _ = ScanAsync();
        _btnStop.Click += (_, _) => _scanCts?.Cancel();
        _btnConnect.Click += (_, _) => ConnectSelected();
        _btnWol.Click += (_, _) => WakeSelected();
        foreach (var b in new[] { _btnScan, _btnStop, _btnConnect, _btnWol }) { b.AutoSize = true; b.Padding = new Padding(6, 2, 6, 2); bar.Controls.Add(b); }

        var btnProbe = new Button { Text = "Проверить список", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        btnProbe.Click += (_, _) => _ = ProbeSavedAsync();
        bar.Controls.Add(btnProbe);

        var menuSettings = new Button { Text = "Настройки", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        menuSettings.Click += (_, _) => ShowSettings();
        bar.Controls.Add(menuSettings);

        // --- список машин ---
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.DoubleClick += (_, _) => ConnectSelected();
        _list.Columns.Add("Состояние", 110);
        _list.Columns.Add("Имя ПК", 160);
        _list.Columns.Add("IP", 130);
        _list.Columns.Add("Порт", 60);
        _list.Columns.Add("Пользователь", 150);
        _list.Columns.Add("ОС", 240);

        var strip = new StatusStrip();
        strip.Items.Add(_st);

        Controls.Add(_list);
        Controls.Add(bar);
        Controls.Add(strip);
        _list.BringToFront();

        Load += (_, _) =>
        {
            RestoreMachines();
            StartUplink();
            if (_settings.AutoScan) _ = ProbeSavedAsync();
        };
        FormClosing += (_, _) =>
        {
            _scanCts?.Cancel();
            SaveMachines();
            _settings.Save();
            _uplink?.Dispose();
        };
    }

    /// <summary>Запуск приёмника uplink-соединений агентов клуба.</summary>
    private void StartUplink()
    {
        try
        {
            _uplink = new UplinkServer(_settings.Password);
            _uplink.AgentAdded += agent => BeginInvoke(() =>
            {
                var m = new Machine
                {
                    HostName = agent.HostName,
                    Ip = "(uplink)",
                    Port = 0,
                    UserName = agent.Connection.Hello.User,
                    Os = agent.Connection.Hello.Os,
                    Mac = agent.Connection.Hello.Mac,
                    Uplink = true,
                };
                UpsertMachine(m);
                _st.Text = $"Uplink: {agent.HostName} подключился сам.";
            });
            _uplink.AgentRemoved += host => BeginInvoke(() =>
            {
                foreach (ListViewItem item in _list.Items)
                    if (item.Tag is MachineState st && st.Machine.Uplink &&
                        string.Equals(st.Machine.HostName, host, StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateRow(item, st.Machine, online: false);
                        _st.Text = $"Uplink: {host} отключился.";
                    }
            });
            _uplink.Start(_settings.UplinkPort);
            _st.Text = $"Uplink-сервер: принимаю агентов на порт {_settings.UplinkPort}.";
        }
        catch (Exception ex)
        {
            _st.Text = $"Uplink-сервер не запущен: {ex.Message}";
        }
    }

    /// <summary>Ждёт появления свободного uplink-агента с заданным именем (для автопереподключения).</summary>
    internal ClientConnection WaitForUplink(string hostname, TimeSpan timeout)
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline && !IsDisposed)
        {
            var a = _uplink?.Get(hostname);
            if (a != null && !_sessionHeld.ContainsKey(hostname))
                return a.Connection;
            Thread.Sleep(200);
        }
        return null;
    }

    internal void SessionClosed(Machine machine)
    {
        if (!machine.Uplink) return;
        _sessionHeld.Remove(machine.HostName);
        var a = _uplink?.Get(machine.HostName);
        if (a != null) a.InSession = false;
        BeginInvoke(() => _st.Text = $"Сессия с {machine.HostName} закрыта, uplink сохранён.");
    }

    private void RestoreMachines()
    {
        foreach (var m in _settings.Machines)
        {
            var item = MakeItem(m, online: null);
            _list.Items.Add(item);
        }
        _st.Text = _list.Items.Count > 0
            ? $"Известных ПК: {_list.Items.Count}. Нажмите «Проверить список» или «Сканировать»."
            : "Укажите подсеть и нажмите «Сканировать».";
    }

    private static ListViewItem MakeItem(Machine m, bool? online)
    {
        var item = new ListViewItem(online switch
        {
            true => "●  онлайн",
            false => "○  офлайн",
            _ => "?  не проверен",
        });
        item.UseItemStyleForSubItems = false;
        item.SubItems[0].ForeColor = online switch { true => Color.Green, false => Color.Gray, _ => Color.DimGray };
        item.SubItems.Add(m.HostName);
        item.SubItems.Add(m.Ip);
        item.SubItems.Add(m.Port.ToString());
        item.SubItems.Add(m.UserName);
        item.SubItems.Add(m.Os);
        item.Tag = new MachineState { Machine = m, Online = online };
        return item;
    }

    private sealed class MachineState
    {
        public Machine Machine;
        public bool? Online;
    }

    private Machine SelectedMachine()
    {
        if (_list.SelectedItems.Count == 0) return null;
        return ((_list.SelectedItems[0].Tag as MachineState)?.Machine);
    }

    private async Task ScanAsync()
    {
        string spec = _subnet.Text.Trim();
        if (spec.Length == 0) { MessageBox.Show(this, "Укажите подсеть, например 192.168.1.0/24", "Скан"); return; }
        int port = (int)_port.Value;

        _settings.LastSubnet = spec;
        _settings.Port = port;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        var ips = Scanner.Enumerate(spec).ToList();
        var scanned = ips.ToHashSet();
        var seen = new HashSet<string>();

        _btnScan.Enabled = false; _btnStop.Enabled = true;
        _st.Text = $"Сканирование {spec} порт {port}...";

        var found = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            int done = 0;
            using var sem = new SemaphoreSlim(200);
            var tasks = ips.Select(async ip =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var m = await Scanner.ProbeAsync(ip, port, 700, ct);
                    if (m != null)
                    {
                        found++;
                        lock (seen) seen.Add(ip);
                        BeginInvoke(() => UpsertMachine(m));
                    }
                }
                catch { }
                finally
                {
                    sem.Release();
                    var d = Interlocked.Increment(ref done);
                    if (d % 51 == 0)
                        BeginInvoke(() => _st.Text = $"Сканирование {spec}... {d}/{ips.Count}, найдено {found}");
                }
            }).ToArray();
            await Task.WhenAll(tasks);
            _st.Text = $"Готово за {sw.ElapsedMilliseconds / 1000.0:F1} с. Найдено агентов: {found}";
        }
        catch (OperationCanceledException) { _st.Text = "Сканирование остановлено"; }
        finally
        {
            // всё просканированное, но не найденное — офлайн (вне диапазона не трогаем)
            var offline = scanned.Except(seen).ToHashSet();
            BeginInvoke(() =>
            {
                foreach (ListViewItem item in _list.Items)
                    if (item.Tag is MachineState st && offline.Contains(st.Machine.Ip))
                        UpdateRow(item, st.Machine, online: false);
                _btnScan.Enabled = true; _btnStop.Enabled = false;
            });
        }
    }

    /// <summary>Быстрая проверка сохранённых ПК (без полного скана подсети).</summary>
    private async Task ProbeSavedAsync()
    {
        var targets = new List<(ListViewItem item, Machine m)>();
        foreach (ListViewItem item in _list.Items)
            if (item.Tag is MachineState st && !st.Machine.Uplink) targets.Add((item, st.Machine));
        if (targets.Count == 0)
        {
            _st.Text = _uplink != null
                ? $"Uplink-сервер на порту {_settings.UplinkPort}. Агенты клуба подключатся сами."
                : "Список пуст.";
            return;
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        _btnScan.Enabled = false; _btnStop.Enabled = true;
        _st.Text = "Проверка списка...";

        int online = 0, done = 0;
        using var sem = new SemaphoreSlim(64);
        try
        {
            await Task.WhenAll(targets.Select(async t =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var m = await Scanner.ProbeAsync(t.m.Ip, t.m.Port, 1200, ct);
                    if (m != null)
                    {
                        Interlocked.Increment(ref online);
                        BeginInvoke(() => UpdateRow(t.item, m, online: true));
                    }
                    else BeginInvoke(() => UpdateRow(t.item, t.m, online: false));
                }
                catch (OperationCanceledException) { throw; }
                catch { BeginInvoke(() => UpdateRow(t.item, t.m, online: false)); }
                finally
                {
                    sem.Release();
                    var d = Interlocked.Increment(ref done);
                    BeginInvoke(() => _st.Text = $"Проверка... {d}/{targets.Count}, онлайн {online}");
                }
            }));
            _st.Text = $"Онлайн: {online} из {targets.Count}";
        }
        catch (OperationCanceledException) { _st.Text = "Проверка остановлена"; }
        finally { BeginInvoke(() => { _btnScan.Enabled = true; _btnStop.Enabled = false; }); }
    }

    private void UpdateRow(ListViewItem item, Machine m, bool online)
    {
        if (item.ListView == null) return;
        if (item.Tag is MachineState st) { st.Machine = m; st.Online = online; }
        item.Text = online ? "●  онлайн" : "○  офлайн";
        item.SubItems[0].ForeColor = online ? Color.Green : Color.Gray;
        if (!string.IsNullOrEmpty(m.HostName)) item.SubItems[1].Text = m.HostName;
        item.SubItems[2].Text = m.Ip;
        item.SubItems[3].Text = m.Port.ToString();
        if (!string.IsNullOrEmpty(m.UserName)) item.SubItems[4].Text = m.UserName;
        if (!string.IsNullOrEmpty(m.Os)) item.SubItems[5].Text = m.Os;
    }

    private void UpsertMachine(Machine m)
    {
        foreach (ListViewItem item in _list.Items)
        {
            var st = item.Tag as MachineState;
            if (st == null || st.Machine.Ip != m.Ip || st.Machine.Port != m.Port) continue;
            UpdateRow(item, m, online: true);
            return;
        }
        _list.Items.Add(MakeItem(m, true));
    }

    private void SaveMachines()
    {
        var set = new List<Machine>();
        foreach (ListViewItem item in _list.Items)
        {
            var st = item.Tag as MachineState;
            if (st != null && !set.Any(x => x.Ip == st.Machine.Ip && x.Port == st.Machine.Port))
                set.Add(st.Machine);
        }
        _settings.Machines = set;
    }

    private void ConnectSelected()
    {
        var m = SelectedMachine();
        if (m == null) { MessageBox.Show(this, "Выберите ПК в списке", "Подключение"); return; }

        if (string.IsNullOrEmpty(_settings.Password))
        {
            using var pf = new PasswordForm($"Пароль агента для {m.Ip}");
            if (pf.ShowDialog(this) != DialogResult.OK || pf.Password.Length == 0) return;
            _settings.Password = pf.Password;
            if (pf.Remember) _settings.Save();
        }

        SessionForm form;
        if (m.Uplink)
        {
            var agent = _uplink?.Get(m.HostName);
            if (agent == null) { MessageBox.Show(this, "Агент сейчас не на связи", "Подключение"); return; }
            if (_sessionHeld.ContainsKey(m.HostName)) { MessageBox.Show(this, "Сессия с этим ПК уже открыта", "Подключение"); return; }
            _sessionHeld[m.HostName] = agent.Connection;
            agent.InSession = true;
            form = new SessionForm(m, _settings, this, agent.Connection);
        }
        else
        {
            m.Port = (int)_port.Value;
            form = new SessionForm(m, _settings, this);
        }
        form.Show(this);
    }

    /// <summary>Wake-on-LAN: будит выбранный офлайн-ПК по magic packet.</summary>
    private void WakeSelected()
    {
        var m = SelectedMachine();
        if (m == null) { MessageBox.Show(this, "Выберите ПК в списке", "WoL"); return; }
        if (string.IsNullOrEmpty(m.Mac))
        {
            MessageBox.Show(this, "У этого ПК неизвестен MAC-адрес (подключитесь к нему хотя бы раз).",
                "WoL", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var macBytes = m.Mac.Split('-').Select(b => Convert.ToByte(b, 16)).ToArray();
            var packet = new byte[6 + 16 * macBytes.Length];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 0; i < 16; i++) Buffer.BlockCopy(macBytes, 0, packet, 6 + i * macBytes.Length, macBytes.Length);
            using var udp = new System.Net.Sockets.UdpClient();
            for (int i = 0; i < 3; i++)
            {
                udp.Send(packet, packet.Length, "255.255.255.255", 9);
                Thread.Sleep(100);
            }
            _st.Text = $"WoL-пакет отправлен на {m.Mac}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "WoL", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowSettings()
    {
        using var dlg = new SettingsForm(_settings);
        dlg.ShowDialog(this);
        _settings.Save();
    }
}

public sealed class SettingsForm : Form
{
    public SettingsForm(ViewerSettings s)
    {
        Text = "Настройки";
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(420, 210);

        var lblPw = new Label { Text = "Пароль агента (общий для клуба):", Location = new Point(12, 14), AutoSize = true };
        var pw = new TextBox { UseSystemPasswordChar = true, Location = new Point(12, 36), Width = 390 };
        pw.Text = s.Password;

        var chkAuto = new CheckBox { Text = "Автосканирование при запуске", Location = new Point(12, 72), AutoSize = true };
        chkAuto.Checked = s.AutoScan;

        var lblDef = new Label { Text = "Качество/FPS по умолчанию:", Location = new Point(12, 100), AutoSize = true };
        var quality = new NumericUpDown { Minimum = 10, Maximum = 90, Value = Math.Clamp(s.Quality, 10, 90), Location = new Point(12, 122), Width = 70 };
        var fps = new NumericUpDown { Minimum = 2, Maximum = 30, Value = Math.Clamp(s.Fps, 2, 30), Location = new Point(92, 122), Width = 70 };
        var lblQ = new Label { Text = "качество  качество→FPS", Location = new Point(170, 124), AutoSize = true, ForeColor = Color.DimGray };

        var ok = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, Location = new Point(210, 168), Width = 96 };
        ok.Click += (_, _) =>
        {
            s.Password = pw.Text;
            s.AutoScan = chkAuto.Checked;
            s.Quality = (int)quality.Value;
            s.Fps = (int)fps.Value;
            s.Save();
        };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(314, 168), Width = 90 };

        Controls.AddRange(new Control[] { lblPw, pw, chkAuto, lblDef, quality, fps, lblQ, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
