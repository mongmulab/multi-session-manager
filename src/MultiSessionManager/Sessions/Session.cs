using System.ComponentModel;
using System.Runtime.CompilerServices;
using MultiSessionManager.Agents;
using MultiSessionManager.Pty;

namespace MultiSessionManager.Sessions;

public enum SessionStatus
{
    /// <summary>앱이 막 떴거나 사용자가 멈춘 상태. 선택하면 이어서 시작한다.</summary>
    Idle,
    /// <summary>프로세스를 띄우는 중. 이 사이에 다시 시작을 누르거나 칸이 다시 보여도 두 번 띄우지 않는다.</summary>
    Starting,
    Running,
    Exited,
    Failed,
}

/// <summary>탭 하나. 저장할 정보(<see cref="Record"/>)와 실행 중인 프로세스를 함께 들고 있다.</summary>
public sealed class Session : INotifyPropertyChanged
{
    private PseudoConsoleProcess? _process;
    private SessionStatus _status = SessionStatus.Idle;
    private string _statusDetail = "";
    private AgentActivity _activity;
    private bool _needsAttention;

    public SessionRecord Record { get; }

    public Session(SessionRecord record) => Record = record;

    public string Id => Record.Id;

    public string Title
    {
        get => Record.Title;
        set
        {
            if (Record.Title == value) return;
            Record.Title = value;
            OnPropertyChanged();
        }
    }

    public string WorkingDirectory => Record.WorkingDirectory;

    public SessionStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            // 프로세스가 바뀌면 지난 알림은 의미가 없다.
            if (value != SessionStatus.Running)
            {
                _activity = AgentActivity.Unknown;
                NeedsAttention = false;
                OnPropertyChanged(nameof(Activity));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set
        {
            _statusDetail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>CLI 가 Hook 으로 알려 온 상태. 실행 중일 때만 뜻이 있다.</summary>
    public AgentActivity Activity
    {
        get => _activity;
        private set
        {
            if (_activity == value) return;
            _activity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>보고 있지 않을 때 턴이 끝났거나 답을 기다리게 됐다. 그 세션을 보면 지운다.</summary>
    public bool NeedsAttention
    {
        get => _needsAttention;
        set
        {
            if (_needsAttention == value) return;
            _needsAttention = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Hook 알림을 반영한다. 실행 중이 아니면(끝난 뒤 늦게 온 알림) 버린다.</summary>
    public void ReportActivity(AgentActivity activity)
    {
        if (Status != SessionStatus.Running) return;
        Activity = activity;
    }

    public string StatusText => Status switch
    {
        SessionStatus.Idle => "대기",
        SessionStatus.Starting => "시작 중",
        SessionStatus.Running => Activity switch
        {
            AgentActivity.Working => "작업 중",
            AgentActivity.Waiting => "입력 대기",
            AgentActivity.NeedsInput => "확인 필요",
            AgentActivity.Failed => "오류",
            _ => "실행 중",
        },
        SessionStatus.Exited => $"종료됨 {StatusDetail}".TrimEnd(),
        SessionStatus.Failed => $"시작 실패 {StatusDetail}".TrimEnd(),
        _ => "",
    };

    public bool IsRunning => Status == SessionStatus.Running;
    public bool IsBusy => Status is SessionStatus.Running or SessionStatus.Starting;

    /// <summary>출력 조각. 읽기 스레드에서 불린다.</summary>
    public event Action<Session, string>? Output;

    /// <summary>프로세스가 끝났을 때. 스레드풀에서 불린다.</summary>
    public event Action<Session, int>? Exited;

    /// <summary>
    /// 기록 조회와 CreateProcess 는 스레드풀에서 돈다(세션 하나에 수백 ms 걸려 UI 를 막았다).
    /// UI 스레드에서 불러야 하고, 끝나면 UI 스레드로 돌아와 상태를 바꾼다.
    /// </summary>
    /// <returns>이전 대화를 이어 갔는지. 이미 떠 있거나 띄우는 중이면 null.</returns>
    public async Task<bool?> StartAsync(IAgentAdapter adapter, ProcessJob job, int columns, int rows)
    {
        if (IsBusy) return null;
        Status = SessionStatus.Starting;
        StatusDetail = "";
        PseudoConsoleProcess? started = null;
        try
        {
            var (process, resumes) = await Task.Run(() =>
            {
                var launch = adapter.Prepare(Record, columns, rows);
                // 콜백은 띄우는 순간에 붙인다. 첫 출력을 놓치지 않기 위해서다.
                var p = PseudoConsoleProcess.Start(launch.StartInfo, job,
                    text => Output?.Invoke(this, text),
                    code => OnExited(started, code));
                return (p, launch.Resumes);
            });
            int? exitedEarly;
            lock (_gate)
            {
                started = process;
                _process = process;
                exitedEarly = _pendingExit;
            }
            Record.LastStartedAt = DateTimeOffset.Now;
            Status = SessionStatus.Running;
            // 띄우는 사이에 이미 끝났으면(잘못된 폴더 등) 종료 코드만 남아 있다. 여기서 마무리한다.
            if (exitedEarly is { } code)
                OnExited(process, code);
            return resumes;
        }
        catch (Exception ex)
        {
            StatusDetail = ex.Message;
            Status = SessionStatus.Failed;
            throw;
        }
    }

    private readonly object _gate = new();
    private int? _pendingExit;

    private void OnExited(PseudoConsoleProcess? process, int code)
    {
        lock (_gate)
        {
            // StartAsync 가 아직 _process 를 채우기 전에 끝난 경우: 값만 남겨 두고 StartAsync 가 마무리한다.
            if (process is null || _process is null && Status == SessionStatus.Starting)
            {
                _pendingExit = code;
                return;
            }
            if (!ReferenceEquals(_process, process)) return;
            _pendingExit = null;
            _process = null;
        }
        StatusDetail = $"({code})";
        Status = SessionStatus.Exited;
        Exited?.Invoke(this, code);
        ThreadPool.QueueUserWorkItem(_ => process.Dispose());
    }

    public void Write(string text)
    {
        _process?.Write(text);
        if (Status == SessionStatus.Running)
            Activity = AfterInput(Activity, text);
    }

    /// <summary>
    /// 사용자가 턴을 끊으면(Esc · Ctrl+C) Claude 는 Stop Hook 을 보내지 않는다. 권한 확인을 Esc 로 거절할 때도 같다.
    /// 그대로 두면 "작업 중" · "확인 필요" 에 머물러서, 키 입력으로 보탠다.
    /// </summary>
    internal static AgentActivity AfterInput(AgentActivity current, string input) => (current, input) switch
    {
        (AgentActivity.Working or AgentActivity.NeedsInput, "\u001b" or "\u0003") => AgentActivity.Waiting,
        // 확인 화면에 다른 답을 했다. 허락이면 곧 PostToolUse 가, 아니면 Stop 이 온다. 그때까지는 모른다.
        (AgentActivity.NeedsInput, _) => AgentActivity.Unknown,
        _ => current,
    };

    public void Resize(int columns, int rows) => _process?.Resize(columns, rows);

    /// <summary>프로세스를 끝낸다. 대화 기록은 CLI 쪽에 남아 있어 다시 시작하면 이어진다.</summary>
    public void Stop()
    {
        PseudoConsoleProcess? process;
        lock (_gate)
        {
            process = _process;
            if (process is null) return;
            _process = null;
            _pendingExit = null;
        }
        process.Dispose();
        StatusDetail = "";
        Status = SessionStatus.Idle;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
