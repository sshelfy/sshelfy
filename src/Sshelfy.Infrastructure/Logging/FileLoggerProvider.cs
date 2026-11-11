using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Sshelfy.Infrastructure.Logging;

/// <summary>
/// Writes log entries to logs/app-yyyy-MM-dd.log on a background thread. Old files are removed
/// after <see cref="RetentionDays"/>. The minimum level is read on every entry, so it can change
/// at runtime.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int RetentionDays = 14;

    private readonly string _directory;
    private readonly Func<LogLevel> _minimumLevel;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Task _writer;

    public FileLoggerProvider(string directory, Func<LogLevel> minimumLevel)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
        DeleteOldFiles();
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, ShortName(name)));

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    private bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimumLevel();

    private void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        var currentDay = DateOnly.MinValue;
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (writer is null || today != currentDay)
                {
                    if (writer is not null)
                        await writer.DisposeAsync().ConfigureAwait(false);
                    currentDay = today;
                    writer = OpenFile(today);
                }

                await writer.WriteLineAsync(line).ConfigureAwait(false);
                if (_queue.Reader.Count == 0)
                    await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // Logging must never take the app down; drop entries if the disk misbehaves.
        }
        finally
        {
            if (writer is not null)
                await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private StreamWriter OpenFile(DateOnly day)
    {
        var path = Path.Combine(_directory, $"app-{day:yyyy-MM-dd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(stream, new UTF8Encoding(false));
    }

    private void DeleteOldFiles()
    {
        var cutoff = DateTime.Now.AddDays(-RetentionDays);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string ShortName(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            sb.Append(' ').Append(Level(logLevel)).Append(" [").Append(category).Append("] ");
            sb.Append(formatter(state, exception));
            if (exception is not null)
                sb.AppendLine().Append("    ").Append(exception.ToString().Replace("\n", "\n    "));
            provider.Enqueue(sb.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
    }
}
