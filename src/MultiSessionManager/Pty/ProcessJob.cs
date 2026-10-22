using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static MultiSessionManager.Pty.NativeMethods;

namespace MultiSessionManager.Pty;

/// <summary>
/// 자식 프로세스를 묶는 Job Object. 핸들이 닫히면(앱이 정상 종료하든 강제 종료되든)
/// 묶인 프로세스와 그 자손까지 OS 가 함께 끝낸다.
/// </summary>
public sealed class ProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    public ProcessJob()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject 실패");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(info)))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject 실패");
    }

    internal void Assign(IntPtr processHandle)
    {
        if (!AssignProcessToJobObject(_handle, processHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject 실패");
    }

    /// <summary>지금 Job 안에서 돌고 있는 프로세스 수(자손 포함).</summary>
    public int ActiveProcesses =>
        QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, out var info, Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
            ? (int)info.ActiveProcesses
            : 0;

    /// <summary>묶인 프로세스가 모두 끝날 때까지 기다린다. 시간 안에 비면 true.</summary>
    public bool WaitUntilEmpty(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (ActiveProcesses > 0)
        {
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(100);
        }
        return true;
    }

    public void Dispose() => _handle.Dispose();
}
