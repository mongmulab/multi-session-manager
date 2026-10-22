using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiSessionManager.Agents;

/// <summary>CLI 가 알려 온 세션 상태. 터미널 화면을 읽지 않고 Hook 으로만 안다.</summary>
public enum AgentActivity
{
    /// <summary>아직 아무 알림이 없었다(막 띄웠거나 Hook 을 못 받는 CLI).</summary>
    Unknown,
    /// <summary>요청을 받아 일하는 중.</summary>
    Working,
    /// <summary>한 턴을 끝내고 다음 입력을 기다린다.</summary>
    Waiting,
    /// <summary>권한 확인 등 사람의 답이 있어야 다음으로 간다.</summary>
    NeedsInput,
    /// <summary>API 오류 등으로 턴이 실패했다.</summary>
    Failed,
}

/// <summary>
/// Claude Code 의 HTTP Hook 을 받는 작은 수신기. 127.0.0.1 의 빈 포트에서 POST 만 받는다.
/// 주소에 실행마다 새로 만든 토큰을 넣어, 같은 PC 의 다른 프로그램이 상태를 꾸며 보내지 못하게 한다.
/// HttpListener 는 쓰지 않는다. 관리자가 아니면 URL 예약(urlacl)이 있어야 해서다.
/// </summary>
public sealed class HookServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    // PostToolUse 본문에는 도구 결과가 통째로 실린다. 그 이상은 읽지 않고 버린다.
    private const int MaxBodyBytes = 32 * 1024 * 1024;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>세션 ID 와 상태. 스레드풀에서 불린다.</summary>
    public event Action<string, AgentActivity>? Received;

    public int Port { get; }

    public HookServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    /// <summary>이 세션의 Hook 이 POST 할 주소.</summary>
    public string UrlFor(string sessionId) => $"http://127.0.0.1:{Port}/hook/{_token}/{Uri.EscapeDataString(sessionId)}";

    /// <summary>Hook 이름(과 Notification 종류)을 상태로 옮긴다. 상태와 상관없는 알림이면 null.</summary>
    public static AgentActivity? Map(string? hookEvent, string? notificationType) => hookEvent switch
    {
        "UserPromptSubmit" or "PostToolUse" or "PostToolUseFailure" => AgentActivity.Working,
        "Stop" => AgentActivity.Waiting,
        "StopFailure" => AgentActivity.Failed,
        "PermissionRequest" => AgentActivity.NeedsInput,
        "Notification" => notificationType switch
        {
            "permission_prompt" or "elicitation_dialog" or "elicitation_url_dialog" or "agent_needs_input" => AgentActivity.NeedsInput,
            "idle_prompt" => AgentActivity.Waiting,
            _ => null,
        },
        _ => null,
    };

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                AppLog.Error("Hook 수신 대기 실패", ex);
                continue;
            }
            _ = Handle(client);
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                var (status, sessionId, activity) = await Read(stream, timeout.Token);
                var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(reply, timeout.Token);
                if (sessionId is not null && activity is { } a)
                    Received?.Invoke(sessionId, a);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
                // Claude 가 기다리다 끊었거나 앱이 닫히는 중. 상태 하나를 놓칠 뿐이다.
            }
        }
    }

    private async Task<(string Status, string? SessionId, AgentActivity? Activity)> Read(NetworkStream stream, CancellationToken ct)
    {
        var header = await ReadHeader(stream, ct);
        if (header is null) return ("400 Bad Request", null, null);

        var lines = header.Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length < 2 || request[0] != "POST") return ("405 Method Not Allowed", null, null);

        // /hook/<토큰>/<세션 ID>
        var parts = request[1].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "hook" || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[1]), Encoding.ASCII.GetBytes(_token)))
            return ("403 Forbidden", null, null);
        var sessionId = Uri.UnescapeDataString(parts[2]);

        long length = 0;
        foreach (var line in lines.Skip(1))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                long.TryParse(line["Content-Length:".Length..].Trim(), out length);
        if (length <= 0 || length > MaxBodyBytes) return ("413 Payload Too Large", null, null);

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var hookEvent = root.TryGetProperty("hook_event_name", out var e) ? e.GetString() : null;
            var kind = root.TryGetProperty("notification_type", out var n) ? n.GetString() : null;
            return ("200 OK", sessionId, Map(hookEvent, kind));
        }
        catch (JsonException)
        {
            return ("400 Bad Request", null, null);
        }
    }

    // 머리말 끝(빈 줄)까지 읽는다. 본문 첫 부분이 같이 읽히지 않게 한 바이트씩 읽는다(머리말은 짧다).
    private static async Task<string?> ReadHeader(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(512);
        var one = new byte[1];
        while (buffer.Count < MaxHeaderBytes)
        {
            if (await stream.ReadAsync(one, ct) == 0) return null;
            buffer.Add(one[0]);
            var c = buffer.Count;
            if (c >= 4 && buffer[c - 4] == '\r' && buffer[c - 3] == '\n' && buffer[c - 2] == '\r' && buffer[c - 1] == '\n')
                return Encoding.ASCII.GetString(buffer.ToArray(), 0, c - 4);
        }
        return null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
