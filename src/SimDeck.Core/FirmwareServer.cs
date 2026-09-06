using System.Net;
using System.Net.Sockets;
using System.Text;
using SimDeck.Core.Ota;

namespace SimDeck.Core;

/// <summary>
/// Serves firmware images so modules can pull them.
///
/// Deliberately a raw TcpListener rather than HttpListener. HttpListener sits
/// on HTTP.sys, which refuses to bind any prefix without a URL ACL
/// reservation, so it fails with access denied unless the app runs elevated
/// or an installer ran netsh first. A plain socket has no such requirement,
/// and the ESP32 only ever issues one simple GET.
///
/// Measured consequence of the old approach: on a normal user account the
/// server never started and every OTA download was refused at 127.0.0.1.
/// </summary>
public sealed class FirmwareServer : IDisposable
{
    private readonly FirmwareRepo _repo;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public bool Running { get; private set; }
    public int Port { get; }

    public FirmwareServer(FirmwareRepo repo, int port = Protocol.HttpPort)
    {
        _repo = repo;
        Port = port;
    }

    public bool TryStart(out string error)
    {
        error = "";
        try
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoop(_cts.Token));
            Running = true;
            return true;
        }
        catch (SocketException ex)
        {
            // Genuinely in use by something else, which is worth reporting,
            // unlike the old access-denied case which was self-inflicted.
            error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"port {Port} is already in use"
                : ex.Message;
            return false;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            _ = Task.Run(() => Serve(client), ct);
        }
    }

    private async Task Serve(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 30000;
                using var stream = client.GetStream();

                var path = await ReadRequestPath(stream);
                if (path is null) { await Respond(stream, 400, "Bad Request"); return; }

                // The images directory is reachable from the LAN. Take the
                // file name only and require the extension, so no crafted
                // path can climb out of it.
                var name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name)
                    || !name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("..", StringComparison.Ordinal))
                {
                    await Respond(stream, 400, "Bad Request");
                    return;
                }

                var full = Path.Combine(_repo.Directory, name);
                if (!File.Exists(full)) { await Respond(stream, 404, "Not Found"); return; }

                var info = new FileInfo(full);
                var header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: application/octet-stream\r\n"
                    + $"Content-Length: {info.Length}\r\n"
                    + "Connection: close\r\n\r\n");

                await stream.WriteAsync(header);

                // Stream from disk rather than loading the image into memory,
                // so the hub's footprint stays flat regardless of image size.
                await using var fs = File.OpenRead(full);
                await fs.CopyToAsync(stream, 32 * 1024);
                await stream.FlushAsync();
            }
        }
        catch { /* module went away mid-download; it will retry */ }
    }

    /// <summary>Reads the request line and ignores the headers. Only GET
    /// matters here, and only its path.</summary>
    private static async Task<string?> ReadRequestPath(NetworkStream stream)
    {
        var buf = new byte[2048];
        var read = 0;

        while (read < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(read, buf.Length - read));
            if (n == 0) break;
            read += n;

            var text = Encoding.ASCII.GetString(buf, 0, read);
            var eol = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (eol < 0) continue;                       // request line incomplete

            var parts = text[..eol].Split(' ');
            if (parts.Length < 2) return null;
            if (!parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase)) return null;
            return parts[1];
        }
        return null;
    }

    private static async Task Respond(NetworkStream stream, int code, string reason)
    {
        var body = Encoding.ASCII.GetBytes(reason);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\n"
            + "Content-Type: text/plain\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Connection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        Running = false;
    }
}
