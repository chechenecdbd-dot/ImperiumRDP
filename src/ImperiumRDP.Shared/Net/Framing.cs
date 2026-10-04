using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace ImperiumRDP.Shared.Net;

/// <summary>Обрамление сообщений: [int32 BE длина][byte тип][полезная нагрузка].</summary>
public static class Framing
{
    public const int MaxFrame = 36_000_000;

    public static async Task WriteFrameAsync(NetworkStream s, byte type, byte[] payload, CancellationToken ct)
    {
        byte[] head = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(head, payload.Length + 1);
        head[4] = type;
        await s.WriteAsync(head, ct).ConfigureAwait(false);
        if (payload.Length > 0)
            await s.WriteAsync(payload, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>null — соединение закрыто.</summary>
    public static async Task<(byte type, byte[] payload)?> ReadFrameAsync(NetworkStream s, CancellationToken ct)
    {
        byte[] head = new byte[5];
        if (!await ReadExactAsync(s, head, ct).ConfigureAwait(false)) return null;
        int total = BinaryPrimitives.ReadInt32BigEndian(head);
        if (total < 1 || total > MaxFrame) throw new IOException($"Некорректная длина кадра: {total}");
        byte[] payload = new byte[total - 1];
        if (payload.Length > 0 && !await ReadExactAsync(s, payload, ct).ConfigureAwait(false)) return null;
        return (head[4], payload);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream s, byte[] buf, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(off, buf.Length - off), ct).ConfigureAwait(false);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }
}
