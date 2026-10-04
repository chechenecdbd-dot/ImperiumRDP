using System.ServiceProcess;

namespace ImperiumRDP.Service;

internal static class Program
{
    static void Main(string[] args)
    {
        if (args.Contains("--console", StringComparer.OrdinalIgnoreCase))
        {
            // отладочный режим: watcher в консоли
            var watcher = new AgentWatcher();
            watcher.Start();
            Console.WriteLine("ImperiumRDP Service (console). Enter — выход.");
            Console.ReadLine();
            watcher.Stop();
            return;
        }
        ServiceBase.Run(new RdpWatcherService());
    }
}

/// <summary>Обёртка над AgentWatcher для SCM.</summary>
public sealed class RdpWatcherService : ServiceBase
{
    private readonly AgentWatcher _watcher = new();

    public RdpWatcherService()
    {
        ServiceName = "ImperiumRDP Agent Watcher";
        CanStop = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _watcher.Start();
        base.OnStart(args);
    }

    protected override void OnStop()
    {
        _watcher.Stop();
        base.OnStop();
    }
}
