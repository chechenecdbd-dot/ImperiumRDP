using System.Text;

namespace ImperiumRDP.Shared.Protocol;

public enum MsgType : byte
{
    // handshake
    Hello = 1, ClientAuth = 2, ServerAuth = 3, AuthFail = 4,
    // video
    ScreenInfo = 5, TileBatch = 6, RequestKeyframe = 7,
    // misc
    Ping = 8, Pong = 9,
    // input
    MouseMove = 10, MouseButton = 11, MouseWheel = 12, Key = 13, Text = 14,
    // admin
    SysInfoReq = 15, SysInfo = 16,
    ProcListReq = 17, ProcList = 18, KillProc = 19, RunProgram = 20,
    ShellStart = 21, ShellInput = 22, ShellOutput = 23, ShellStop = 24,
    FilePushStart = 25, FileData = 26, FilePull = 27, FilePullOk = 28, FilePullDeny = 29,
    Power = 30,
    ClipboardSet = 32, ClipboardGet = 33, ClipboardData = 34,
    SetOptions = 35,
    Ok = 36, Error = 37,
    H264Data = 38,
    CursorState = 39,
    CursorShape = 40,
}

public abstract class Msg
{
    public abstract MsgType Type { get; }
    public abstract void Write(BinaryWriter w);
    public abstract void Read(BinaryReader r);

    public byte[] Encode()
    {
        using var ms = new MemoryStream(256);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        Write(w);
        w.Flush();
        return ms.ToArray();
    }
}

public sealed class MHello : Msg
{
    public override MsgType Type => MsgType.Hello;
    public string Magic = "IRDP1";
    public int Version = 1;
    public string Host = "";
    public string User = "";
    public string Os = "";
    public string Mac = "";
    public byte[] Salt = new byte[16];
    public byte[] Challenge = new byte[16];

    public override void Write(BinaryWriter w)
    {
        w.Write(Magic); w.Write(Version); w.Write(Host); w.Write(User); w.Write(Os); w.Write(Mac);
        w.Write((byte)Salt.Length); w.Write(Salt);
        w.Write((byte)Challenge.Length); w.Write(Challenge);
    }

    public override void Read(BinaryReader r)
    {
        Magic = r.ReadString(); Version = r.ReadInt32();
        Host = r.ReadString(); User = r.ReadString(); Os = r.ReadString(); Mac = r.ReadString();
        Salt = r.ReadBytes(r.ReadByte());
        Challenge = r.ReadBytes(r.ReadByte());
    }
}

public sealed class MClientAuth : Msg
{
    public override MsgType Type => MsgType.ClientAuth;
    public byte[] ClientChallenge = new byte[16];
    public byte[] Proof = Array.Empty<byte>();

    public override void Write(BinaryWriter w)
    {
        w.Write((byte)ClientChallenge.Length); w.Write(ClientChallenge);
        w.Write((byte)Proof.Length); w.Write(Proof);
    }

    public override void Read(BinaryReader r)
    {
        ClientChallenge = r.ReadBytes(r.ReadByte());
        Proof = r.ReadBytes(r.ReadByte());
    }
}

public sealed class MServerAuth : Msg
{
    public override MsgType Type => MsgType.ServerAuth;
    public byte[] Proof = Array.Empty<byte>();

    public override void Write(BinaryWriter w) { w.Write((byte)Proof.Length); w.Write(Proof); }
    public override void Read(BinaryReader r) { Proof = r.ReadBytes(r.ReadByte()); }
}

public sealed class MAuthFail : Msg
{
    public override MsgType Type => MsgType.AuthFail;
    public string Reason = "";
    public override void Write(BinaryWriter w) => w.Write(Reason);
    public override void Read(BinaryReader r) => Reason = r.ReadString();
}

public sealed class MScreenInfo : Msg
{
    public override MsgType Type => MsgType.ScreenInfo;
    public int Vx, Vy, Vw, Vh;
    public byte Codec;              // 0 = JPEG-тайлы, 1 = H.264
    public override void Write(BinaryWriter w) { w.Write(Vx); w.Write(Vy); w.Write(Vw); w.Write(Vh); w.Write(Codec); }
    public override void Read(BinaryReader r) { Vx = r.ReadInt32(); Vy = r.ReadInt32(); Vw = r.ReadInt32(); Vh = r.ReadInt32(); Codec = r.ReadByte(); }
}

public sealed class MVideoData : Msg
{
    public override MsgType Type => MsgType.H264Data;
    public long Pts;
    public bool Key;
    public byte[] Data = Array.Empty<byte>();
    public override void Write(BinaryWriter w) { w.Write(Pts); w.Write(Key); w.Write(Data.Length); w.Write(Data); }
    public override void Read(BinaryReader r) { Pts = r.ReadInt64(); Key = r.ReadBoolean(); Data = r.ReadBytes(r.ReadInt32()); }
}

/// <summary>Позиция курсора (шлётся с каждым кадром H.264; рисует viewer).</summary>
public sealed class MCursorState : Msg
{
    public override MsgType Type => MsgType.CursorState;
    public int X, Y;
    public bool Visible;
    public override void Write(BinaryWriter w) { w.Write(X); w.Write(Y); w.Write(Visible); }
    public override void Read(BinaryReader r) { X = r.ReadInt32(); Y = r.ReadInt32(); Visible = r.ReadBoolean(); }
}

/// <summary>Форма курсора как BGRA-битмап; шлётся при смене формы.</summary>
public sealed class MCursorShape : Msg
{
    public override MsgType Type => MsgType.CursorShape;
    public int Width, Height, HotX, HotY;
    public byte[] Bgra = Array.Empty<byte>();
    public override void Write(BinaryWriter w) { w.Write(Width); w.Write(Height); w.Write(HotX); w.Write(HotY); w.Write(Bgra.Length); w.Write(Bgra); }
    public override void Read(BinaryReader r) { Width = r.ReadInt32(); Height = r.ReadInt32(); HotX = r.ReadInt32(); HotY = r.ReadInt32(); Bgra = r.ReadBytes(r.ReadInt32()); }
}

public sealed class MTileBatch : Msg
{
    public override MsgType Type => MsgType.TileBatch;
    public int Vx, Vy, Vw, Vh;
    public List<Tile> Tiles = new();

    public sealed class Tile
    {
        public int Tx, Ty;
        public byte[] Data = Array.Empty<byte>();
    }

    public override void Write(BinaryWriter w)
    {
        w.Write(Vx); w.Write(Vy); w.Write(Vw); w.Write(Vh);
        w.Write(Tiles.Count);
        foreach (var t in Tiles)
        {
            w.Write(t.Tx); w.Write(t.Ty);
            w.Write(t.Data.Length); w.Write(t.Data);
        }
    }

    public override void Read(BinaryReader r)
    {
        Vx = r.ReadInt32(); Vy = r.ReadInt32(); Vw = r.ReadInt32(); Vh = r.ReadInt32();
        int n = r.ReadInt32();
        Tiles = new List<Tile>(n);
        for (int i = 0; i < n; i++)
        {
            var t = new Tile { Tx = r.ReadInt32(), Ty = r.ReadInt32(), Data = r.ReadBytes(r.ReadInt32()) };
            Tiles.Add(t);
        }
    }
}

public sealed class MRequestKeyframe : Msg
{
    public override MsgType Type => MsgType.RequestKeyframe;
    public override void Write(BinaryWriter w) { }
    public override void Read(BinaryReader r) { }
}

public sealed class MPing : Msg
{
    public override MsgType Type => MsgType.Ping;
    public long Stamp;
    public override void Write(BinaryWriter w) => w.Write(Stamp);
    public override void Read(BinaryReader r) => Stamp = r.ReadInt64();
}

public sealed class MPong : Msg
{
    public override MsgType Type => MsgType.Pong;
    public long Stamp;
    public override void Write(BinaryWriter w) => w.Write(Stamp);
    public override void Read(BinaryReader r) => Stamp = r.ReadInt64();
}

public sealed class MMouseMove : Msg
{
    public override MsgType Type => MsgType.MouseMove;
    public int X, Y;
    public override void Write(BinaryWriter w) { w.Write(X); w.Write(Y); }
    public override void Read(BinaryReader r) { X = r.ReadInt32(); Y = r.ReadInt32(); }
}

public sealed class MMouseButton : Msg
{
    public override MsgType Type => MsgType.MouseButton;
    public byte Button; // 0 left, 1 right, 2 middle
    public bool Down;
    public int X, Y;
    public override void Write(BinaryWriter w) { w.Write(Button); w.Write(Down); w.Write(X); w.Write(Y); }
    public override void Read(BinaryReader r) { Button = r.ReadByte(); Down = r.ReadBoolean(); X = r.ReadInt32(); Y = r.ReadInt32(); }
}

public sealed class MMouseWheel : Msg
{
    public override MsgType Type => MsgType.MouseWheel;
    public int Delta, X, Y;
    public override void Write(BinaryWriter w) { w.Write(Delta); w.Write(X); w.Write(Y); }
    public override void Read(BinaryReader r) { Delta = r.ReadInt32(); X = r.ReadInt32(); Y = r.ReadInt32(); }
}

public sealed class MKey : Msg
{
    public override MsgType Type => MsgType.Key;
    public int Vk;
    public bool Down;
    public override void Write(BinaryWriter w) { w.Write(Vk); w.Write(Down); }
    public override void Read(BinaryReader r) { Vk = r.ReadInt32(); Down = r.ReadBoolean(); }
}

public sealed class MText : Msg
{
    public override MsgType Type => MsgType.Text;
    public string Chars = "";
    public override void Write(BinaryWriter w) => w.Write(Chars);
    public override void Read(BinaryReader r) => Chars = r.ReadString();
}

public sealed class MSysInfoReq : Msg
{
    public override MsgType Type => MsgType.SysInfoReq;
    public override void Write(BinaryWriter w) { }
    public override void Read(BinaryReader r) { }
}

public abstract class MJson : Msg
{
    public string Json = "";
    public MJson() { }
    public MJson(string json) { Json = json; }
    public override void Write(BinaryWriter w) => w.Write(Json);
    public override void Read(BinaryReader r) => Json = r.ReadString();
}
// MJson используется для SysInfo / ProcList — тип сообщения задаётся явно:
public sealed class MSysInfo : MJson { public override MsgType Type => MsgType.SysInfo; public MSysInfo() { } public MSysInfo(string j) : base(j) { } }
public sealed class MProcList : MJson { public override MsgType Type => MsgType.ProcList; public MProcList() { } public MProcList(string j) : base(j) { } }

public sealed class MKillProc : Msg
{
    public override MsgType Type => MsgType.KillProc;
    public int Pid;
    public override void Write(BinaryWriter w) => w.Write(Pid);
    public override void Read(BinaryReader r) => Pid = r.ReadInt32();
}

public sealed class MRunProgram : Msg
{
    public override MsgType Type => MsgType.RunProgram;
    public string Command = "";
    public override void Write(BinaryWriter w) => w.Write(Command);
    public override void Read(BinaryReader r) => Command = r.ReadString();
}

public class MEmpty : Msg
{
    private readonly MsgType _type;
    public MEmpty(MsgType t) { _type = t; }
    public override MsgType Type => _type;
    public override void Write(BinaryWriter w) { }
    public override void Read(BinaryReader r) { }
}

// пустые запросы с фиксированным типом
public sealed class MProcListReq : MEmpty { public MProcListReq() : base(MsgType.ProcListReq) { } }
public sealed class MShellStart : MEmpty { public MShellStart() : base(MsgType.ShellStart) { } }
public sealed class MShellStop : MEmpty { public MShellStop() : base(MsgType.ShellStop) { } }
public sealed class MClipboardGet : MEmpty { public MClipboardGet() : base(MsgType.ClipboardGet) { } }

public sealed class MShellInput : Msg
{
    public override MsgType Type => MsgType.ShellInput;
    public string Line = "";
    public override void Write(BinaryWriter w) => w.Write(Line);
    public override void Read(BinaryReader r) => Line = r.ReadString();
}

public sealed class MShellOutput : Msg
{
    public override MsgType Type => MsgType.ShellOutput;
    public string Text = "";
    public bool IsErr;
    public override void Write(BinaryWriter w) { w.Write(Text); w.Write(IsErr); }
    public override void Read(BinaryReader r) { Text = r.ReadString(); IsErr = r.ReadBoolean(); }
}

public sealed class MFilePushStart : Msg
{
    public override MsgType Type => MsgType.FilePushStart;
    public string RemotePath = "";
    public long Size;
    public override void Write(BinaryWriter w) { w.Write(RemotePath); w.Write(Size); }
    public override void Read(BinaryReader r) { RemotePath = r.ReadString(); Size = r.ReadInt64(); }
}

public sealed class MFileData : Msg
{
    public override MsgType Type => MsgType.FileData;
    public bool Last;
    public byte[] Chunk = Array.Empty<byte>();
    public override void Write(BinaryWriter w) { w.Write(Last); w.Write(Chunk.Length); w.Write(Chunk); }
    public override void Read(BinaryReader r) { Last = r.ReadBoolean(); Chunk = r.ReadBytes(r.ReadInt32()); }
}

public sealed class MFilePull : Msg
{
    public override MsgType Type => MsgType.FilePull;
    public string RemotePath = "";
    public override void Write(BinaryWriter w) => w.Write(RemotePath);
    public override void Read(BinaryReader r) => RemotePath = r.ReadString();
}

public sealed class MFilePullOk : Msg
{
    public override MsgType Type => MsgType.FilePullOk;
    public string RemotePath = "";
    public long Size;
    public override void Write(BinaryWriter w) { w.Write(RemotePath); w.Write(Size); }
    public override void Read(BinaryReader r) { RemotePath = r.ReadString(); Size = r.ReadInt64(); }
}

public sealed class MFilePullDeny : Msg
{
    public override MsgType Type => MsgType.FilePullDeny;
    public string Error = "";
    public override void Write(BinaryWriter w) => w.Write(Error);
    public override void Read(BinaryReader r) => Error = r.ReadString();
}

public sealed class MPower : Msg
{
    public override MsgType Type => MsgType.Power;
    public byte Action; // 0 reboot, 1 shutdown, 2 logoff
    public override void Write(BinaryWriter w) => w.Write(Action);
    public override void Read(BinaryReader r) => Action = r.ReadByte();
}

public sealed class MClipboardSet : Msg
{
    public override MsgType Type => MsgType.ClipboardSet;
    public string Text = "";
    public override void Write(BinaryWriter w) => w.Write(Text);
    public override void Read(BinaryReader r) => Text = r.ReadString();
}

public sealed class MClipboardData : Msg
{
    public override MsgType Type => MsgType.ClipboardData;
    public string Text = "";
    public override void Write(BinaryWriter w) => w.Write(Text);
    public override void Read(BinaryReader r) => Text = r.ReadString();
}

public sealed class MSetOptions : Msg
{
    public override MsgType Type => MsgType.SetOptions;
    public int Fps, Quality;
    public override void Write(BinaryWriter w) { w.Write(Fps); w.Write(Quality); }
    public override void Read(BinaryReader r) { Fps = r.ReadInt32(); Quality = r.ReadInt32(); }
}

public sealed class MStatus : Msg
{
    public override MsgType Type { get; }
    public string Text = "";
    public MStatus(MsgType t, string text = "") { Type = t; Text = text; }
    public override void Write(BinaryWriter w) => w.Write(Text);
    public override void Read(BinaryReader r) => Text = r.ReadString();
}

public static class MsgCodec
{
    public static byte[] Encode(Msg m) => m.Encode();

    public static Msg Decode(MsgType t, byte[] payload)
    {
        Msg m;
        switch (t)
        {
            case MsgType.Hello: m = new MHello(); break;
            case MsgType.ClientAuth: m = new MClientAuth(); break;
            case MsgType.ServerAuth: m = new MServerAuth(); break;
            case MsgType.AuthFail: m = new MAuthFail(); break;
            case MsgType.ScreenInfo: m = new MScreenInfo(); break;
            case MsgType.TileBatch: m = new MTileBatch(); break;
            case MsgType.RequestKeyframe: m = new MRequestKeyframe(); break;
            case MsgType.Ping: m = new MPing(); break;
            case MsgType.Pong: m = new MPong(); break;
            default:
                m = CreateOther(t); break;
        }
        using var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
        m.Read(r);
        return m;
    }

    private static Msg CreateOther(MsgType t) => t switch
    {
        MsgType.MouseMove => new MMouseMove(),
        MsgType.MouseButton => new MMouseButton(),
        MsgType.MouseWheel => new MMouseWheel(),
        MsgType.Key => new MKey(),
        MsgType.Text => new MText(),
        MsgType.SysInfoReq => new MSysInfoReq(),
        MsgType.SysInfo => new MSysInfo(),
        MsgType.ProcListReq => new MProcListReq(),
        MsgType.ProcList => new MProcList(),
        MsgType.KillProc => new MKillProc(),
        MsgType.RunProgram => new MRunProgram(),
        MsgType.ShellStart => new MShellStart(),
        MsgType.ShellInput => new MShellInput(),
        MsgType.ShellOutput => new MShellOutput(),
        MsgType.ShellStop => new MShellStop(),
        MsgType.FilePushStart => new MFilePushStart(),
        MsgType.FileData => new MFileData(),
        MsgType.FilePull => new MFilePull(),
        MsgType.FilePullOk => new MFilePullOk(),
        MsgType.FilePullDeny => new MFilePullDeny(),
        MsgType.Power => new MPower(),
        MsgType.ClipboardSet => new MClipboardSet(),
        MsgType.ClipboardGet => new MClipboardGet(),
        MsgType.ClipboardData => new MClipboardData(),
        MsgType.SetOptions => new MSetOptions(),
        MsgType.Ok => new MStatus(MsgType.Ok),
        MsgType.Error => new MStatus(MsgType.Error),
        MsgType.H264Data => new MVideoData(),
        MsgType.CursorState => new MCursorState(),
        MsgType.CursorShape => new MCursorShape(),
        _ => new MEmpty(t),
    };
}
