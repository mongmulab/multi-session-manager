using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MultiSessionManager.Agents;
using MultiSessionManager.Sessions;
using MultiSessionManager.Terminal;

namespace MultiSessionManager;

public partial class MainWindow : Window
{
    private readonly SessionManager? _manager;
    private readonly string? _startupError;
    private TerminalBridge? _bridge;

    // 세션마다 xterm 이 마지막으로 알려 준 크기. 다시 시작할 때 그 크기로 띄운다.
    private readonly Dictionary<string, (int Cols, int Rows)> _sizes = [];

    // 이번 실행에서 자동으로 한 번 띄운 세션. 사용자가 멈추거나 종료된 세션은 다시 고를 때 저절로 살리지 않는다.
    private readonly HashSet<string> _autoStarted = [];

    public MainWindow()
    {
        InitializeComponent();

        var adapter = ClaudeAdapter.TryCreate();
        if (adapter is null)
        {
            _startupError = "PATH 에서 claude 를 찾지 못했습니다. Claude Code 를 설치한 뒤 다시 실행하세요.";
        }
        else
        {
            _manager = new SessionManager(new SessionStore(SessionStore.DefaultPath()), adapter);
            _manager.Load();
            foreach (var session in _manager.Sessions)
                Attach(session);
            SessionList.ItemsSource = _manager.Sessions;
        }

        Loaded += OnLoaded;
        Closing += OnClosing;
        UpdateButtons();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_manager is null)
        {
            SetStatus(_startupError!);
            NewButton.IsEnabled = false;
            return;
        }

        try
        {
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiSessionManager", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            SetStatus("WebView2 런타임을 띄우지 못했습니다: " + ex.Message);
            return;
        }

        var webRoot = Path.Combine(AppContext.BaseDirectory, "Web");
        _bridge = new TerminalBridge(Web.CoreWebView2, webRoot);
        _bridge.Ready += OnBridgeReady;
        _bridge.Input += (id, data) => Find(id)?.Write(data);
        _bridge.Resized += OnTerminalResized;
        _bridge.Navigate();
    }

    private void OnBridgeReady()
    {
        foreach (var session in _manager!.Sessions)
            _bridge!.Create(session.Id);

        var active = Find(_manager.ActiveSessionId) ?? _manager.Sessions.FirstOrDefault();
        if (active is not null)
            SessionList.SelectedItem = active;
        else
            _bridge!.Show(null);
        SetStatus($"세션 {_manager.Sessions.Count}개 · 저장 위치 {SessionStore.DefaultPath()}");
    }

    private void Attach(Session session)
    {
        session.Output += (s, text) => _bridge?.QueueOutput(s.Id, text);
        session.Exited += (s, code) => Dispatcher.BeginInvoke(() =>
        {
            _bridge?.ShowExit(s.Id, $"프로세스 종료 · 코드 {code} · '다시 시작'을 누르면 같은 대화로 이어집니다");
            UpdateButtons();
        });
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Session.Status))
                Dispatcher.BeginInvoke(UpdateButtons);
        };
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

        // 처음 보이는 세션은 xterm 이 크기를 잰 뒤에 띄운다. 그래야 Claude 가 첫 화면을 맞는 폭으로 그린다.
        if (session.Status == SessionStatus.Idle && _autoStarted.Add(id) && ReferenceEquals(SessionList.SelectedItem, session))
            StartSession(session);
    }

    private void StartSession(Session session)
    {
        var (cols, rows) = _sizes.TryGetValue(session.Id, out var size) ? size : (120, 30);
        var resuming = _manager!.HasConversation(session);
        try
        {
            _manager.Start(session, cols, rows);
            SetStatus($"{session.Title}: {(resuming ? "이전 대화를 이어서" : "새 대화로")} 시작했습니다 · {session.WorkingDirectory}");
        }
        catch (Exception ex)
        {
            _bridge?.ShowExit(session.Id, "시작 실패: " + ex.Message);
            SetStatus($"{session.Title}: 시작하지 못했습니다 — {ex.Message}");
        }
        UpdateButtons();
    }

    private void OnNewSession(object? sender, RoutedEventArgs? e)
    {
        if (_manager is null || _bridge is null) return;
        var dialog = new OpenFolderDialog { Title = "세션을 열 폴더" };
        if (Selected is { } current && Directory.Exists(current.WorkingDirectory))
            dialog.InitialDirectory = Path.GetDirectoryName(current.WorkingDirectory) ?? current.WorkingDirectory;
        if (dialog.ShowDialog(this) != true) return;

        var session = _manager.Create(dialog.FolderName);
        Attach(session);
        _bridge.Create(session.Id);
        SessionList.SelectedItem = session;
    }

    private void OnRename(object? sender, RoutedEventArgs? e)
    {
        if (Selected is not { } session) return;
        var dialog = new RenameDialog(session.Title) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _manager!.Rename(session, dialog.Value);
        if (session.IsRunning)
            SetStatus("탭 이름을 바꿨습니다. Claude 쪽 세션 이름은 다음에 시작할 때 반영됩니다.");
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } session || session.IsRunning) return;
        _autoStarted.Add(session.Id);
        StartSession(session);
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } session || !session.IsRunning) return;
        session.Stop();
        _bridge?.ShowExit(session.Id, "중지했습니다 · '다시 시작'을 누르면 같은 대화로 이어집니다");
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
        _bridge?.Remove(session.Id);
        _sizes.Remove(session.Id);
        SessionList.SelectedItem = Find(_manager.ActiveSessionId);
        if (SessionList.SelectedItem is null)
            _bridge?.Show(null);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        if (Selected is not { } session || _bridge is null) return;
        _manager!.ActiveSessionId = session.Id;
        _bridge.Show(session.Id);
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
        if (e.Key == Key.T && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OnNewSession(null, null);
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_manager is null) return;
        _manager.Save();
        _bridge?.Dispose();
        _manager.Dispose();
    }

    private Session? Selected => SessionList.SelectedItem as Session;

    private Session? Find(string? id) => id is null ? null : _manager?.Sessions.FirstOrDefault(s => s.Id == id);

    private void UpdateButtons()
    {
        var session = Selected;
        RenameButton.IsEnabled = session is not null;
        CloseButton.IsEnabled = session is not null;
        StopButton.IsEnabled = session?.IsRunning == true;
        RestartButton.IsEnabled = session is not null && !session.IsRunning;
    }

    private void SetStatus(string text) => StatusBar.Text = text;
}
