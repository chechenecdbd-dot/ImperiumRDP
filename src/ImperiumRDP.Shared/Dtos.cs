namespace ImperiumRDP.Shared;

public sealed class Machine
{
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public string HostName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Os { get; set; } = "";
    public string Mac { get; set; } = "";
    public bool Uplink { get; set; }        // машина подключается сама (через NAT)
    public DateTime LastSeen { get; set; } = DateTime.MinValue;
    public override string ToString() => $"{HostName} ({Ip})";
}

public sealed class SysInfoDto
{
    public string HostName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Os { get; set; } = "";
    public string Cpu { get; set; } = "";
    public long TotalRamMb { get; set; }
    public long FreeRamMb { get; set; }
    public long UptimeMin { get; set; }
    public List<string> Ips { get; set; } = new();
    public List<DriveDto> Drives { get; set; } = new();
    public string AgentVersion { get; set; } = "";
    public string Screen { get; set; } = "";
    public string ServerTime { get; set; } = "";
}

public sealed class DriveDto
{
    public string Name { get; set; } = "";
    public double TotalGb { get; set; }
    public double FreeGb { get; set; }
}

public sealed class ProcInfoDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public double MemMb { get; set; }
    public string Title { get; set; } = "";
}
