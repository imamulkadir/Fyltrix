namespace Fyltrix.Core;

/// <summary>Tracks writes that can outlive a tool or automation engine. Used only to refuse update-triggered shutdown.</summary>
internal static class UpdateWork
{
    private static int running;
    internal static int RunningCount => Volatile.Read(ref running);
    internal static IDisposable Track()
    {
        Interlocked.Increment(ref running);
        return new Scope();
    }
    private sealed class Scope : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Decrement(ref running); }
    }
}
