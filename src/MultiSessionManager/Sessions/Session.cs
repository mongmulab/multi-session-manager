using System.ComponentModel;
using System.Runtime.CompilerServices;
using MultiSessionManager.Agents;
using MultiSessionManager.Pty;

namespace MultiSessionManager.Sessions;

public enum SessionStatus
{
    /// <summary>앱이 막 떴거나 사용자가 멈춘 상태. 선택하면 이어서 시작한다.</summary>
    Idle,
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

    public string StatusText => Status switch
    {
        SessionStatus.Idle => "대기",
        SessionStatus.Running => "실행 중",
        SessionStatus.Exited => $"종료됨 {StatusDetail}".TrimEnd(),
        SessionStatus.Failed => $"시작 실패 {StatusDetail}".TrimEnd(),
        _ => "",
    };

    public bool IsRunning => Status == SessionStatus.Running;

    /// <summary>출력 조각. 읽기 스레드에서 불린다.</summary>
    public event Action<Session, string>? Output;

    /// <summary>프로세스가 끝났을 때. 스레드풀에서 불린다.</summary>
    public event Action<Session, int>? Exited;

    public void Start(IAgentAdapter adapter, ProcessJob job, int columns, int rows)
    {
        if (_process is not null) return;
        try
        {
            var info = adapter.BuildStartInfo(Record, columns, rows);
            var process = PseudoConsoleProcess.Start(info, job);
            process.Output += text => Output?.Invoke(this, text);
            process.Exited += code => OnExited(process, code);
            _process = process;
            Record.LastStartedAt = DateTimeOffset.Now;
            StatusDetail = "";
            Status = SessionStatus.Running;
        }
        catch (Exception ex)
        {
            StatusDetail = ex.Message;
            Status = SessionStatus.Failed;
            throw;
        }
    }

    private void OnExited(PseudoConsoleProcess process, int code)
    {
        if (!ReferenceEquals(_process, process)) return;
        _process = null;
        StatusDetail = $"({code})";
        Status = SessionStatus.Exited;
        Exited?.Invoke(this, code);
        ThreadPool.QueueUserWorkItem(_ => process.Dispose());
    }

    public void Write(string text) => _process?.Write(text);

    public void Resize(int columns, int rows) => _process?.Resize(columns, rows);

    /// <summary>프로세스를 끝낸다. 대화 기록은 CLI 쪽에 남아 있어 다시 시작하면 이어진다.</summary>
    public void Stop()
    {
        var process = _process;
        if (process is null) return;
        _process = null;
        process.Dispose();
        StatusDetail = "";
        Status = SessionStatus.Idle;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
