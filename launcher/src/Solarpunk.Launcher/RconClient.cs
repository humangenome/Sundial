using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Solarpunk.Launcher;

/// <summary>
/// Source RCON over TCP client. Authenticates with the configured password,
/// then sends arbitrary command strings and yields the server's reply.
/// Used by the launcher's Console tab.
/// </summary>
public sealed class RconClient : IDisposable
{
    private readonly TcpClient _tcp = new();
    private NetworkStream? _stream;
    private int _nextId = 1;
    private bool _authed;

    public bool Authenticated => _authed;

    public async Task<bool> ConnectAndAuthAsync(string host, int port, string password, CancellationToken ct = default)
    {
        await _tcp.ConnectAsync(host, port, ct);
        _stream = _tcp.GetStream();
        var authId = _nextId++;
        await WritePacketAsync(authId, 3, password, ct);
        // First reply: type 0 empty body
        var p1 = await ReadPacketAsync(ct);
        if (p1 is null) return false;
        // Second reply: type 2, id == authId on success, -1 on fail
        var p2 = await ReadPacketAsync(ct);
        if (p2 is null) return false;
        _authed = p2.Value.id == authId;
        return _authed;
    }

    public async Task<string> ExecAsync(string command, CancellationToken ct = default)
    {
        if (!_authed || _stream is null) throw new InvalidOperationException("not authenticated");
        var id = _nextId++;
        await WritePacketAsync(id, 2, command, ct);
        var reply = await ReadPacketAsync(ct);
        return reply?.body ?? "";
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
    }

    private async Task WritePacketAsync(int id, int type, string body, CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("not connected");
        var b = Encoding.UTF8.GetBytes(body);
        var size = 4 + 4 + b.Length + 2;
        var buf = new byte[4 + size];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), size);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), id);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), type);
        Buffer.BlockCopy(b, 0, buf, 12, b.Length);
        await _stream.WriteAsync(buf, ct);
        await _stream.FlushAsync(ct);
    }

    private async Task<(int id, int type, string body)?> ReadPacketAsync(CancellationToken ct)
    {
        if (_stream is null) return null;
        var sizeBuf = new byte[4];
        if (!await ReadExactAsync(sizeBuf, ct)) return null;
        var size = BinaryPrimitives.ReadInt32LittleEndian(sizeBuf);
        if (size < 10 || size > 1024 * 1024) return null;
        var rest = new byte[size];
        if (!await ReadExactAsync(rest, ct)) return null;
        var id = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(0, 4));
        var type = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(4, 4));
        var end = Array.IndexOf<byte>(rest, 0, 8);
        if (end < 0) end = rest.Length - 1;
        var body = Encoding.UTF8.GetString(rest, 8, end - 8);
        return (id, type, body);
    }

    private async Task<bool> ReadExactAsync(byte[] buf, CancellationToken ct)
    {
        if (_stream is null) return false;
        var read = 0;
        while (read < buf.Length)
        {
            var n = await _stream.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
