using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using MultiSessionManager.Sessions;

namespace MultiSessionManager.Terminal;

/// <summary>
/// WebView2 안의 xterm.js 와 주고받는 메시지를 맡는다.
/// 출력은 세션별로 모았다가 16ms 마다 한 번씩 보낸다. Claude 가 화면을 다시 그릴 때
/// 조각이 수백 개씩 나오는데, 조각마다 PostWebMessage 를 부르면 UI 스레드가 밀린다.
/// </summary>
public sealed class TerminalBridge : IDisposable
{
    // .local 은 mDNS 조회 대상이라 쓰지 않는다. WebView2 문서가 권하는 대로 예약 TLD .example 을 쓴다.
    public const string VirtualHost = "msm.example";

    private readonly CoreWebView2 _web;
    private readonly ConcurrentDictionary<string, StringBuilder> _pending = new();
    private readonly DispatcherTimer _flushTimer;
    private bool _ready;
    private readonly List<string> _beforeReady = [];

    public event Action<string, string>? Input;
    public event Action<string, int, int>? Resized;
    public event Action? Ready;
    public event Action<int>? FocusRequested;
    public event Action<double, double>? RatiosChanged;
    public event Action<string>? Command;

    public TerminalBridge(CoreWebView2 web, string webRoot)
    {
        _web = web;
        _web.SetVirtualHostNameToFolderMapping(VirtualHost, webRoot, CoreWebView2HostResourceAccessKind.Deny);
        _web.Settings.AreDefaultContextMenusEnabled = false;
        // F5·Ctrl+F·Ctrl+P 같은 브라우저 단축키를 끈다. 복사·붙여넣기는 이 설정과 무관하게 동작한다.
        _web.Settings.AreBrowserAcceleratorKeysEnabled = false;
        _web.Settings.IsStatusBarEnabled = false;
        _web.Settings.IsZoomControlEnabled = false;
        _web.WebMessageReceived += OnMessage;

        _flushTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    public void Navigate() => _web.Navigate($"https://{VirtualHost}/index.html");

    /// <summary>읽기 스레드에서 불러도 된다.</summary>
    public void QueueOutput(string sessionId, string text)
    {
        var sb = _pending.GetOrAdd(sessionId, _ => new StringBuilder());
        lock (sb) sb.Append(text);
    }

    public void Create(string sessionId) => Send(new { t = "create", id = sessionId });
    public void ApplyLayout(PaneLayout layout) =>
        Send(new { t = "layout", mode = layout.Mode, slots = layout.Slots, focus = layout.Focus, col = layout.ColumnRatio, row = layout.RowRatio });

    public void SetTitle(string sessionId, string title, string status) =>
        Send(new { t = "title", id = sessionId, title, status });
    public void ShowExit(string sessionId, string text)
    {
        Flush();
        Send(new { t = "exit", id = sessionId, text });
    }
    public void Remove(string sessionId)
    {
        _pending.TryRemove(sessionId, out _);
        Send(new { t = "dispose", id = sessionId });
    }

    private void Flush()
    {
        if (!_ready) return;
        foreach (var (id, sb) in _pending)
        {
            string text;
            lock (sb)
            {
                if (sb.Length == 0) continue;
                text = sb.ToString();
                sb.Clear();
            }
            Send(new { t = "out", id, data = text });
        }
    }

    private void Send(object message)
    {
        var json = JsonSerializer.Serialize(message);
        if (!_ready)
        {
            _beforeReady.Add(json);
            return;
        }
        _web.PostWebMessageAsJson(json);
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;
        switch (root.GetProperty("t").GetString())
        {
            case "ready":
                // 페이지가 스스로 잰 값: 탐색 시작부터 스크립트가 다 돌 때까지.
                if (root.TryGetProperty("ms", out var ms))
                    AppLog.Info($"페이지 스크립트 완료 · 탐색 시작 후 {ms.GetInt32()}ms");
                _ready = true;
                foreach (var json in _beforeReady)
                    _web.PostWebMessageAsJson(json);
                _beforeReady.Clear();
                Ready?.Invoke();
                break;
            case "in":
                Input?.Invoke(root.GetProperty("id").GetString()!, root.GetProperty("data").GetString() ?? "");
                break;
            case "resize":
                Resized?.Invoke(root.GetProperty("id").GetString()!, root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
                break;
            case "focus":
                FocusRequested?.Invoke(root.GetProperty("slot").GetInt32());
                break;
            case "command":
                Command?.Invoke(root.GetProperty("name").GetString() ?? "");
                break;
            case "ratios":
                RatiosChanged?.Invoke(root.GetProperty("col").GetDouble(), root.GetProperty("row").GetDouble());
                break;
        }
    }

    public void Dispose()
    {
        _flushTimer.Stop();
        _web.WebMessageReceived -= OnMessage;
    }
}
