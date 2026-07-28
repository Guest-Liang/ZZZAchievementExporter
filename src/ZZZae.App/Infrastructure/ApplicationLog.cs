using System.Runtime.InteropServices;
using System.Text;

namespace ZZZae.App.Infrastructure;

internal sealed class ApplicationLog : IDisposable
{
    private const string FileName = "ZZZae.log";

    private static readonly object CurrentGate = new();
    private static ApplicationLog? _current;

    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly TeeTextWriter _teeOut;
    private readonly TeeTextWriter _teeError;
    private readonly LogSink _sink;
    private bool _disposed;

    private ApplicationLog(string filePath, TextWriter originalOut, TextWriter originalError, LogSink sink)
    {
        FilePath = filePath;
        _originalOut = originalOut;
        _originalError = originalError;
        _sink = sink;
        _teeOut = new TeeTextWriter(originalOut, sink);
        _teeError = new TeeTextWriter(originalError, sink);
    }

    public string FilePath { get; }

    public static string? CurrentFilePath
    {
        get
        {
            lock (CurrentGate)
            {
                return _current?.FilePath;
            }
        }
    }

    public static ApplicationLog? TryStart()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var filePath = Path.Combine(AppContext.BaseDirectory, FileName);
        FileStream? stream = null;
        LogSink? sink = null;

        try
        {
            stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
            sink = new LogSink(writer);
            var log = new ApplicationLog(filePath, originalOut, originalError, sink);

            Console.SetOut(log._teeOut);
            Console.SetError(log._teeError);
            log.WriteSessionHeader();

            lock (CurrentGate)
            {
                _current = log;
            }

            stream = null;
            sink = null;
            return log;
        }
        catch (Exception exception)
        {
            try
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
            catch (Exception)
            {
                // Preserve the original initialization error.
            }

            sink?.Dispose();
            stream?.Dispose();
            originalError.WriteLine($"警告：无法创建日志文件 {filePath}：{exception.Message}");
            return null;
        }
    }

    public static void WriteDiagnostic(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        lock (CurrentGate)
        {
            _current?._sink.WriteLine($"[{NowInChina():yyyy-MM-dd HH:mm:ss.fff zzz}] {message}");
        }
    }

    public static void WriteException(string context, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentNullException.ThrowIfNull(exception);

        WriteDiagnostic($"{context}{Environment.NewLine}{exception}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sink.WriteLine($"[{NowInChina():yyyy-MM-dd HH:mm:ss.fff zzz}] 日志会话结束。");
        _sink.WriteLine(string.Empty);

        if (ReferenceEquals(Console.Out, _teeOut))
        {
            Console.SetOut(_originalOut);
        }
        if (ReferenceEquals(Console.Error, _teeError))
        {
            Console.SetError(_originalError);
        }

        lock (CurrentGate)
        {
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        }

        _sink.Dispose();
    }

    private void WriteSessionHeader()
    {
        _sink.WriteLine(string.Empty);
        _sink.WriteLine("================================================================");
        _sink.WriteLine($"[{NowInChina():yyyy-MM-dd HH:mm:ss.fff zzz}] ZZZae 日志会话开始");
        _sink.WriteLine($"可执行文件：{Environment.ProcessPath ?? "unknown"}");
        _sink.WriteLine($"操作系统：{RuntimeInformation.OSDescription}");
        _sink.WriteLine(
            $"进程架构：{RuntimeInformation.ProcessArchitecture}；"
                + $"运行时：{RuntimeInformation.FrameworkDescription}"
        );
        _sink.WriteLine("================================================================");
    }

    private static DateTimeOffset NowInChina()
    {
        return DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
    }

    private sealed class LogSink : IDisposable
    {
        private readonly object _gate = new();
        private readonly TextWriter _writer;
        private bool _available = true;

        public LogSink(TextWriter writer)
        {
            _writer = writer;
        }

        public void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            TryWrite(static (writer, state) => writer.Write(state), value);
        }

        public void Write(char[] buffer, int index, int count)
        {
            TryWrite(
                static (writer, state) => writer.Write(state.Buffer, state.Index, state.Count),
                (Buffer: buffer, Index: index, Count: count)
            );
        }

        public void WriteLine(string? value)
        {
            TryWrite(static (writer, state) => writer.WriteLine(state), value);
        }

        public void WriteLine()
        {
            TryWrite(static (writer, _) => writer.WriteLine(), 0);
        }

        public void Flush()
        {
            TryWrite(static (writer, _) => writer.Flush(), 0);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _available = false;
                try
                {
                    _writer.Dispose();
                }
                catch (Exception)
                {
                    // A final flush failure must not change the app result.
                }
            }
        }

        private void TryWrite<TState>(Action<TextWriter, TState> write, TState state)
        {
            lock (_gate)
            {
                if (!_available)
                {
                    return;
                }

                try
                {
                    write(_writer, state);
                }
                catch (Exception)
                {
                    // Logging must never prevent an achievement export.
                    _available = false;
                }
            }
        }
    }

    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly LogSink _sink;

        public TeeTextWriter(TextWriter console, LogSink sink)
        {
            _console = console;
            _sink = sink;
        }

        public override Encoding Encoding => _console.Encoding;

        public override void Write(char value)
        {
            _console.Write(value);
            _sink.Write(value.ToString());
        }

        public override void Write(string? value)
        {
            _console.Write(value);
            _sink.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            _console.Write(buffer, index, count);
            _sink.Write(buffer, index, count);
        }

        public override void WriteLine()
        {
            _console.WriteLine();
            _sink.WriteLine();
        }

        public override void WriteLine(string? value)
        {
            _console.WriteLine(value);
            _sink.WriteLine(value);
        }

        public override void Flush()
        {
            _console.Flush();
            _sink.Flush();
        }
    }
}
