using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using ImperiumRDP.Shared;
using ImperiumRDP.Shared.Protocol;

namespace ImperiumRDP.Viewer.UI;

public sealed class ShellForm : Form
{
    private readonly RichTextBox _out = new();
    private readonly TextBox _in = new();
    private readonly Action<string> _sendLine;
    private readonly Action _closed;

    public ShellForm(Action<string> sendLine, Action closed)
    {
        _sendLine = sendLine;
        _closed = closed;
        Text = "Удалённая командная строка (cmd)";
        Font = new Font("Consolas", 9.5F);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(820, 500);
        MinimumSize = new Size(420, 240);

        _out.Dock = DockStyle.Fill;
        _out.ReadOnly = true;
        _out.BackColor = Color.FromArgb(12, 12, 12);
        _out.ForeColor = Color.Gainsboro;
        _out.DetectUrls = false;
        _out.WordWrap = false;

        var panel = new Panel { Dock = DockStyle.Bottom, Height = 32 };
        _in.Dock = DockStyle.Fill;
        _in.BackColor = Color.FromArgb(24, 24, 28);
        _in.ForeColor = Color.Gainsboro;
        _in.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !string.IsNullOrEmpty(_in.Text))
            {
                Append(_in.Text + "\n", false);
                _sendLine(_in.Text);
                _in.Clear();
                e.Handled = true; e.SuppressKeyPress = true;
            }
        };
        var hint = new Label { Text = " >  ", Dock = DockStyle.Left, Width = 30, ForeColor = Color.LimeGreen, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.FromArgb(24, 24, 28) };
        panel.Controls.Add(_in);
        panel.Controls.Add(hint);
        hint.BringToFront();

        Controls.Add(_out);
        Controls.Add(panel);
    }

    public void Append(string text, bool isErr)
    {
        if (IsDisposed) return;
        _out.SelectionStart = _out.TextLength;
        _out.SelectionColor = isErr ? Color.OrangeRed : Color.Gainsboro;
        _out.AppendText(text);
        _out.SelectionColor = _out.ForeColor;
        _out.SelectionStart = _out.TextLength;
        _out.ScrollToCaret();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _closed();
    }
}

public sealed class ProcessesForm : Form
{
    private readonly ListView _list = new();
    private readonly Action _refresh;
    private readonly Action<int> _kill;
    private readonly Action<string> _run;
    private readonly TextBox _runBox = new();

    public ProcessesForm(Action refresh, Action<int> kill, Action<string> run)
    {
        _refresh = refresh; _kill = kill; _run = run;
        Text = "Процессы удалённого ПК";
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 520);
        MinimumSize = new Size(420, 260);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.Columns.Add("Процесс", 220);
        _list.Columns.Add("PID", 70);
        _list.Columns.Add("Память, МБ", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Окно", 300);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        var btnRefresh = new Button { Text = "Обновить", AutoSize = true };
        btnRefresh.Click += (_, _) => _refresh();
        var btnKill = new Button { Text = "Снять процесс", AutoSize = true };
        btnKill.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count == 0) return;
            var item = _list.SelectedItems[0];
            if (int.TryParse(item.SubItems[1].Text, out int pid) &&
                MessageBox.Show(this, $"Завершить «{item.Text}» (PID {pid})?", "Подтверждение",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                _kill(pid);
        };
        _runBox.Width = 320;
        _runBox.PlaceholderText = "Запустить: напр. notepad, https://yandex.ru, D:\\soft\\setup.exe";
        var btnRun = new Button { Text = "Запустить", AutoSize = true };
        btnRun.Click += (_, _) => { if (_runBox.Text.Trim().Length > 0) _run(_runBox.Text.Trim()); };
        _runBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { btnRun.PerformClick(); e.SuppressKeyPress = true; } };

        bar.Controls.Add(btnRefresh);
        bar.Controls.Add(btnKill);
        bar.Controls.Add(new Label { Text = "   ", AutoSize = true });
        bar.Controls.Add(_runBox);
        bar.Controls.Add(btnRun);

        Controls.Add(_list);
        Controls.Add(bar);
        _list.BringToFront();
    }

    public void UpdateList(string json)
    {
        if (IsDisposed) return;
        try
        {
            var procs = JsonSerializer.Deserialize<List<ProcInfoDto>>(json) ?? new();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var p in procs)
            {
                var item = new ListViewItem(string.IsNullOrEmpty(p.Name) ? "?" : p.Name);
                item.SubItems.Add(p.Id.ToString());
                item.SubItems.Add(p.MemMb.ToString("F0"));
                item.SubItems.Add(p.Title ?? "");
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            Text = $"Процессы удалённого ПК — {procs.Count}";
        }
        catch { }
    }
}

public sealed class FilesForm : Form
{
    private readonly SessionForm _session;
    private readonly TextBox _remotePath = new();
    private readonly TextBox _upLocal = new();
    private readonly TextBox _downRemote = new();
    private readonly Label _progress = new();
    private long _downloadTotal, _downloadGot;

    public FilesForm(SessionForm session)
    {
        _session = session;
        Text = "Передача файлов";
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        Size = new Size(640, 300);
        MaximizeBox = false;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        int row = 0;

        grid.Controls.Add(new Label { Text = "Отправить файл на ПК:", Dock = DockStyle.Fill, AutoSize = true }, 0, row);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, row), 3);
        row++;

        _upLocal.Dock = DockStyle.Fill;
        _upLocal.PlaceholderText = "Локальный файл";
        grid.Controls.Add(_upLocal, 0, row);
        var btnPick = new Button { Text = "Выбрать...", AutoSize = true };
        btnPick.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Title = "Файл для отправки" };
            if (d.ShowDialog(this) == DialogResult.OK)
            {
                _upLocal.Text = d.FileName;
                _remotePath.Text = $"%TEMP%\\{Path.GetFileName(d.FileName)}";
            }
        };
        grid.Controls.Add(btnPick, 1, row);
        var btnSend = new Button { Text = "Отправить", AutoSize = true };
        btnSend.Click += (_, _) => Upload();
        grid.Controls.Add(btnSend, 2, row);
        row++;

        _remotePath.Dock = DockStyle.Fill;
        _remotePath.PlaceholderText = "Путь на удалённом ПК (можно %TEMP%\\file.zip)";
        grid.Controls.Add(_remotePath, 0, row);
        row++;

        grid.Controls.Add(new Label { Text = "Скачать файл с ПК:", Dock = DockStyle.Fill, AutoSize = true }, 0, row);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, row), 3);
        row++;

        _downRemote.Dock = DockStyle.Fill;
        _downRemote.PlaceholderText = @"Например: C:\ImperiumRDP\agent.log";
        grid.Controls.Add(_downRemote, 0, row);
        var btnSave = new Button { Text = "Скачать как...", AutoSize = true };
        btnSave.Click += (_, _) => Download();
        grid.Controls.Add(btnSave, 1, row);
        row++;

        _progress.Dock = DockStyle.Fill;
        _progress.AutoSize = true;
        _progress.ForeColor = Color.DimGray;
        grid.Controls.Add(_progress, 0, row);
        grid.SetColumnSpan(_progress, 3);

        Controls.Add(grid);
    }

    private async void Upload()
    {
        string local = _upLocal.Text.Trim();
        string remote = _remotePath.Text.Trim();
        if (!File.Exists(local)) { MessageBox.Show(this, "Локальный файл не найден", "Файлы"); return; }
        if (remote.Length == 0) remote = $"%TEMP%\\{Path.GetFileName(local)}";

        var conn = _session.Connection;
        if (conn == null) return;

        _progress.Text = $"Отправка {local}...";
        try
        {
            long size = new FileInfo(local).Length;
            await conn.SendAsync(new MFilePushStart { RemotePath = remote, Size = size });
            using var fs = File.OpenRead(local);
            byte[] buf = new byte[256 * 1024];
            int n;
            long sent = 0;
            while ((n = await fs.ReadAsync(buf)) > 0)
            {
                var chunk = new byte[n];
                Buffer.BlockCopy(buf, 0, chunk, 0, n);
                sent += n;
                await conn.SendAsync(new MFileData { Chunk = chunk, Last = sent >= size });
                _progress.Text = $"Отправлено {sent / 1024} из {size / 1024} КБ";
            }
            if (size == 0) conn.Send(new MFileData { Chunk = Array.Empty<byte>(), Last = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Отправка файла");
        }
    }

    private void Download()
    {
        string remote = _downRemote.Text.Trim();
        if (remote.Length == 0) { MessageBox.Show(this, "Укажите путь к файлу на удалённом ПК", "Файлы"); return; }
        using var d = new SaveFileDialog { Title = "Куда сохранить", FileName = Path.GetFileName(remote) };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _downloadTotal = _downloadGot = 0;
        _progress.Text = "Запрос файла...";
        _session.BeginPull(remote, d.FileName);
    }

    public void DownloadStarted(string remote, long size)
    {
        if (IsDisposed) return;
        _downloadTotal = size; _downloadGot = 0;
        _progress.Text = $"Скачивание {remote} ({size / 1024} КБ)...";
    }

    public void DownloadProgress(int bytes)
    {
        if (IsDisposed) return;
        _downloadGot += bytes;
        _progress.Text = _downloadTotal > 0
            ? $"Скачано {_downloadGot / 1024} из {_downloadTotal / 1024} КБ"
            : $"Скачано {_downloadGot / 1024} КБ";
    }

    public void DownloadFinished(bool ok, string error)
    {
        if (IsDisposed) return;
        _progress.Text = ok ? "Файл скачан." : "Ошибка: " + error;
        _progress.ForeColor = ok ? Color.Green : Color.Firebrick;
    }

    public void UploadFinished(bool ok, string message)
    {
        if (IsDisposed) return;
        _progress.ForeColor = ok ? Color.Green : Color.Firebrick;
        _progress.Text = message;
    }
}

public sealed class InfoForm : Form
{
    private readonly TextBox _box = new();

    public InfoForm()
    {
        Text = "Информация о ПК";
        Font = new Font("Segoe UI", 9.5F);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(560, 560);
        MinimumSize = new Size(380, 260);

        _box.Dock = DockStyle.Fill;
        _box.Multiline = true;
        _box.ReadOnly = true;
        _box.ScrollBars = ScrollBars.Vertical;
        _box.Font = new Font("Consolas", 9.5F);
        Controls.Add(_box);
    }

    public void Show(SysInfoDto d)
    {
        if (IsDisposed) return;
        var sb = new StringBuilder();
        sb.AppendLine($"Имя:          {d.HostName}");
        sb.AppendLine($"Пользователь: {d.UserName}");
        sb.AppendLine($"ОС:           {d.Os}");
        sb.AppendLine($"Процессор:    {d.Cpu}");
        sb.AppendLine($"Экран:        {d.Screen}");
        sb.AppendLine($"Память:       свободно {d.FreeRamMb:F0} из {d.TotalRamMb:F0} МБ");
        sb.AppendLine($"Аптайм:       {d.UptimeMin} мин");
        sb.AppendLine($"Агент:        v{d.AgentVersion}");
        sb.AppendLine($"Время на ПК:  {d.ServerTime}");
        sb.AppendLine();
        sb.AppendLine("Сеть:");
        foreach (var ip in d.Ips) sb.AppendLine("  " + ip);
        sb.AppendLine();
        sb.AppendLine("Диски:");
        foreach (var dr in d.Drives)
            sb.AppendLine($"  {dr.Name}  свободно {dr.FreeGb} из {dr.TotalGb} ГБ");
        _box.Text = sb.ToString();
    }
}

public sealed class PasswordForm : Form
{
    public string Password => _box.Text;
    public bool Remember => _chkRemember.Checked;

    private readonly TextBox _box = new();
    private readonly CheckBox _chkRemember = new();

    public PasswordForm(string title)
    {
        Text = title;
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(380, 150);

        var lbl = new Label { Text = "Пароль агента:", Location = new Point(12, 14), AutoSize = true };
        _box.SetBounds(12, 38, 350, 24);
        _box.UseSystemPasswordChar = true;
        _chkRemember.Text = "Запомнить пароль";
        _chkRemember.SetBounds(12, 70, 350, 22);
        var ok = new Button { Text = "ОК", DialogResult = DialogResult.OK };
        ok.SetBounds(200, 104, 80, 28);
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(286, 104, 80, 28);

        Controls.AddRange(new Control[] { lbl, _box, _chkRemember, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
