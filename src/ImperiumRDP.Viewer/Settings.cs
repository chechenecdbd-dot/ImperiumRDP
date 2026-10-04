using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImperiumRDP.Shared;

namespace ImperiumRDP.Viewer;

public sealed class ViewerSettings
{
    public string Password { get; set; } = "";        // только в памяти; в файл не пишется
    public string PasswordDpapi { get; set; } = "";   // DPAPI (CurrentUser) — в файле
    public int Port { get; set; } = 48499;
    public int UplinkPort { get; set; } = 48500;   // порт приёма uplink-агентов
    public int Quality { get; set; } = 66;
    public int Fps { get; set; } = 60;
    public bool AutoScan { get; set; } = true;
    public string LastSubnet { get; set; } = "";
    public List<Machine> Machines { get; set; } = new();

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImperiumRDP");
    private static string Path_ => System.IO.Path.Combine(Dir, "viewer.json");

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static ViewerSettings Load()
    {
        ViewerSettings cfg = new();
        try
        {
            if (File.Exists(Path_))
                cfg = JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(Path_), JsonOpts) ?? new ViewerSettings();
        }
        catch { }
        cfg.Password = Unprotect(cfg.PasswordDpapi);
        cfg.PasswordDpapi = "";
        return cfg;
    }

    public void Save()
    {
        try
        {
            PasswordDpapi = string.IsNullOrEmpty(Password) ? "" : Protect(Password);
            var plain = Password;
            Password = ""; // не пишем открытым текстом
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonOpts) { WriteIndented = true }));
            Password = plain;
            PasswordDpapi = "";
        }
        catch { }
    }

    private static string Protect(string s)
    {
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(bytes);
        }
        catch { return s; }
    }

    private static string Unprotect(string s)
    {
        if (string.IsNullOrEmpty(s) || !s.StartsWith("dpapi:", StringComparison.Ordinal)) return s ?? "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(s["dpapi:".Length..]), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }
}
