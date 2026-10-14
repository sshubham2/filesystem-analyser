namespace FsAnalyser.Core;

/// <summary>
/// What an entry holds for certain after a stop (SL-112, SL-113): a lower bound on its size, on-disk size and file count,
/// as <see cref="Store.ScanStore.AtLeast"/> gives it. Only Core's store makes one: the constructor is internal, Core grants
/// <c>InternalsVisibleTo</c> to its tests alone, and the properties have no setters, so a window that has to word a
/// figure as "at least" can only be handed a bound the store produced, never a rolled figure or a listing figure with the
/// word put in front of it (SL-113). Rolled figures are not a lower bound: a stale-large hardlink first name leaves them
/// above the final ones (SL-112).
/// </summary>
/// <remarks>
/// A struct, so the parameterless <c>default</c> exists and reads (0, 0, 0). That is still a true lower bound, which is why it
/// is not the defect this type closes. It is a plain readonly struct, not a record: a record's <c>with</c> would let a
/// copy carry other figures.
/// </remarks>
public readonly struct AtLeastFigures : IEquatable<AtLeastFigures>
{
    internal AtLeastFigures(long size, long onDisk, int files)
    {
        Size = size;
        OnDisk = onDisk;
        Files = files;
    }

    /// <summary>Logical bytes the entry holds for certain.</summary>
    public long Size { get; }

    /// <summary>Space on disk, in bytes, the entry holds for certain.</summary>
    public long OnDisk { get; }

    /// <summary>Files at or under the entry (a file counts itself).</summary>
    public int Files { get; }

    public bool Equals(AtLeastFigures other) => Size == other.Size && OnDisk == other.OnDisk && Files == other.Files;

    public override bool Equals(object? obj) => obj is AtLeastFigures other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Size, OnDisk, Files);

    public static bool operator ==(AtLeastFigures left, AtLeastFigures right) => left.Equals(right);

    public static bool operator !=(AtLeastFigures left, AtLeastFigures right) => !left.Equals(right);

    public override string ToString() => $"({Size}, {OnDisk}, {Files})";
}
