using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Solarpunk.Launcher;

/// <summary>
/// Minimal Source A2S_INFO client used by the launcher to ping listed
/// servers. Returns null on timeout/error. Used to show live server name,
/// map, player count, and ping latency in the row.
/// </summary>
public static class A2sClient
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(1);

    public sealed record A2sInfo(
        string Name,
        string Map,
        string Game,
        int Players,
        int MaxPlayers,
        int LatencyMs,
        long SteamAppId,
        bool IsPasswordProtected);

    public static async Task<A2sInfo?> QueryAsync(string host, int port, CancellationToken ct = default)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 1500;
            var req = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x54 }
                .Concat(Encoding.ASCII.GetBytes("Source Engine Query\0")).ToArray();
            var ep = await ResolveAsync(host, port, ct).ConfigureAwait(false);

            // Time only the LAST round trip. If the server replies with a
            // challenge first, that round-trip is bookkeeping and shouldn't
            // count toward the ping number we show users. Re-time after the
            // challenge resend so we measure the network RTT to the actual
            // A2S_INFO response.
            var sentAt = DateTime.UtcNow;
            await udp.SendAsync(req, req.Length, ep).ConfigureAwait(false);

            var recv = await udp.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
            byte[] data = recv.Buffer;
            if (data.Length > 5 && data[4] == 0x41)
            {
                var challenged = req.Concat(data.Skip(5)).ToArray();
                sentAt = DateTime.UtcNow; // reset — only the second exchange is the real ping
                await udp.SendAsync(challenged, challenged.Length, ep).ConfigureAwait(false);
                recv = await udp.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
                data = recv.Buffer;
            }
            var latencyMs = (int)(DateTime.UtcNow - sentAt).TotalMilliseconds;
            if (data.Length < 6 || BinaryPrimitives.ReadInt32LittleEndian(data) != -1 || data[4] != 0x49)
                return null;
            int i = 6;
            var name = ReadCString(data, ref i);
            var map = ReadCString(data, ref i);
            var folder = ReadCString(data, ref i);
            var game = ReadCString(data, ref i);
            var legacyAppId = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i, 2));
            i += 2;
            var players = data[i++];
            var maxPlayers = data[i++];
            i += 1; // bots
            i += 1; // server type
            i += 1; // env
            // A2S_INFO visibility byte: 0 = public, 1 = password-protected.
            // BeaconServer's SourceQueryServer sets this based on the
            // configured BeaconAuthPassword, so a non-empty join password
            // surfaces here even though the game itself stays open. The
            // launcher uses this to know whether to prompt for a password
            // pre-flight when the customer's saved entry has none.
            //
            // No bounds-check shim here — `data[i]` will throw on a truncated
            // reply, the surrounding try/catch returns null, and the caller
            // treats null as "no answer" (IsPasswordProtected stays at
            // whatever the previous successful query reported, including
            // null = unknown). Softening this to `i < data.Length && data[i]
            // == 1` would let a malformed response be misread as "open
            // server" and silently suppress the pre-flight password prompt.
            bool isPasswordProtected = data[i] == 1;
            i += 1; // visibility
            i += 1; // vac
            var version = ReadCString(data, ref i);
            long appId = legacyAppId;
            if (i < data.Length)
            {
                byte edf = data[i++];
                if ((edf & 0x80) != 0) i += 2; // port
                if ((edf & 0x10) != 0) i += 8; // steamid
                if ((edf & 0x40) != 0) { ReadCString(data, ref i); i += 2; }
                if ((edf & 0x20) != 0) ReadCString(data, ref i); // keywords
                if ((edf & 0x01) != 0 && i + 8 <= data.Length)
                {
                    appId = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(i, 8));
                    i += 8;
                }
            }
            return new A2sInfo(name, map, game, players, maxPlayers, latencyMs, appId, isPasswordProtected);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IPEndPoint> ResolveAsync(string host, int port, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip))
            return new IPEndPoint(ip, port);

        using var resolveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        resolveCts.CancelAfter(ResolveTimeout);
        var entries = await Dns.GetHostAddressesAsync(host, resolveCts.Token).ConfigureAwait(false);
        var v4 = entries.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? entries[0];
        return new IPEndPoint(v4, port);
    }

    private static string ReadCString(byte[] buf, ref int i)
    {
        int start = i;
        while (i < buf.Length && buf[i] != 0) i++;
        var s = Encoding.UTF8.GetString(buf, start, i - start);
        if (i < buf.Length) i++; // skip null
        return s;
    }
}
