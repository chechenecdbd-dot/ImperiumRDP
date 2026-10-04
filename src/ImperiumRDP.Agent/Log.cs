namespace ImperiumRDP.Agent;

/// <summary>Простой логгер: консоль + файл agent.log рядом с exe (с ротацией) + подписчики (GUI).</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static string _path = ResolveLogPath();

    private static string ResolveLogPath()
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("IMPERIUMRDP_HOME");
            string dir = !string.IsNullOrEmpty(env)
                ? env
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ImperiumRDP");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "agent.log");
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "agent.log");
        }
    }

    /// <summary>Подписка на все записи (для GUI). Вызывается из любых потоков.</summary>
    public static event Action<string> OnWrite;

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (Lock)
        {
            try { Console.WriteLine(line); } catch { }
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch { }
        }
        try { OnWrite?.Invoke(line); } catch { }
    }
}
