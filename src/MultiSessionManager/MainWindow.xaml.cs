using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MultiSessionManager.Agents;
using MultiSessionManager.Sessions;
using MultiSessionManager.Terminal;

namespace MultiSessionManager;

public partial class MainWindow : Window
{
    private static readonly Brush ActiveLayoutBrush = new SolidColorBrush(Color.FromRgb(0x09, 0x47, 0x71));
    private static readonly Brush NormalButtonBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x41));

    private readonly SessionManager? _manager;
    private readonly string? _startupError;
    private readonly TerminalBridge _bridge = new();
    private readonly Task<CoreWebView2Environment>? _webEnvironment;
    private readonly HookServer? _hooks;

    // 세션마다 xterm 이 마지막으로 알려 준 크기. 다시 시작할 때 그 크기로 띄운다.
    private readonly Dictionary<string, (int Cols, int Rows)> _sizes = [];

    // 이번 실행에서 자동으로 한 번 띄운 세션. 사용자가 멈추거나 종료된 세션은 다시 보일 때 저절로 살리지 않는다.
    private readonly HashSet<string> _autoStarted = [];

    // 칸 포커스를 따라 목록 선택을 바꿀 때, 그 선택 변경이 다시 칸 배치를 건드리지 않게 막는다.
    private bool _syncingSelection;

    public MainWindow()
    {
        AppLog.Info("창 생성 시작");
        InitializeComponent();
        AppLog.Info($"창 구성 끝 · 목록 {SessionStore.DefaultPath()}");

        var adapter = ClaudeAdapter.TryCreate();
        if (adapter is null)
        {
            _startupError = "PATH 에서 claude 를 찾지 못했습니다. Claude Code 를 설치한 뒤 다시 실행하세요.";
        }
        else
        {
            // 브라우저 환경은 창을 그리는 동안 따로 만든다. 창이 뜨기를 기다릴 이유가 없다.
            _webEnvironment = CreateWebEnvironmentAsync();

            // 세션 상태(작업 중·입력 대기·확인 필요)는 Claude 의 Hook 으로 받는다. 받지 못해도 세션은 그대로 돈다.
            try
            {
                _hooks = new HookServer();
                _hooks.Received += (id, activity) => Dispatcher.BeginInvoke(() => OnHook(id, activity));
                adapter.Hooks = _hooks;
                AppLog.Info($"Hook 수신 127.0.0.1:{_hooks.Port}");
            }
            catch (Exception ex)
            {
                AppLog.Error("Hook 수신기를 열지 못했습니다. 상태 표시 없이 진행합니다", ex);
            }

            _manager = new SessionManager(new SessionStore(SessionStore.DefaultPath()), adapter);
            _manager.Load();
            WireBridge();
            foreach (var session in _manager.Sessions)
            {
                Attach(session);
                _bridge.Create(session.Id);
                PushTitle(session);
            }
            SessionList.ItemsSource = _manager.Sessions;
            PushLayout();
        }

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Activated += (_, _) => ClearAttention();
        UpdateButtons();
    }

    private static Task<CoreWebView2Environment> CreateWebEnvironmentAsync()
    {
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiSessionManager", "WebView2");
        return CoreWebView2Environment.CreateAsync(null, userData);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (_manager?.Window is { } bounds)
            WindowPlacement.Apply(new WindowInteropHelper(this).Handle, bounds);
    }

    private PaneLayout Layout => _manager!.Layout;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppLog.Info("창 표시");
        if (_manager is null)
        {
            SetStatus(_startupError!);
            NewButton.IsEnabled = false;
            return;
        }

        try
        {
            var env = await _webEnvironment!;
            AppLog.Info("WebView2 환경 생성");
            await Web.EnsureCoreWebView2Async(env);
            AppLog.Info("WebView2 준비");
        }
        catch (Exception ex)
        {
            AppLog.Error("WebView2 실패", ex);
            SetStatus("WebView2 런타임을 띄우지 못했습니다: " + ex.Message);
            return;
        }

        _bridge.Connect(Web.CoreWebView2, Path.Combine(AppContext.BaseDirectory, "Web"));
    }

    private void WireBridge()
    {
        _bridge.Ready += OnBridgeReady;
        _bridge.Input += (id, data) => Find(id)?.Write(data);
        _bridge.Resized += OnTerminalResized;
        _bridge.FocusRequested += OnPaneFocused;
        _bridge.Command += name =>
        {
            if (name == "new") OnNewSession(null, null);
            else if (name.StartsWith("layout") && int.TryParse(name[6..], out var mode)) SetLayoutMode(mode);
        };
        _bridge.RatiosChanged += (col, row) =>
        {
            Layout.SetRatios(col, row);
            _manager!.Save();
        };
    }

    private void OnBridgeReady()
    {
        AppLog.Info("터미널 페이지 준비");
        SetStatus($"세션 {_manager!.Sessions.Count}개 · 저장 위치 {SessionStore.DefaultPath()}");
    }

    private void Attach(Session session)
    {
        session.Output += (s, text) => _bridge.QueueOutput(s.Id, text);
        session.Exited += (s, code) => Dispatcher.BeginInvoke(() =>
        {
            _bridge.ShowExit(s.Id, $"프로세스 종료 · 코드 {code} · '다시 시작'을 누르면 같은 대화로 이어집니다");
            UpdateButtons();
        });
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Session.Status) or nameof(Session.StatusText) or nameof(Session.Title))
                Dispatcher.BeginInvoke(() =>
                {
                    PushTitle(session);
                    UpdateButtons();
                });
        };
    }

    private void OnHook(string id, AgentActivity activity)
    {
        if (Find(id) is not { } session) return;
        session.ReportActivity(activity);
        // 지금 보고 있는 세션이 아니면 표시를 남긴다. 보는 순간 지운다.
        if (activity is AgentActivity.Waiting or AgentActivity.NeedsInput or AgentActivity.Failed && !IsWatching(session))
            session.NeedsAttention = true;
        UpdateFocusStatus();
    }

    private bool IsWatching(Session session) => IsActive && Layout.FocusedSessionId == session.Id;

    private void ClearAttention()
    {
        if (IsActive && Find(Layout.FocusedSessionId) is { } session)
            session.NeedsAttention = false;
    }

    private void PushTitle(Session session) => _bridge.SetTitle(session.Id, session.Title, session.StatusText);

    /// <summary>칸 배치를 화면에 보내고, 목록 선택을 포커스 칸의 세션에 맞춘다.</summary>
    private void PushLayout()
    {
        _bridge.ApplyLayout(Layout);

        _syncingSelection = true;
        SessionList.SelectedItem = Find(Layout.FocusedSessionId);
        _syncingSelection = false;

        foreach (var (button, mode) in new[] { (Layout1Button, 1), (Layout2Button, 2), (Layout4Button, 4) })
            button.Background = Layout.Mode == mode ? ActiveLayoutBrush : NormalButtonBrush;
        UpdateButtons();
        UpdateFocusStatus();
        ClearAttention();
        _manager!.Save();
    }

    private void OnTerminalResized(string id, int cols, int rows)
    {
        var session = Find(id);
        if (session is null) return;
        _sizes[id] = (cols, rows);

        if (session.IsRunning)
        {
            session.Resize(cols, rows);
            return;
        }
        if (session.Status == SessionStatus.Starting)
            return; // 다 뜨면 StartSession 이 마지막 크기로 맞춘다.

        // 칸에 처음 보이는 세션은 xterm 이 크기를 잰 뒤에 띄운다. 그래야 Claude 가 첫 화면을 맞는 폭으로 그린다.
        // 화면보다 먼저 띄우면 안 된다. Claude 가 시작할 때 터미널에 묻는 말에 답할 xterm 이 아직 없어서,
        // Claude 가 전체 화면 렌더러를 실패로 치고 이 PC 의 모든 세션에서 꺼 버린다(~/.claude.json fullscreenAutoDisabled).
        if (session.Status == SessionStatus.Idle && Layout.IsVisible(id) && _autoStarted.Add(id))
            StartSession(session);
    }

    private async void StartSession(Session session)
    {
        var (cols, rows) = _sizes.TryGetValue(session.Id, out var size) ? size : (120, 30);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var resumed = await _manager!.StartAsync(session, cols, rows);
            if (resumed is null) return;
            AppLog.Info($"세션 시작 {session.Id} {(resumed.Value ? "resume" : "new")} {cols}x{rows} · {watch.ElapsedMilliseconds}ms");

            // 띄우는 사이에 목록에서 닫혔으면 바로 끝낸다.
            if (!_manager.Sessions.Contains(session))
            {
                session.Stop();
                return;
            }
            // 띄우는 사이에 칸 크기가 바뀌었으면 마지막 크기로 맞춘다.
            if (_sizes.TryGetValue(session.Id, out var latest) && latest != (cols, rows))
                session.Resize(latest.Cols, latest.Rows);

            if (Layout.FocusedSessionId == session.Id)
                SetStatus($"{session.Title}: {(resumed.Value ? "이전 대화를 이어서" : "새 대화로")} 시작했습니다 · {session.WorkingDirectory}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"세션 시작 실패 {session.Id}", ex);
            _bridge.ShowExit(session.Id, "시작 실패: " + ex.Message);
            SetStatus($"{session.Title}: 시작하지 못했습니다 — {ex.Message}");
        }
        UpdateButtons();
    }

    private void OnNewSession(object? sender, RoutedEventArgs? e)
    {
        if (_manager is null) return;
        var dialog = new OpenFolderDialog { Title = "세션을 열 폴더" };
        if (Selected is { } current && Directory.Exists(current.WorkingDirectory))
            dialog.InitialDirectory = Path.GetDirectoryName(current.WorkingDirectory) ?? current.WorkingDirectory;
        if (dialog.ShowDialog(this) != true) return;

        var session = _manager.Create(dialog.FolderName);
        Attach(session);
        _bridge.Create(session.Id);
        PushTitle(session);
        Layout.Show(session.Id);
        PushLayout();
    }

    private void OnRename(object? sender, RoutedEventArgs? e)
    {
        if (Selected is not { } session) return;
        var dialog = new RenameDialog(session.Title) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _manager!.Rename(session, dialog.Value);
        if (session.IsRunning)
            SetStatus("탭 이름을 바꿨습니다. Claude 입력칸 쪽 이름은 다음에 시작할 때 반영됩니다.");
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } session || session.IsBusy) return;
        _autoStarted.Add(session.Id);
        StartSession(session);
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } session || !session.IsRunning) return;
        session.Stop();
        _bridge.ShowExit(session.Id, "중지했습니다 · '다시 시작'을 누르면 같은 대화로 이어집니다");
        UpdateButtons();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } session) return;
        if (session.IsRunning)
        {
            var answer = MessageBox.Show(this, $"'{session.Title}' 은(는) 실행 중입니다. 프로세스를 끝내고 목록에서 지울까요?\n대화 기록은 Claude 쪽에 남습니다.",
                "세션 닫기", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }
        _manager!.Remove(session);
        _bridge.Remove(session.Id);
        _sizes.Remove(session.Id);
        PushLayout();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        if (_syncingSelection || Selected is not { } session) return;
        Layout.Show(session.Id);
        PushLayout();
    }

    private void OnPaneFocused(int slot)
    {
        Layout.FocusSlot(slot);
        PushLayout();
    }

    private void OnLayoutButton(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var mode))
            SetLayoutMode(mode);
    }

    private void SetLayoutMode(int mode)
    {
        if (_manager is null || Layout.Mode == mode) return;
        Layout.SetMode(mode, _manager.SessionOrder);
        PushLayout();
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => OnRename(null, null);

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            OnRename(null, null);
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.T && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OnNewSession(null, null);
            e.Handled = true;
            return;
        }
        if (mods == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mode = key switch { Key.D1 or Key.NumPad1 => 1, Key.D2 or Key.NumPad2 => 2, Key.D4 or Key.NumPad4 => 4, _ => 0 };
            if (mode != 0)
            {
                SetLayoutMode(mode);
                e.Handled = true;
            }
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_manager is null) return;
        _manager.Window = WindowPlacement.Capture(new WindowInteropHelper(this).Handle) ?? _manager.Window;
        _manager.Save();
        // 세션 정리(Claude 의 종료 훅까지)는 몇 초 걸릴 수 있다. 창은 먼저 내려 바로 닫힌 것처럼 보이게 한다.
        Hide();
        _bridge.Dispose();
        _manager.Dispose();
        _hooks?.Dispose();
    }

    private Session? Selected => SessionList.SelectedItem as Session;

    private Session? Find(string? id) => id is null ? null : _manager?.Sessions.FirstOrDefault(s => s.Id == id);

    private void UpdateButtons()
    {
        var session = Selected;
        RenameButton.IsEnabled = session is not null;
        CloseButton.IsEnabled = session is not null;
        StopButton.IsEnabled = session?.IsRunning == true;
        RestartButton.IsEnabled = session is not null && !session.IsBusy;
    }

    // 상태줄은 포커스 칸의 세션을 보여 준다. 마지막으로 시작한 세션이 아니라 지금 보고 있는 세션이 기준이다.
    private void UpdateFocusStatus()
    {
        if (Find(Layout.FocusedSessionId) is { } session)
            SetStatus($"{session.Title} · {session.StatusText} · {session.WorkingDirectory}");
    }

    private void SetStatus(string text) => StatusBar.Text = text;
}
