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

    [Fact]
    public void Locate_prefers_environment_then_chosen_then_default()
    {
        var pointer = Path.Combine(_dir, "location.json");
        var fallback = Path.Combine(_dir, "default", "sessions.json");
        var chosen = Path.Combine(_dir, "chosen", "sessions.json");

        Assert.Equal((fallback, StoreSource.Default), SessionStore.Locate(null, pointer, fallback));

        Directory.CreateDirectory(Path.GetDirectoryName(chosen)!);
        SessionStore.Remember(pointer, fallback, chosen);
        Assert.Equal((chosen, StoreSource.Chosen), SessionStore.Locate(null, pointer, fallback));
        Assert.Equal((FilePath, StoreSource.Environment), SessionStore.Locate(FilePath, pointer, fallback));

        // 기본 위치를 고르면 기록을 지운다
        SessionStore.Remember(pointer, fallback, fallback);
        Assert.False(File.Exists(pointer));
    }

    [Fact]
    public void Chosen_folder_that_is_gone_opens_default_but_keeps_the_choice()
    {
        var pointer = Path.Combine(_dir, "location.json");
        var fallback = Path.Combine(_dir, "sessions.json");
        var gone = Path.Combine(_dir, "unplugged", "sessions.json");
        SessionStore.Remember(pointer, fallback, gone);

        Assert.Equal((fallback, StoreSource.ChosenMissing), SessionStore.Locate(null, pointer, fallback));
        Assert.Equal(gone, SessionStore.ReadPointer(pointer));
    }

    [Fact]
    public void Broken_pointer_opens_default()
    {
        Directory.CreateDirectory(_dir);
        var pointer = Path.Combine(_dir, "location.json");
        File.WriteAllText(pointer, "{ nope");
        Assert.Equal((FilePath, StoreSource.Default), SessionStore.Locate(null, pointer, FilePath));
    }

    [Fact]
    public void MoveTo_writes_the_new_file_backs_up_what_was_there_and_removes_the_old()
    {
        var target = Path.Combine(_dir, "moved", "sessions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "{}");
        using var manager = new SessionManager(new SessionStore(FilePath), new NullAdapter());
        manager.Create(@"D:\work\api");

        var committed = false;
        manager.MoveTo(target, () => committed = true);

        Assert.True(committed);
        Assert.False(File.Exists(FilePath));
        Assert.Single(new SessionStore(target).Load().Sessions);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!, "sessions.json.bak-*"));

        // 옮긴 뒤의 저장은 새 위치로 간다
        manager.Create(@"D:\work\web");
        Assert.Equal(2, new SessionStore(target).Load().Sessions.Count);
    }

    [Fact]
    public void MoveTo_keeps_the_old_place_when_the_commit_fails()
    {
        var target = Path.Combine(_dir, "moved", "sessions.json");
        using var manager = new SessionManager(new SessionStore(FilePath), new NullAdapter());
        manager.Create(@"D:\work\api");

        Assert.Throws<IOException>(() => manager.MoveTo(target, () => throw new IOException("기록 실패")));

        Assert.Equal(FilePath, manager.StoreFile);
        Assert.Single(new SessionStore(FilePath).Load().Sessions);
    }

    private sealed class NullAdapter : IAgentAdapter
    {
        public string Kind => "null";
        public AgentLaunch Prepare(SessionRecord session, int columns, int rows) => throw new NotSupportedException();
        public bool HasConversation(SessionRecord session) => false;
    }
}
