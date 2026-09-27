namespace FsAnalyser.Core.Scanning;

/// <summary>
/// The one thread allowed to use an object, recorded when the object is made. Makes a single-writer rule
/// structural rather than remembered (SL-056, review M2): a call from any other thread throws, so every test
/// that runs a scan enforces it. The same idiom as <see cref="SynchronousIoCancel"/>'s owner check.
/// </summary>
internal sealed class ThreadOwner(string what)
{
    private readonly int _owner = Environment.CurrentManagedThreadId;

    /// <exception cref="InvalidOperationException">The caller is not the thread that made the owner.</exception>
    public void Check()
    {
        if (Environment.CurrentManagedThreadId != _owner)
        {
            throw new InvalidOperationException(
                $"{what} is used only on the thread that made it (thread {_owner}), not on thread {Environment.CurrentManagedThreadId}. " +
                "The scan store has one writer: SL-042 measured folder totals swinging by hundreds of MiB when several threads wrote them.");
        }
    }
}
