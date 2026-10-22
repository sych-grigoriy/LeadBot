using System.Collections.Concurrent;

namespace LeadBot.Application.Messaging;

/// <summary>
/// Сериализует обработку по ключу (один лид — один поток), разные лиды обрабатываются параллельно.
/// Работает в пределах одного процесса; для нескольких инстансов — распределённая блокировка (Redis).
/// </summary>
public sealed class KeyedLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new Releaser(sem);
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                sem.Release();
        }
    }
}
