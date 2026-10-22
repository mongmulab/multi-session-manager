using MultiSessionManager.Agents;
using MultiSessionManager.Pty;
using MultiSessionManager.Sessions;

namespace MultiSessionManager.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msm-store-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "sessions.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_gives_empty_state()
    {
        Assert.Empty(new SessionStore(FilePath).Load().Sessions);
    }

    [Fact]
    public void Round_trip_keeps_korean_readable()
    {
        var store = new SessionStore(FilePath);
        var record = new SessionRecord { Title = "결제 리팩터", WorkingDirectory = @"D:\프로젝트\결제" };
        store.Save(new AppState { Sessions = [record], ActiveSessionId = record.Id });
        store.Save(new AppState { Sessions = [record], ActiveSessionId = record.Id }); // 덮어쓰기 경로도 지나간다

        var loaded = new SessionStore(FilePath).Load();
        var only = Assert.Single(loaded.Sessions);
        Assert.Equal("결제 리팩터", only.Title);
        Assert.Equal(record.AgentSessionId, only.AgentSessionId);
        Assert.Equal(record.Id, loaded.ActiveSessionId);
        Assert.Contains("결제 리팩터", File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Broken_file_is_kept_aside()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        var state = new SessionStore(FilePath).Load();
        Assert.Empty(state.Sessions);
        Assert.Single(Directory.GetFiles(_dir, "sessions.json.broken-*"));
    }

    [Fact]
    public void Manager_gives_unique_default_titles_from_folder_name()
    {
        using var manager = new SessionManager(new SessionStore(FilePath), new NullAdapter());
        var a = manager.Create(@"D:\work\api\");
        var b = manager.Create(@"D:\other\api");
        var c = manager.Create(@"D:\work\web", "  프런트 ");
        Assert.Equal("api", a.Title);
        Assert.Equal("api (2)", b.Title);
        Assert.Equal("프런트", c.Title);

        manager.Rename(a, "   ");
        Assert.Equal("api", a.Title);

        manager.Remove(b);
        Assert.Equal(2, new SessionStore(FilePath).Load().Sessions.Count);
    }

    private sealed class NullAdapter : IAgentAdapter
    {
        public string Kind => "null";
        public PtyStartInfo BuildStartInfo(SessionRecord session, int columns, int rows) => throw new NotSupportedException();
        public bool HasConversation(SessionRecord session) => false;
    }
}
