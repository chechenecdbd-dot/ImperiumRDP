using System.Diagnostics;
using System.Drawing;
using ImperiumRDP.Agent;
using ImperiumRDP.Agent.Capture;

namespace ImperiumRDP.GUI;

/// <summary>
/// Хаб: одна программа — две роли.
/// «Панель» — окно администратора (скан подсети, подключение к клубным ПК).
/// «Агент» — принимает подключения администратора на этом ПК (запуск/остановка, автозапуск).
/// </summary>
public sealed class HubForm : Form
{
    private TextBox _port, _password, _uplink;
    private Button _btnStart, _btnStop, _btnInstall, _btnUninstall, _btnEye;
    private ListBox _log;
    private Label _status, _adminAddr;
    private AgentServer _server;

    public HubForm()
    {
        Text = "ImperiumRDP";
        Font = new Font("Segoe UI", 9.5F);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        ClientSize = new Size(680, 640);

        int y = 14;
        Controls.Add(new Label
        {
            Text = "ImperiumRDP - удалённый доступ",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, y),
        });
        y += 40;

        // --- блок «Панель» ---
        var grpPanel = new GroupBox
        {
            Text = "Админка",
            Size = new Size(648, 88),
            Location = new Point(16, y),
        };
        var btnPanel = new Button
        {
            Text = "Открыть панель админа",
            Size = new Size(280, 36),
            Location = new Point(16, 30),
        };
        btnPanel.Click += (_, _) => OpenPanel();
        grpPanel.Controls.Add(btnPanel);
        _adminAddr = new Label
        {
            Text = "Адрес для клубных ПК: определяется...",
            Location = new Point(312, 26),
            AutoSize = true,
            ForeColor = Color.DimGray,
        };
        grpPanel.Controls.Add(_adminAddr);
        grpPanel.Controls.Add(new Label
        {
            Text = "Экран до 60 FPS, управление, файлы, CMD.\r\nАгенты клуба подключаются сами (uplink).",
            Location = new Point(312, 46),
            AutoSize = true,
            ForeColor = Color.DimGray,
        });
        Controls.Add(grpPanel);
        y += 100;

        // --- блок «Агент» ---
        var grpAgent = new GroupBox
        {
            Text = "Этот ПК — клубная машина (агент)",
            Size = new Size(648, 302),
            Location = new Point(16, y),
        };

        // строка 1: порт и пароль
        grpAgent.Controls.Add(new Label { Text = "Порт:", Location = new Point(16, 34), AutoSize = true });
        _port = new TextBox { Location = new Point(70, 30), Width = 70, Text = "48499" };
        grpAgent.Controls.Add(_port);
        grpAgent.Controls.Add(new Label { Text = "Пароль:", Location = new Point(164, 34), AutoSize = true });
        _password = new TextBox { Location = new Point(232, 30), Width = 219, UseSystemPasswordChar = true };
        grpAgent.Controls.Add(_password);
        _btnEye = new Button
        {
            Text = "👁",
            Size = new Size(30, 26),
            Location = new Point(453, 29),
            Font = new Font("Segoe UI Emoji", 9F),
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(0),
        };
        _btnEye.Click += (_, _) =>
        {
            _password.UseSystemPasswordChar = !_password.UseSystemPasswordChar;
            _btnEye.BackColor = _password.UseSystemPasswordChar ? SystemColors.Control : Color.FromArgb(210, 230, 255);
        };
        grpAgent.Controls.Add(_btnEye);

        // строка 2: адрес панели (uplink)
        grpAgent.Controls.Add(new Label { Text = "Адрес панели:", Location = new Point(16, 74), AutoSize = true });
        _uplink = new TextBox { Location = new Point(126, 70), Width = 300, PlaceholderText = "домен или IP:порт домашнего ПК" };
        grpAgent.Controls.Add(_uplink);

        // строка 3: подсказка
        grpAgent.Controls.Add(new Label
        {
            Text = "Агенты клуба сами подключатся к панели по этому адресу —\r\nбез проброса портов на клубном роутере.",
            Location = new Point(18, 100),
            AutoSize = true,
            ForeColor = Color.DimGray,
        });

        // строка 4: запуск/останов
        _btnStart = new Button { Text = "▶  Запустить агент", Size = new Size(160, 34), Location = new Point(16, 146) };
        _btnStart.Click += (_, _) => StartAgent();
        _btnStop = new Button { Text = "■  Остановить", Size = new Size(130, 34), Location = new Point(182, 146), Enabled = false };
        _btnStop.Click += (_, _) => StopAgent();

        // строка 5: автозапуск
        _btnInstall = new Button { Text = "Автозапуск: установить", Size = new Size(190, 34), Location = new Point(16, 188) };
        _btnInstall.Click += (_, _) => RunSchTask("--install");
        _btnUninstall = new Button { Text = "Автозапуск: удалить", Size = new Size(190, 34), Location = new Point(212, 188) };
        _btnUninstall.Click += (_, _) => RunSchTask("--uninstall");

        // строка 6: подпись
        grpAgent.Controls.Add(new Label
        {
            Text = "Автозапуск ставит задание планировщика: агент стартует скрыто при входе пользователя.\r\n" +
                   "Нужен старт без входа? Ставьте MSI с ролью «Игровой ПК клуба» — тогда ставится служба.",
            Location = new Point(16, 234),
            AutoSize = true,
            ForeColor = Color.DimGray,
        });
        grpAgent.Controls.AddRange(new Control[] { _btnStart, _btnStop, _btnInstall, _btnUninstall });
        Controls.Add(grpAgent);
        y += 314;

        // --- статус ---
        _status = new Label
        {
            Text = "Агент не запущен.",
            Location = new Point(16, y),
            Size = new Size(648, 22),
            ForeColor = Color.Firebrick,
        };
        Controls.Add(_status);
        y += 28;

        // --- лог ---
        Controls.Add(new Label { Text = "Журнал:", Location = new Point(16, y), AutoSize = true });
        y += 22;
        _log = new ListBox
        {
            Location = new Point(16, y),
            Size = new Size(648, ClientSize.Height - y - 16),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font("Consolas", 8.5F),
        };
        Controls.Add(_log);

        Log.OnWrite += OnAgentLog;
        FormClosing += (_, _) => Log.OnWrite -= OnAgentLog;

        // предзаполнение из agent.json, если есть
        var cfg = AgentConfig.Load();
        _port.Text = cfg.Port.ToString();
        _password.Text = cfg.Password;
        _uplink.Text = cfg.Uplink;
        _ = ResolvePublicAddress();
    }

    /// <summary>Показывает админу его публичный адрес для поля «Адрес панели» на клубных ПК.</summary>
    private async Task ResolvePublicAddress()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            string ip = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            _adminAddr.Text = $"Адрес для клубных ПК: {ip}:{_settingsUplinkPort}";
            _adminAddr.ForeColor = Color.Green;
        }
        catch
        {
            _adminAddr.Text = "Адрес для клубных ПК: не удалось определить (нет интернета?)";
        }
    }

    private const int _settingsUplinkPort = 48500;

    private void OnAgentLog(string line)
    {
        try
        {
            BeginInvoke(() =>
            {
                _log.Items.Add(line);
                if (_log.Items.Count > 500) _log.Items.RemoveAt(0);
                _log.TopIndex = _log.Items.Count - 1;
            });
        }
        catch { }
    }

    private void StartAgent()
    {
        if (_server != null) return;
        try
        {
            var cfg = new AgentConfig
            {
                Port = int.Parse(_port.Text.Trim()),
                Password = _password.Text.Trim(),
                Uplink = _uplink.Text.Trim(),
            };
            if (cfg.Password.Length == 0)
            {
                MessageBox.Show(this, "Задайте пароль агента.", "ImperiumRDP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            cfg.Save();
            TryEnsureFirewallRule(cfg.Port);
            _server = new AgentServer(cfg);
            _server.Start();
            _status.Text = $"Агент запущен: порт {cfg.Port}. Подключайтесь из панели по IP этого ПК.";
            _status.ForeColor = Color.Green;
            _btnStart.Enabled = false;
            _btnStop.Enabled = true;
            _port.Enabled = _password.Enabled = false;
        }
        catch (Exception ex)
        {
            _status.Text = "Не удалось запустить: " + ex.Message;
            _status.ForeColor = Color.Firebrick;
            _server = null;
        }
    }

    private void StopAgent()
    {
        if (_server == null) return;
        try { _server.Stop(); } catch { }
        _server = null;
        _status.Text = "Агент не запущен.";
        _status.ForeColor = Color.Firebrick;
        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
        _port.Enabled = _password.Enabled = true;
    }

    /// <summary>Попытка открыть порт в брандмауэре (тихо; без прав администратора просто не сработает).</summary>
    private void TryEnsureFirewallRule(int port)
    {
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"ImperiumRDP Agent\"");
            RunNetsh($"advfirewall firewall add rule name=\"ImperiumRDP Agent\" dir=in action=allow protocol=TCP localport={port}");
            Log.Write("Брандмауэр: правило для порта " + port + " применено");
        }
        catch { /* нет прав — пользователь настроит вручную */ }
    }

    private static void RunNetsh(string args)
    {
        var psi = new ProcessStartInfo("netsh.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        p.WaitForExit(10000);
    }

    private void RunSchTask(string arg)
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName;
            // автозапуск ставим на сам агент (консольный, --hidden)
            string agentExe = Path.Combine(Path.GetDirectoryName(exe) ?? ".", "ImperiumRDP.Agent.exe");
            if (!File.Exists(agentExe)) agentExe = exe; // fallback
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                Arguments = arg == "--install"
                    ? $"/Create /F /TN \"ImperiumRDP Agent\" /TR \"\\\"{agentExe}\\\" --hidden\" /SC ONLOGON /RL HIGHEST"
                    : "/Delete /F /TN \"ImperiumRDP Agent\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var p = Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            Log.Write($"schtasks {arg}: {(p.ExitCode == 0 ? "успешно" : outp.Trim())}");
            MessageBox.Show(this,
                p.ExitCode == 0
                    ? (arg == "--install"
                        ? $"Автозапуск установлен.\nЗапускается: {agentExe}"
                        : "Автозапуск удалён.")
                    : "Ошибка:\n" + outp,
                "Автозапуск", MessageBoxButtons.OK,
                p.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Автозапуск", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenPanel()
    {
        var panel = new ImperiumRDP.Viewer.UI.MainForm();
        panel.Show(this);
        WindowState = FormWindowState.Minimized;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_server != null)
        {
            var r = MessageBox.Show(this, "Агент запущен. Остановить и выйти?", "ImperiumRDP",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.No) { e.Cancel = true; return; }
            StopAgent();
        }
    }
}
