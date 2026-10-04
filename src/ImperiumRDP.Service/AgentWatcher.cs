using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ImperiumRDP.Service;

/// <summary>
/// Надзиратель: следит за активной консольной сессией и запускает в ней
/// ImperiumRDP.Agent.exe (скрыто) от имени залогиненного пользователя.
/// Захват экрана и ввод работают, потому что агент живёт в интерактивной сессии.
/// Пока пользователь не залогинен — просто ждёт; при выходе агента перезапускает.
/// </summary>
public sealed class AgentWatcher
{
    private Thread _thread;
    private volatile bool _running;
    private Process _agent;
    private int _agentSession = -1;

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "AgentWatcher" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { if (_thread?.Join(3000) == false) _thread.Interrupt(); } catch { }
        KillAgent();
    }

    // TODO: автообновление — панель кладёт новый agent.exe рядом с маркер-файлом версии,
    // watcher замечает, глушит агент-процесс, подменяет exe и запускает заново.

    private void Log(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ImperiumRDP");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "service.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\r\n");
        }
        catch { }
    }

    private void Loop()
    {
        string agentPath = Path.Combine(AppContext.BaseDirectory, "ImperiumRDP.Agent.exe");
        if (!File.Exists(agentPath))
        {
            Log($"агент не найден: {agentPath}");
            return;
        }

        Log($"watcher запущен, агент: {agentPath}");
        EnsureFirewallRule(agentPath);
        while (_running)
        {
            try
            {
                int session = unchecked((int)WTSGetActiveConsoleSessionId());
                if (session == -1 || session == 0)
                {
                    // никто не залогинен (или сеанс 0) — если агент был, он умрёт вместе с сессией
                    CleanupIfDead();
                    Thread.Sleep(3000);
                    continue;
                }

                if (_agent != null && _agentSession == session)
                {
                    _agent.Refresh();
                    if (!_agent.HasExited) { Thread.Sleep(3000); continue; }
                    Log($"агент вышел (код {_agent.ExitCode}), перезапускаю");
                    CleanupIfDead();
                }

                if (!StartAgentInSession(agentPath, session))
                {
                    Thread.Sleep(5000);
                    continue;
                }

                _agentSession = session;
                Log($"агент запущен в сессии {session} (PID {_agent?.Id})");
            }
            catch (Exception ex)
            {
                Log("ошибка: " + ex.Message);
                Thread.Sleep(5000);
            }
            Thread.Sleep(2000);
        }
    }

    private static bool _firewallDone;

    /// <summary>Разрешает входящие подключения для exe агента (от SYSTEM права есть).</summary>
    private void EnsureFirewallRule(string agentPath)
    {
        if (_firewallDone) return;
        _firewallDone = true;
        try
        {
            RunNetsh("advfirewall firewall delete rule name=\"ImperiumRDP Agent\"");
            RunNetsh($"advfirewall firewall add rule name=\"ImperiumRDP Agent\" dir=in action=allow program=\"{agentPath}\" enable=yes");
            Log("брандмауэр: правило для агента применено");
        }
        catch (Exception ex) { Log("брандмауэр: " + ex.Message); }
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

    private void CleanupIfDead()
    {
        try { _agent?.Dispose(); } catch { }
        _agent = null;
        _agentSession = -1;
    }

    private bool StartAgentInSession(string agentPath, int sessionId)
    {
        // уже запущен другим экземпляром? (например, вручную из GUI)
        var existing = Process.GetProcessesByName("ImperiumRDP.Agent")
            .FirstOrDefault(p => { try { return p.SessionId == sessionId; } catch { return false; } });
        if (existing != null)
        {
            _agent = existing;
            return true;
        }

        if (!WTSQueryUserToken((uint)sessionId, out IntPtr userToken))
        {
            Log($"WTSQueryUserToken({sessionId}) не удался: {Marshal.GetLastWin32Error()}");
            return false;
        }

        IntPtr env = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out env, userToken, false))
                Log("CreateEnvironmentBlock не удался — продолжаю без окружения");

            var si = new STARTUPINFOW { cb = (uint)Marshal.SizeOf<STARTUPINFOW>() };
            var creationFlags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;
            if (!CreateProcessAsUser(userToken, agentPath, $"\"{agentPath}\" --hidden",
                    IntPtr.Zero, IntPtr.Zero, false, creationFlags, env,
                    Path.GetDirectoryName(agentPath), ref si, out _))
            {
                Log($"CreateProcessAsUser не удался: {Marshal.GetLastWin32Error()}");
                return false;
            }
            return true;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            CloseHandle(userToken);
        }
    }

    private void KillAgent()
    {
        try { if (_agent is { HasExited: false }) _agent.Kill(entireProcessTree: true); } catch { }
        try { _agent?.Dispose(); } catch { }
        _agent = null;
    }

    // ---- interop ----

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public string lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }
}
