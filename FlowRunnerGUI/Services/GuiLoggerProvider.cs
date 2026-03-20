using Microsoft.Extensions.Logging;

namespace FlowRunnerGUI.Services;

public sealed class GuiLoggerProvider : ILoggerProvider
{
    private readonly Action<string> _writeAction;
    private readonly LogLevel _minLevel;

    public GuiLoggerProvider(Action<string> writeAction, LogLevel minLevel = LogLevel.Information)
    {
        _writeAction = writeAction;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName)
    {
        var shortName = categoryName.Contains('.')
            ? categoryName[(categoryName.LastIndexOf('.') + 1)..]
            : categoryName;
        return new GuiLogger(shortName, _writeAction, _minLevel);
    }

    public void Dispose() { }

    private sealed class GuiLogger : ILogger
    {
        private readonly string _category;
        private readonly Action<string> _writeAction;
        private readonly LogLevel _minLevel;

        public GuiLogger(string category, Action<string> writeAction, LogLevel minLevel)
        {
            _category = category;
            _writeAction = writeAction;
            _minLevel = minLevel;
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var level = logLevel switch
            {
                LogLevel.Trace => "TRACE",
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRIT",
                _ => "NONE"
            };

            var message = formatter(state, exception);
            var line = $"[{DateTime.Now:HH:mm:ss}] {level} [{_category}] {message}";
            if (exception is not null)
            {
                line += $" | {exception.Message}";
            }

            _writeAction(line);
        }
    }
}
