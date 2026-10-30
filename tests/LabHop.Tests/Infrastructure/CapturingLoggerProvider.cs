using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LabHop.Tests.Infrastructure;

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> messages = new();

    public string AllText => string.Join(Environment.NewLine, messages);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            messages.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
        }
    }
}
