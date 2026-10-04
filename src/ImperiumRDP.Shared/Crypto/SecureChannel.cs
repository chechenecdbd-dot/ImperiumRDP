using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using ImperiumRDP.Shared.Net;
using ImperiumRDP.Shared.Protocol;

namespace ImperiumRDP.Shared.Crypto;

/// <summary>
/// Шифрованный канал поверх NetworkStream.
/// Рукопожатие: PBKDF2(пароль, соль) -> взаимная аутентификация по HMAC-вызовам.
/// Далее каждое сообщение: AES-CBC (случайный IV) + HMAC-SHA256 (encrypt-then-MAC, счётчик последовательности).
/// </summary>
public sealed class SecureChannel : IDisposable
{
    private readonly NetworkStream _stream;
    private readonly object _sendLock = new();
    private readonly object _recvLock = new();

    private byte[] _encKeySend, _macKeySend, _encKeyRecv, _macKeyRecv;
    private long _sendSeq, _recvSeq;
    private Aes _aesSend, _aesRecv;

    public long TotalBytesSent, TotalBytesReceived;

    private SecureChannel(NetworkStream stream) { _stream = stream; }

    public static byte[] DeriveKey(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password ?? ""), salt, 100_000, HashAlgorithmName.SHA256, 32);
    }

    private static byte[] ServerProof(byte[] k, byte[] salt, byte[] chS, byte[] chC)
        => Proof(k, "IRDP-S", salt, chS, chC);

    private static byte[] ClientProof(byte[] k, byte[] salt, byte[] chS, byte[] chC)
        => Proof(k, "IRDP-C", salt, chS, chC);

    private static byte[] Proof(byte[] k, string label, byte[] salt, byte[] chS, byte[] chC)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(label));
        ms.Write(salt); ms.Write(chS); ms.Write(chC);
        return HMACSHA256.HashData(k, ms.ToArray());
    }

    /// <summary>Результат приёма подключения агентом.</summary>
    public readonly record struct AcceptResult(SecureChannel Channel, bool BadPassword);

    /// <summary>Серверная сторона (агент). Channel == null: сканер-проба (тихо) либо неверный пароль (BadPassword).</summary>
    public static async Task<AcceptResult> AcceptAsync(NetworkStream s, string password, MHello hello, CancellationToken ct)
    {
        await Framing.WriteFrameAsync(s, (byte)MsgType.Hello, hello.Encode(), ct).ConfigureAwait(false);

        var frame = await Framing.ReadFrameAsync(s, ct).ConfigureAwait(false);
        if (frame is null) return default;                       // закрылось до аутентификации (скан)
        if (frame.Value.type != (byte)MsgType.ClientAuth) return default;

        var auth = new MClientAuth();
        using (var r = new BinaryReader(new MemoryStream(frame.Value.payload)))
            auth.Read(r);

        byte[] k = DeriveKey(password, hello.Salt);
        byte[] expected = ClientProof(k, hello.Salt, hello.Challenge, auth.ClientChallenge);

        if (expected.Length != auth.Proof.Length ||
            !CryptographicOperations.FixedTimeEquals(expected, auth.Proof))
        {
            await Framing.WriteFrameAsync(s, (byte)MsgType.AuthFail,
                new MAuthFail { Reason = "Неверный пароль" }.Encode(), CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            return new AcceptResult(null, BadPassword: true);
        }

        var ch = new SecureChannel(s);
        await Framing.WriteFrameAsync(s, (byte)MsgType.ServerAuth,
            new MServerAuth { Proof = ServerProof(k, hello.Salt, hello.Challenge, auth.ClientChallenge) }.Encode(),
            ct).ConfigureAwait(false);
        ch.InitKeys(k, isServer: true);
        ch.Hello = hello;
        return new AcceptResult(ch, BadPassword: false);
    }

    /// <summary>Клиентская сторона (viewer). Бросает исключение при ошибке аутентификации.</summary>
    public static async Task<SecureChannel> ConnectAsync(NetworkStream s, string password, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var frame = await Framing.ReadFrameAsync(s, cts.Token).ConfigureAwait(false);
        if (frame is null || frame.Value.type != (byte)MsgType.Hello)
            throw new IOException("Агент не ответил приветствием");

        var hello = new MHello();
        using (var r = new BinaryReader(new MemoryStream(frame.Value.payload)))
            hello.Read(r);
        if (hello.Magic != "IRDP1")
            throw new IOException("Неизвестный протокол на порту");

        byte[] k = DeriveKey(password, hello.Salt);
        byte[] chC = RandomNumberGenerator.GetBytes(16);
        var auth = new MClientAuth
        {
            ClientChallenge = chC,
            Proof = ClientProof(k, hello.Salt, hello.Challenge, chC)
        };
        await Framing.WriteFrameAsync(s, (byte)MsgType.ClientAuth, auth.Encode(), cts.Token).ConfigureAwait(false);

        var reply = await Framing.ReadFrameAsync(s, cts.Token).ConfigureAwait(false);
        if (reply is null) throw new IOException("Соединение закрыто при аутентификации");

        if (reply.Value.type == (byte)MsgType.AuthFail)
        {
            var f = new MAuthFail();
            using (var r = new BinaryReader(new MemoryStream(reply.Value.payload))) f.Read(r);
            throw new AuthenticationException($"Агент отклонил подключение: {f.Reason}");
        }

        if (reply.Value.type != (byte)MsgType.ServerAuth)
            throw new IOException("Неожиданный ответ агента");

        var sa = new MServerAuth();
        using (var r = new BinaryReader(new MemoryStream(reply.Value.payload))) sa.Read(r);

        byte[] expected = ServerProof(k, hello.Salt, hello.Challenge, chC);
        if (expected.Length != sa.Proof.Length ||
            !CryptographicOperations.FixedTimeEquals(expected, sa.Proof))
            throw new AuthenticationException("Агент не прошёл проверку подлинности");

        var ch = new SecureChannel(s);
        ch.InitKeys(k, isServer: false);
        ch.Hello = hello;
        return ch;
    }

    public MHello Hello { get; private set; }

    private void InitKeys(byte[] k, bool isServer)
    {
        // сервер шлёт s2c и принимает c2s; клиент — наоборот
        string sendDir = isServer ? "s2c" : "c2s";
        string recvDir = isServer ? "c2s" : "s2c";
        _encKeySend = HMACSHA256.HashData(k, Encoding.ASCII.GetBytes("enc-" + sendDir));
        _macKeySend = HMACSHA256.HashData(k, Encoding.ASCII.GetBytes("mac-" + sendDir));
        _encKeyRecv = HMACSHA256.HashData(k, Encoding.ASCII.GetBytes("enc-" + recvDir));
        _macKeyRecv = HMACSHA256.HashData(k, Encoding.ASCII.GetBytes("mac-" + recvDir));
        _aesSend = Aes.Create(); _aesSend.Key = _encKeySend;
        _aesRecv = Aes.Create(); _aesRecv.Key = _encKeyRecv;
    }

    public async Task SendAsync(byte type, byte[] payload, CancellationToken ct)
    {
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        byte[] cipher;
        byte[] mac;
        byte[] wire;
        lock (_sendLock)
        {
            long seq = _sendSeq++;
            cipher = _aesSend.EncryptCbc(payload, iv, System.Security.Cryptography.PaddingMode.PKCS7);

            using var ms = new MemoryStream(8 + 1 + 16 + cipher.Length);
            byte[] seqBuf = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(seqBuf, seq);
            ms.Write(seqBuf, 0, 8);
            ms.WriteByte(type);
            ms.Write(iv);
            ms.Write(cipher);
            mac = HMACSHA256.HashData(_macKeySend, ms.ToArray());

            wire = new byte[16 + cipher.Length + 32];
            Buffer.BlockCopy(iv, 0, wire, 0, 16);
            Buffer.BlockCopy(cipher, 0, wire, 16, cipher.Length);
            Buffer.BlockCopy(mac, 0, wire, 16 + cipher.Length, 32);
        }

        await Framing.WriteFrameAsync(_stream, type, wire, ct).ConfigureAwait(false);
        Interlocked.Add(ref TotalBytesSent, 5 + wire.Length);
    }

    /// <summary>null — соединение закрыто.</summary>
    public async Task<(byte type, byte[] payload)?> ReceiveAsync(CancellationToken ct)
    {
        var frame = await Framing.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
        if (frame is null) return null;

        byte type = frame.Value.type;
        byte[] wire = frame.Value.payload;
        Interlocked.Add(ref TotalBytesReceived, 5 + wire.Length);

        if (wire.Length < 16 + 16 + 32)
            throw new IOException("Кадр слишком короткий");

        byte[] iv = new byte[16];
        Buffer.BlockCopy(wire, 0, iv, 0, 16);
        int cipherLen = wire.Length - 16 - 32;
        byte[] cipher = new byte[cipherLen];
        Buffer.BlockCopy(wire, 16, cipher, 0, cipherLen);
        byte[] mac = new byte[32];
        Buffer.BlockCopy(wire, 16 + cipherLen, mac, 0, 32);

        byte[] plain;
        lock (_recvLock)
        {
            long seq = _recvSeq;
            using var ms = new MemoryStream(8 + 1 + 16 + cipherLen);
            byte[] seqBuf = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(seqBuf, seq);
            ms.Write(seqBuf, 0, 8);
            ms.WriteByte(type);
            ms.Write(iv);
            ms.Write(cipher);
            byte[] expected = HMACSHA256.HashData(_macKeyRecv, ms.ToArray());

            if (expected.Length != mac.Length ||
                !CryptographicOperations.FixedTimeEquals(expected, mac))
                throw new IOException("Нарушена целостность сообщения (HMAC)");

            plain = _aesRecv.DecryptCbc(cipher, iv, System.Security.Cryptography.PaddingMode.PKCS7);
            _recvSeq++;
        }
        return (type, plain);
    }

    public void Dispose()
    {
        _aesSend?.Dispose();
        _aesRecv?.Dispose();
    }
}
