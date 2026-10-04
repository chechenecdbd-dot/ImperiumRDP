using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace ImperiumRDP.Agent.Admin;

public static class SysInfoBuilder
{
    public static Shared.SysInfoDto Build(string agentVersion, string screen)
    {
        var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        NativeMethods.GlobalMemoryStatusEx(ref mem);

        var dto = new Shared.SysInfoDto
        {
            HostName = Environment.MachineName,
            UserName = Environment.UserName,
            Os = Environment.OSVersion.VersionString,
            Cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "",
            TotalRamMb = (long)(mem.ullTotalPhys / (1024.0 * 1024.0)),
            FreeRamMb = (long)(mem.ullAvailPhys / (1024.0 * 1024.0)),
            UptimeMin = (long)(NativeMethods.GetTickCount64() / 60000),
            AgentVersion = agentVersion,
            Screen = screen,
            ServerTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        dto.Ips.Add($"{ni.Name}: {a.Address}");
            }
        }
        catch { }

        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                dto.Drives.Add(new Shared.DriveDto
                {
                    Name = d.Name,
                    TotalGb = Math.Round(d.TotalSize / (1024.0 * 1024 * 1024), 1),
                    FreeGb = Math.Round(d.AvailableFreeSpace / (1024.0 * 1024 * 1024), 1),
                });
            }
        }
        catch { }

        return dto;
    }
}

public static class ProcessAdmin
{
    public static List<Shared.ProcInfoDto> List()
    {
        var result = new List<Shared.ProcInfoDto>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                result.Add(new Shared.ProcInfoDto
                {
                    Id = p.Id,
                    Name = p.ProcessName,
                    MemMb = Math.Round(p.WorkingSet64 / (1024.0 * 1024), 1),
                    Title = p.MainWindowTitle,
                });
            }
            catch { /* процесс завершился */ }
            finally { p.Dispose(); }
        }
        return result.OrderByDescending(x => x.MemMb).ToList();
    }

    public static void Kill(int pid)
    {
        var p = Process.GetProcessById(pid);
        p.Kill(entireProcessTree: true);
    }

    public static void Run(string command)
    {
        // через cmd start — чтобы работали пути, URL и документы
        var psi = new ProcessStartInfo("cmd.exe", "/c start \"\" " + command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi)?.Dispose();
    }
}

public static class PowerAdmin
{
    public static void Reboot(int timeoutSec = 15) =>
        Process.Start("shutdown.exe", $"/r /t {timeoutSec} /c \"ImperiumRDP: перезагрузка администратором\"")?.Dispose();

    public static void Shutdown(int timeoutSec = 15) =>
        Process.Start("shutdown.exe", $"/s /t {timeoutSec} /c \"ImperiumRDP: выключение администратором\"")?.Dispose();

    public static void Logoff() =>
        NativeMethods.ExitWindowsEx(NativeMethods.EWX_LOGOFF, 0);
}

/// <summary>Буфер обмена — только из STA-потока.</summary>
public static class AgentClipboard
{
    private static readonly Thread StaThread;
    private static readonly BlockingCollection<Action> Queue = new();

    static AgentClipboard()
    {
        StaThread = new Thread(() =>
        {
            foreach (var a in Queue.GetConsumingEnumerable()) { try { a(); } catch { } }
        })
        { IsBackground = true, Name = "ClipboardSTA" };
        StaThread.SetApartmentState(ApartmentState.STA);
        StaThread.Start();
    }

    public static Task<string> GetTextAsync()
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            string text = "";
            for (int i = 0; i < 5; i++)
            {
                try { text = System.Windows.Forms.Clipboard.GetText(); break; }
                catch { Thread.Sleep(80); }
            }
            tcs.TrySetResult(text ?? "");
        });
        return tcs.Task;
    }

    public static Task<bool> SetTextAsync(string text)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            bool ok = false;
            for (int i = 0; i < 5; i++)
            {
                try { System.Windows.Forms.Clipboard.SetText(text); ok = true; break; }
                catch { Thread.Sleep(80); }
            }
            tcs.TrySetResult(ok);
        });
        return tcs.Task;
    }
}

/// <summary>Удалённый cmd с потоковым выводом.</summary>
public sealed class ShellServer : IDisposable
{
    private Process _proc;

    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();

    public void Start(Action<string, bool> onOutput)
    {
        if (_proc != null) return;
        var enc = Encoding.GetEncoding((int)GetOEMCP());
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = enc,
            StandardErrorEncoding = enc,
        };
        _proc = new Process { StartInfo = psi };
        _proc.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data + "\n", false); };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data + "\n", true); };
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    public void WriteLine(string line)
    {
        try { _proc?.StandardInput.WriteLine(line); } catch { }
    }

    public void Dispose()
    {
        try { _proc?.Kill(entireProcessTree: true); } catch { }
        _proc?.Dispose();
        _proc = null;
    }
}

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    [DllImport("kernel32.dll")]
    internal static extern ulong GetTickCount64();

    [DllImport("user32.dll")]
    internal static extern bool ExitWindowsEx(uint uFlags, uint dwReason);
    internal const uint EWX_LOGOFF = 0;
}
