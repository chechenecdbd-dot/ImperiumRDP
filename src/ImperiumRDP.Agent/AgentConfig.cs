using System.Text.Json;

namespace ImperiumRDP.Agent;

public sealed class AgentConfig
{
    public int Port { get; set; } = 48499;
    public string Password { get; set; } = "";
    public int Fps { get; set; } = 60;
    public int Quality { get; set; } = 66;
    public int MaxClients { get; set; } = 4;

    /// <summary>Адрес панели «хост:порт» — агент сам держит исходящее uplink-соединение
    /// (работает из-за NAT провайдера, проброс не нужен). Пусто = выключено.</summary>
    public string Uplink { get; set; } = "";

    private static string HomeDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("IMPERIUMRDP_HOME");
            if (!string.IsNullOrEmpty(env)) return env;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ImperiumRDP");
        }
    }

    private static string ConfigPath
    {
        get
        {
            // в ProgramData (для установленной версии); рядом с exe — для portable
            string dataPath = Path.Combine(HomeDir, "agent.json");
            if (File.Exists(dataPath)) return dataPath;
            string local = Path.Combine(AppContext.BaseDirectory, "agent.json");
            if (File.Exists(local)) return local;
            return dataPath;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static AgentConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new AgentConfig();
        }
        catch { }
        var cfg = new AgentConfig();
        cfg.Save();
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(HomeDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
        catch { /* нет прав — настройки не сохранятся, но агент заработает с текущими */ }
    }
}
