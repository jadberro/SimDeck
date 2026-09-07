using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimDeck.Core;

namespace SimDeck.Bench.Network;

public sealed class BenchPanelClient : IDisposable
{
    private const int HelloIntervalUnlinked = 3000;
    private const int HelloIntervalLinked = 15000;
    private const int PingInterval = 2000;
    private const int TimeoutMs = 1500;

    private readonly UdpClient _udp;
    private readonly IPEndPoint _hubEp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _helloTimer;
    private readonly Timer _pingTimer;
    private readonly Timer _timeoutCheckTimer;
    private readonly Stopwatch _fpsSw = new();
    private readonly SHA256 _sha = SHA256.Create();
    private readonly Dictionary<string, int> _slots = new();
    private readonly object _lock = new();

    private bool _isLinked;
    private bool _live;
    private float _accum;
    private float _left;
    private float _right;
    private long _lastFrameTicks;
    private int _frameCount;
    private double _fps;
    private ushort _lastSeq;

    public string ModuleId { get; } = "accu-bench";
    public string ModuleType { get; } = "accu_panel";
    public string ModuleName { get; } = "A320 Accu/Brake (Bench)";
    public string CurrentFw { get; private set; } = "1.0.0";
    public bool IsLinked => _isLinked;
    public bool Live => _live;
    public double FrameRate => _fps;
    public ushort LastSeq => _lastSeq;

    public event Action<(double accum, double left, double right, bool live)>? ValuesReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? LogMessage;
    public event Action? Identify;
    public event Action<string, int, string?>? OtaProgress;

    public BenchPanelClient()
    {
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, Protocol.ModulePort));

        _hubEp = new IPEndPoint(IPAddress.Loopback, Protocol.CtrlPort);

        _helloTimer = new Timer(_ => SendHello(), null, 100, HelloIntervalUnlinked);
        _pingTimer = new Timer(_ => SendPing(), null, PingInterval, PingInterval);
        _timeoutCheckTimer = new Timer(_ => CheckTimeout(), null, 500, 250);

        Task.Run(ReceiveLoop);
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var res = await _udp.ReceiveAsync(_cts.Token);
                HandlePacket(res.Buffer);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[bench] receive error: {ex.Message}");
            }
        }
    }

    private void HandlePacket(byte[] buf)
    {
        if (buf.Length == 0) return;

        if (buf[0] == '{')
        {
            HandleJson(buf);
        }
        else if (buf.Length >= Protocol.HeaderLen && buf[0] == Protocol.DataMagic)
        {
            HandleBinary(buf);
        }
    }

    private void HandleJson(byte[] buf)
    {
        try
        {
            using var doc = JsonDocument.Parse(buf);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var tProp)) return;
            var t = tProp.GetString();

            switch (t)
            {
                case "welcome":
                    lock (_lock)
                    {
                        _isLinked = true;
                        _slots.Clear();
                        if (root.TryGetProperty("slots", out var slotsObj))
                        {
                            foreach (var s in slotsObj.EnumerateObject())
                            {
                                _slots[s.Name] = s.Value.GetInt32();
                            }
                        }
                    }
                    _helloTimer.Change(HelloIntervalLinked, HelloIntervalLinked);
                    StatusChanged?.Invoke("Linked to Hub");
                    LogMessage?.Invoke($"[bench] Welcome received, {_slots.Count} slot(s) mapped");
                    break;

                case "identify":
                    Identify?.Invoke();
                    LogMessage?.Invoke("[bench] Identify request received");
                    break;

                case "ota":
                    HandleOta(root);
                    break;

                case "pong":
                    break;
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[bench] JSON parse error: {ex.Message}");
        }
    }

    private void HandleBinary(byte[] buf)
    {
        if (!Protocol.TryDecodeFrame(buf, out var seq, out var flags, out var vals))
            return;

        _lastFrameTicks = Stopwatch.GetTimestamp();
        _lastSeq = seq;
        bool simOk = (flags & Protocol.FlagSimOk) != 0;
        bool profileOk = (flags & Protocol.FlagProfileOk) != 0;

        _live = simOk && profileOk;

        _frameCount++;
        if (!_fpsSw.IsRunning) _fpsSw.Start();
        else if (_fpsSw.ElapsedMilliseconds >= 1000)
        {
            _fps = _frameCount * 1000.0 / _fpsSw.ElapsedMilliseconds;
            _frameCount = 0;
            _fpsSw.Restart();
        }

        lock (_lock)
        {
            if (_slots.TryGetValue("brake.accum_psi", out var ai) && ai < vals.Length)
                _accum = float.IsNaN(vals[ai]) ? 0 : vals[ai];
            if (_slots.TryGetValue("brake.left_psi", out var li) && li < vals.Length)
                _left = float.IsNaN(vals[li]) ? 0 : vals[li];
            if (_slots.TryGetValue("brake.right_psi", out var ri) && ri < vals.Length)
                _right = float.IsNaN(vals[ri]) ? 0 : vals[ri];
        }

        ValuesReceived?.Invoke((_accum, _left, _right, _live));
    }

    private void CheckTimeout()
    {
        if (_lastFrameTicks == 0) return;
        var elapsedMs = (Stopwatch.GetTimestamp() - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs > TimeoutMs && _live)
        {
            _live = false;
            ValuesReceived?.Invoke((0, 0, 0, false));
            StatusChanged?.Invoke("Stream timed out (waiting for data)");
        }
    }

    private void SendHello()
    {
        var msg = new
        {
            t = "hello",
            id = ModuleId,
            type = ModuleType,
            name = ModuleName,
            fw = CurrentFw,
            rate = 30,
            sub = new[] { "brake.accum_psi", "brake.left_psi", "brake.right_psi" }
        };
        SendJson(msg);
    }

    private void SendPing()
    {
        if (_isLinked)
        {
            SendJson(new { t = "ping", id = ModuleId });
        }
    }

    public void SendInput(string inputName, double value)
    {
        var msg = new
        {
            t = "ev",
            id = ModuleId,
            @in = inputName,
            v = value
        };
        SendJson(msg);
        LogMessage?.Invoke($"[bench] Event fired: {inputName} = {value}");
    }

    private void SendOtaStatus(string state, int pct = 0, string? err = null)
    {
        var msg = new
        {
            t = "ota_status",
            id = ModuleId,
            state,
            pct,
            err
        };
        SendJson(msg);
    }

    private async void HandleOta(JsonElement root)
    {
        if (!root.TryGetProperty("url", out var u) ||
            !root.TryGetProperty("sha256", out var s) ||
            !root.TryGetProperty("ver", out var v))
            return;

        var url = u.GetString()!;
        var expectedHash = s.GetString()!;
        var version = v.GetString()!;

        LogMessage?.Invoke($"[bench] OTA update offered: v{version} from {url}");
        OtaProgress?.Invoke("start", 0, null);
        SendOtaStatus("start", 0);

        try
        {
            using var http = new HttpClient();
            var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? 0L;
            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var ms = new MemoryStream();

            var buf = new byte[8192];
            long read = 0;
            int n;
            int lastPct = 0;

            while ((n = await stream.ReadAsync(buf)) > 0)
            {
                ms.Write(buf, 0, n);
                read += n;
                if (total > 0)
                {
                    int pct = (int)(read * 100 / total);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        OtaProgress?.Invoke("progress", pct, null);
                        SendOtaStatus("progress", pct);
                    }
                }
            }

            var hash = Convert.ToHexString(_sha.ComputeHash(ms.ToArray())).ToLowerInvariant();
            if (string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                CurrentFw = version;
                OtaProgress?.Invoke("ok", 100, null);
                SendOtaStatus("ok", 100);
                LogMessage?.Invoke($"[bench] OTA completed successfully! Upgraded to v{CurrentFw}");
            }
            else
            {
                var err = $"SHA-256 mismatch (got {hash[..8]}..., expected {expectedHash[..8]}...)";
                OtaProgress?.Invoke("fail", 0, err);
                SendOtaStatus("fail", 0, err);
                LogMessage?.Invoke($"[bench] OTA failed: {err}");
            }
        }
        catch (Exception ex)
        {
            OtaProgress?.Invoke("fail", 0, ex.Message);
            SendOtaStatus("fail", 0, ex.Message);
            LogMessage?.Invoke($"[bench] OTA error: {ex.Message}");
        }
    }

    private void SendJson(object obj)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, Json.Options);
            _udp.Send(bytes, bytes.Length, _hubEp);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[bench] send error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _helloTimer.Dispose();
        _pingTimer.Dispose();
        _timeoutCheckTimer.Dispose();
        _udp.Dispose();
        _sha.Dispose();
        _cts.Dispose();
    }
}
