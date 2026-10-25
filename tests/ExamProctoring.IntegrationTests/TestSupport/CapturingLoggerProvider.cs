using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ExamProctoring.IntegrationTests.TestSupport;

// Keeps every log entry written through it (message, structured values, exception and scopes), so a test can check
// what the API logs.
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<string> _entries = new();
    private IExternalScopeProvider? _scopes;

    public IReadOnlyCollection<string> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            provider._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var parts = new List<string> { category, formatter(state, exception) };
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                parts.AddRange(values.Select(value => $"{value.Key}={value.Value}"));
            if (exception is not null)
                parts.Add(exception.ToString());
            provider._scopes?.ForEachScope((scope, list) => list.Add(scope?.ToString() ?? string.Empty), parts);
            provider._entries.Enqueue(string.Join(" ", parts));
        }
    }
}
