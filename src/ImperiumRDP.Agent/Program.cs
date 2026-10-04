using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ImperiumRDP.Agent;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_HIDE = 0;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cfg = AgentConfig.Load();

        // --- аргументы командной строки ---
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--port" when i + 1 < args.Length:
                    cfg.Port = int.Parse(args[++i]);
                    cfg.Save();
                    break;
                case "--password" when i + 1 < args.Length:
                    cfg.Password = args[++i];
                    cfg.Save();
                    Console.WriteLine("Пароль сохранён в agent.json");
                    return 0;
                case "--fps" when i + 1 < args.Length:
                    cfg.Fps = Math.Clamp(int.Parse(args[++i]), 1, 60);
                    cfg.Save();
                    break;
                case "--quality" when i + 1 < args.Length:
                    cfg.Quality = Math.Clamp(int.Parse(args[++i]), 10, 95);
                    cfg.Save();
                    break;
                case "--install": return InstallTask();
                case "--uninstall": return UninstallTask();
                case "--hidden":
                    var hwnd = GetConsoleWindow();
                    if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_HIDE);
                    break;
                case "--uplink" when i + 1 < args.Length:
                    cfg.Uplink = args[++i].Trim();
                    cfg.Save();
                    Log.Write($"Uplink из командной строки: {cfg.Uplink}");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(cfg.Password))
        {
            cfg.Password = System.Security.Cryptography.RandomNumberGenerator.GetString(
                "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789", 12);
            cfg.Save();
            Console.WriteLine("====================================================");
            Console.WriteLine($"  Сгенерирован новый пароль: {cfg.Password}");
            Console.WriteLine("  Запишите его — он нужен в панели администратора.");
            Console.WriteLine("  (хранится в agent.json, смените: ImperiumRDP.Agent --password НОВЫЙ)");
            Console.WriteLine("====================================================");
        }

        Console.WriteLine($"ImperiumRDP Agent v1.0  |  {Environment.MachineName}");
        Console.WriteLine($"Порт: {cfg.Port}  FPS: {cfg.Fps}  Качество JPEG: {cfg.Quality}");
        Console.WriteLine("Ctrl+C — выход\n");

        var server = new AgentServer(cfg);
        server.Start();

        var exit = new ManualResetEvent(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };
        exit.WaitOne();
        return 0;
    }

    private static int InstallTask()
    {
        if (!IsAdmin())
        {
            Console.WriteLine("Запустите консоль от имени администратора для установки автозапуска.");
            return 1;
        }
        string exe = Environment.ProcessPath;
        string args = $"schtasks /Create /F /TN \"ImperiumRDP Agent\" /TR \"\\\"{exe}\\\" --hidden\" /SC ONLOGON /RL HIGHEST";
        if (RunCmd(args, out string output))
        {
            Console.WriteLine("Автозапуск установлен (задание планировщика \"ImperiumRDP Agent\", вход любого пользователя).");
            Console.WriteLine(output);
            return 0;
        }
        Console.WriteLine("Ошибка: " + output);
        return 1;
    }

    private static int UninstallTask()
    {
        RunCmd("schtasks /Delete /F /TN \"ImperiumRDP Agent\"", out string output);
        Console.WriteLine("Автозапуск удалён. " + output);
        return 0;
    }

    private static bool RunCmd(string args, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        output = (p.StandardOutput.ReadToEnd() + " " + p.StandardError.ReadToEnd()).Trim();
        p.WaitForExit(15000);
        return p.ExitCode == 0;
    }

    private static bool IsAdmin()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
