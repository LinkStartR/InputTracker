using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace InputTracker;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        IsPrimary = ownsMutex;
    }

    public bool IsPrimary { get; }

    public static SingleInstanceGuard TryAcquireForExecutable() =>
        TryAcquire(Path.GetFullPath(AppContext.BaseDirectory));

    public static SingleInstanceGuard TryAcquire(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(scope.ToUpperInvariant()));
        string name = $"Local\\InputTracker-{Convert.ToHexString(hash.AsSpan(0, 12))}";
        var mutex = new Mutex(initiallyOwned: true, name, out bool createdNew);
        return new SingleInstanceGuard(mutex, createdNew);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _disposed = true;
    }
}
