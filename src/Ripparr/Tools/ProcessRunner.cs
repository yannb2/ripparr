using System.Diagnostics;
using System.Text;

namespace Ripparr.Tools;

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;

    /// <summary>True when the tool could not be started at all (most often: not installed).</summary>
    public bool NotStarted => ExitCode == ProcessRunner.NotStartedCode;

    /// <summary>The last non-empty line of the error output, or of the output: usually the reason.</summary>
    public string LastLine
    {
        get
        {
            var text = Error.Trim().Length > 0 ? Error : Output;
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? "";
        }
    }
}

/// <summary>What to run: the tool, its arguments, and anything the environment needs.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments)
{
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// About how many bytes the tool reads from start to end (the clips it demuxes): when it prints
    /// no percentage of its own, the job's progress is what it has read so far out of this.
    /// </summary>
    public long? ExpectedReadBytes { get; init; }

    /// <summary>Called with the process id once it runs.</summary>
    public Action<int>? Started { get; init; }

    /// <summary>The command as it would be typed in a shell, for the job log.</summary>
    public string CommandLine => string.Join(' ', new[] { FileName }.Concat(Arguments).Select(Quote));

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.All(c => char.IsLetterOrDigit(c) || "-_./:=+,@%".Contains(c)))
        {
            return argument;
        }

        return "'" + argument.Replace("'", "'\\''") + "'";
    }
}

/// <summary>Runs a command line tool, collects what it prints and can hand over every line as it comes.</summary>
public static class ProcessRunner
{
    public const int NotStartedCode = -1;
    public const int TimedOutCode = -2;

    public static Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(new ProcessSpec(fileName, arguments.ToList()), timeout, null, cancellationToken);
    }

    /// <summary>
    /// Runs a tool that writes binary data (raw pictures) to stdout and returns those bytes, or
    /// null when it fails. Stderr is drained and dropped.
    /// </summary>
    public static async Task<byte[]?> RunBytesAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeoutSource.Token);
            var drain = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await Task.WhenAll(copy, drain);
            await process.WaitForExitAsync(timeoutSource.Token);
            return process.ExitCode == 0 ? output.ToArray() : null;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    /// <param name="onLine">Called for every line on stdout and stderr, as it is printed. Carriage
    /// returns also end a line, so progress counters that rewrite one line come through.</param>
    public static async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, Action<string>? onLine,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // ArgumentList quotes each argument, so file names with spaces,
        // quotes or brackets are passed through untouched.
        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (spec.Environment != null)
        {
            foreach (var (key, value) in spec.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        if (spec.WorkingDirectory != null)
        {
            startInfo.WorkingDirectory = spec.WorkingDirectory;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            spec.Started?.Invoke(process.Id);
        }
        catch (Exception e)
        {
            return new ProcessResult(NotStartedCode, "", $"Could not start {spec.FileName}: {e.Message}");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var output = new StringBuilder();
        var error = new StringBuilder();
        var outputTask = PumpAsync(process.StandardOutput, output, onLine, timeoutSource.Token);
        var errorTask = PumpAsync(process.StandardError, error, onLine, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            await Task.WhenAll(outputTask, errorTask);
            return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult(TimedOutCode, output.ToString(),
                $"{spec.FileName} took longer than {timeout.TotalMinutes:0.#} min and was stopped");
        }
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sink, Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            lock (sink)
            {
                sink.Append(buffer, 0, read);
            }

            if (onLine == null)
            {
                continue;
            }

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\n' or '\r')
                {
                    if (line.Length > 0)
                    {
                        onLine(line.ToString());
                        line.Clear();
                    }
                }
                else
                {
                    line.Append(c);
                }
            }
        }

        if (onLine != null && line.Length > 0)
        {
            onLine(line.ToString());
        }
    }
}
