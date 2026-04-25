namespace StorageCleaner.Core;

public readonly struct PauseToken
{
    private readonly PauseTokenSource? _source;

    internal PauseToken(PauseTokenSource? source)
    {
        _source = source;
    }

    public bool IsPaused => _source?.IsPaused ?? false;

    public Task WaitWhilePausedAsync(CancellationToken cancellationToken = default)
        => _source?.WaitWhilePausedAsync(cancellationToken) ?? Task.CompletedTask;
}

public sealed class PauseTokenSource
{
    private volatile TaskCompletionSource<bool>? _paused;

    public bool IsPaused => _paused is not null;

    public PauseToken Token => new(this);

    public void Pause()
    {
        Interlocked.CompareExchange(
            ref _paused,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            null);
    }

    public void Resume()
    {
        while (true)
        {
            var current = _paused;
            if (current is null)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _paused, null, current) == current)
            {
                current.TrySetResult(true);
                return;
            }
        }
    }

    internal Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        var paused = _paused;
        if (paused is null)
        {
            return Task.CompletedTask;
        }

        if (!cancellationToken.CanBeCanceled)
        {
            return paused.Task;
        }

        return paused.Task.WaitAsync(cancellationToken);
    }
}
