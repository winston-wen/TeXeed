using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace TeXeed.App.AI;

/// <summary>Real user-level PowerShell. Permission checks belong to the session-bound host.</summary>
internal static class AiShell
{
    internal static async Task<string> ExecuteAsync(string root, string command, string workingDirectory, int timeoutSeconds, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 100_000)
            throw new InvalidOperationException("Shell 命令应为非空文本，最多 10 万字符。");
        var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(workingDirectory) ? root : Path.Combine(root, workingDirectory));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("工作目录不存在：" + directory);
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // Pass code as one UTF-16 encoded argument: no nested shell quoting or interpolation.
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); $OutputEncoding = [Console]::OutputEncoding;\n" + command)) })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 600)));
        try { process.Start(); }
        catch (Win32Exception ex) { throw new InvalidOperationException("无法启动 PowerShell：" + ex.Message, ex); }
        process.StandardInput.Close();
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { } });
        var stdout = DrainAsync(process.StandardOutput, timeout.Token);
        var stderr = DrainAsync(process.StandardError, timeout.Token);
        var timedOut = false;
        try { await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr); }
        catch (OperationCanceledException) { timedOut = true; }
        token.ThrowIfCancellationRequested();
        if (timedOut) return $"Shell 超时（{Math.Clamp(timeoutSeconds, 1, 600)} 秒），已终止命令及子进程；命令可能已产生部分改动。\n工作目录：{directory}";
        return $"Shell 退出码：{process.ExitCode}\n工作目录：{directory}\n标准输出：\n{await stdout}\n标准错误：\n{await stderr}";
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken token)
    {
        const int maximum = 24_000;
        var output = new StringBuilder(); var buffer = new char[4096]; var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            var retained = Math.Min(count, maximum - output.Length);
            output.Append(buffer, 0, retained); truncated |= retained < count;
        }
        if (truncated) output.Append("\n[输出截断：超过 24000 字符，请缩小查询范围或将输出写入文件后分段读取。]");
        return output.ToString();
    }
}
