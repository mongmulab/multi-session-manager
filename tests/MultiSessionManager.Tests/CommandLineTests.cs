using System.Runtime.InteropServices;
using MultiSessionManager.Pty;

namespace MultiSessionManager.Tests;

public class CommandLineTests
{
    // 만든 명령줄을 Windows 가 실제로 쪼개는 방식(CommandLineToArgvW)으로 되돌려 본다.
    [Theory]
    [InlineData("plain")]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("quote\"inside")]
    [InlineData(@"C:\Program Files\")]
    [InlineData(@"trailing\\")]
    [InlineData(@"back\""slash")]
    [InlineData("한글 이름 세션")]
    [InlineData("tab\there")]
    public void Arguments_survive_round_trip(string arg)
    {
        var line = CommandLine.Build(@"C:\x\claude.exe", ["--name", arg]);
        var parsed = Split(line);
        Assert.Equal([@"C:\x\claude.exe", "--name", arg], parsed);
    }

    [Fact]
    public void Simple_arguments_are_not_quoted()
    {
        Assert.Equal("claude.exe --resume abc", CommandLine.Build("claude.exe", ["--resume", "abc"]));
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static string[] Split(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        try
        {
            var result = new string[argc];
            for (var i = 0; i < argc; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }
}
