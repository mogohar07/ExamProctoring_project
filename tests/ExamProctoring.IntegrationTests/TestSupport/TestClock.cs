using ExamProctoring.Application.Abstractions;

namespace ExamProctoring.IntegrationTests.TestSupport;

// The API's IClock in the shared test host: the real time, unless a test freezes it or moves it forward.
// Tests in the shared collection run one at a time, and each change is undone when its scope is disposed,
// so no other test sees it. JWT lifetime validation in the pipeline still uses the real time.
public sealed class TestClock : IClock
{
    private DateTimeOffset? _frozenAt;
    private TimeSpan _offset;

    public DateTimeOffset UtcNow => _frozenAt ?? DateTimeOffset.UtcNow + _offset;

    public IDisposable FreezeAt(DateTimeOffset at)
    {
        _frozenAt = at;
        return new Reset(this);
    }

    public IDisposable Advance(TimeSpan by)
    {
        _offset = by;
        return new Reset(this);
    }

    private sealed class Reset(TestClock clock) : IDisposable
    {
        public void Dispose()
        {
            clock._frozenAt = null;
            clock._offset = TimeSpan.Zero;
        }
    }
}
