using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static MultiSessionManager.Pty.NativeMethods;

namespace MultiSessionManager.Pty;

/// <summary>
/// ConPTY 하나와 그 위에서 도는 자식 프로세스 하나.
/// 출력은 별도 스레드에서 읽어 <see cref="Output"/> 으로 내보내고,
/// 프로세스가 끝나면 남은 출력을 다 흘려보낸 뒤 <see cref="Exited"/> 를 한 번 올린다.
/// </summary>
public sealed class PseudoConsoleProcess : IDisposable
{
    private IntPtr _pseudoConsole;
    private readonly IntPtr _processHandle;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Thread _reader;
    private RegisteredWaitHandle? _exitWait;
    private readonly ManualResetEvent _processEvent;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _exitDone = new(false);
    private int _exitRaised;
    private bool _disposed;

    public int ProcessId { get; }
    public int? ExitCode { get; private set; }

    /// <summary>UTF-8 로 풀어 낸 출력 조각. 읽기 스레드에서 불린다.</summary>
    public event Action<string>? Output;

    /// <summary>종료 코드. 남은 출력을 다 보낸 다음 한 번만 불린다.</summary>
    public event Action<int>? Exited;

    private PseudoConsoleProcess(IntPtr pseudoConsole, PROCESS_INFORMATION pi, SafeFileHandle input, SafeFileHandle output)
    {
        _pseudoConsole = pseudoConsole;
        _processHandle = pi.hProcess;
        ProcessId = pi.dwProcessId;
        _input = new FileStream(input, FileAccess.Write, 1);
        _output = new FileStream(output, FileAccess.Read, 1);

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"pty-read-{ProcessId}" };
        _processEvent = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(_processHandle, ownsHandle: false) };
    }

    /// <summary>
    /// 프로세스를 띄운다. <paramref name="output"/>·<paramref name="exited"/> 는 읽기와 종료 감시를 시작하기 전에 붙인다.
    /// 그래야 첫 출력이나 아주 빨리 끝나는 프로세스의 종료를 놓치지 않는다.
    /// </summary>
    public static PseudoConsoleProcess Start(PtyStartInfo info, ProcessJob? job = null, Action<string>? output = null, Action<int>? exited = null)
    {
        if (!CreatePipe(out var ptyInputRead, out var inputWrite, IntPtr.Zero, 0))
            throw Fail("CreatePipe(input) 실패");
        if (!CreatePipe(out var outputRead, out var ptyOutputWrite, IntPtr.Zero, 0))
            throw Fail("CreatePipe(output) 실패");

        var size = new COORD { X = (short)Math.Max(info.Columns, 2), Y = (short)Math.Max(info.Rows, 2) };
        var hr = CreatePseudoConsole(size, ptyInputRead, ptyOutputWrite, 0, out var hpc);
        if (hr != 0)
            throw new Win32Exception(hr, $"CreatePseudoConsole 실패 ({new Win32Exception(hr).Message})");

        IntPtr attrList = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;
        try
        {
            var attrSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
            attrList = Marshal.AllocHGlobal(attrSize);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize))
                throw Fail("InitializeProcThreadAttributeList 실패");
            if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw Fail("UpdateProcThreadAttribute 실패");

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            // 부모의 표준 입출력이 파이프로 돌려져 있으면(테스트 러너·다른 터미널에서 띄웠을 때)
            // 자식이 ConPTY 대신 그쪽을 물려받는다. 빈 핸들로 막아 둔다.
            si.StartupInfo.dwFlags = 0x00000100; // STARTF_USESTDHANDLES
            si.lpAttributeList = attrList;

            envBlock = Marshal.StringToHGlobalUni(EnvironmentBlock.Build(info.Environment));
            var cmd = new StringBuilder(CommandLine.Build(info.ApplicationPath, info.Arguments));

            var flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED;
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, envBlock, info.WorkingDirectory, ref si, out var pi))
                throw Fail($"실행하지 못했습니다: {info.ApplicationPath} · 폴더 {info.WorkingDirectory}");

            try
            {
                // 일시정지 상태에서 Job 에 넣어야 손자 프로세스까지 빠짐없이 묶인다.
                job?.Assign(pi.hProcess);
            }
            catch
            {
                TerminateProcess(pi.hProcess, 1);
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                throw;
            }
            ResumeThread(pi.hThread);
            CloseHandle(pi.hThread);

            // 자식 쪽 파이프 끝은 ConPTY 가 복제해 들고 있으므로 우리 사본은 닫는다.
            ptyInputRead.Dispose();
            ptyOutputWrite.Dispose();

            var process = new PseudoConsoleProcess(hpc, pi, inputWrite, outputRead);
            hpc = IntPtr.Zero;
            if (output is not null) process.Output += output;
            if (exited is not null) process.Exited += exited;
            process._reader.Start();
            process._exitWait = ThreadPool.RegisterWaitForSingleObject(process._processEvent, (_, _) => process.OnProcessExited(), null, Timeout.Infinite, executeOnlyOnce: true);
            return process;
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (envBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(envBlock);
            if (hpc != IntPtr.Zero)
            {
                ClosePseudoConsole(hpc);
                ptyInputRead.Dispose();
                ptyOutputWrite.Dispose();
                inputWrite.Dispose();
                outputRead.Dispose();
            }
        }
    }

    // 우리가 붙인 설명 뒤에 Windows 가 알려 준 사유를 그대로 붙인다(예: "디렉터리 이름이 올바르지 않습니다").
    private static Win32Exception Fail(string what)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{what} — {new Win32Exception(error).Message}");
    }

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_gate)
        {
            if (_disposed || ExitCode is not null) return;
            try
            {
                _input.Write(bytes, 0, bytes.Length);
                _input.Flush();
            }
            catch (IOException)
            {
                // 프로세스가 막 끝나 파이프가 닫힌 경우. 종료 처리는 Exited 쪽에서 한다.
            }
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            if (_pseudoConsole == IntPtr.Zero || columns < 2 || rows < 2) return;
            ResizePseudoConsole(_pseudoConsole, new COORD { X = (short)columns, Y = (short)rows });
        }
    }

    public void Kill()
    {
        lock (_gate)
        {
            if (_disposed || ExitCode is not null) return;
            TerminateProcess(_processHandle, 1);
        }
    }

    private void ReadLoop()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        try
        {
            int n;
            while ((n = _output.Read(buffer, 0, buffer.Length)) > 0)
            {
                // 디코더가 상태를 들고 있어서, 한글 3바이트가 두 번에 나뉘어 와도 깨지지 않는다.
                var count = decoder.GetChars(buffer, 0, n, chars, 0, flush: false);
                if (count > 0)
                    Output?.Invoke(new string(chars, 0, count));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void OnProcessExited()
    {
        GetExitCodeProcess(_processHandle, out var code);
        ExitCode = unchecked((int)code);

        // ConPTY 는 닫혀야 출력 파이프를 끝낸다. ClosePseudoConsole 은 남은 출력이 다 읽힐 때까지
        // 기다릴 수 있으므로 읽기 스레드가 살아 있는 동안 별도 스레드에서 부른다.
        ClosePseudoConsoleOnce();
        _reader.Join(TimeSpan.FromSeconds(3));

        try
        {
            if (Interlocked.Exchange(ref _exitRaised, 1) == 0)
                Exited?.Invoke(ExitCode.Value);
        }
        finally
        {
            _exitDone.Set();
        }
    }

    private void ClosePseudoConsoleOnce()
    {
        IntPtr hpc;
        lock (_gate)
        {
            hpc = _pseudoConsole;
            _pseudoConsole = IntPtr.Zero;
        }
        if (hpc != IntPtr.Zero)
        {
            var closer = new Thread(() => ClosePseudoConsole(hpc)) { IsBackground = true, Name = "pty-close" };
            closer.Start();
            closer.Join(TimeSpan.FromSeconds(3));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (ExitCode is null)
                TerminateProcess(_processHandle, 1);
        }
        // 종료 처리(출력 비우기·Exited)가 끝나야 프로세스 핸들을 닫을 수 있다.
        _exitDone.Wait(TimeSpan.FromSeconds(5));
        ClosePseudoConsoleOnce();
        _exitWait?.Unregister(null);
        _input.Dispose();
        _output.Dispose();
        _processEvent.Dispose();
        _exitDone.Dispose();
        CloseHandle(_processHandle);
    }
}
