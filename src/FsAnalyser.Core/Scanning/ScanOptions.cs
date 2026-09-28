namespace FsAnalyser.Core.Scanning;

/// <summary>The scanner's test seam for a whole scan; the cancel guard has its own, per call. The app never sets it.</summary>
internal sealed class ScanOptions
{
    public static readonly ScanOptions Default = new();

    /// <summary>List the whole scan with FILE_FULL_DIR_INFO, as on a volume without the extended class.</summary>
    public bool ForceFullDirectoryClass { get; init; }

    /// <summary>
    /// Replaces the FileId each entry was listed with, as a volume that answers the extended class with no ID, or
    /// a "no ID" value, for some or all of its files would list it (HF-001). Null leaves the listing's.
    /// </summary>
    public Func<UInt128, UInt128>? ListedFileId { get; init; }

    /// <summary>Called after each folder is listed, with the number of folders listed so far (the root is 1).</summary>
    public Action<int>? AfterFolder { get; init; }

    /// <summary>
    /// The least time between two top-level snapshots with something changed in them; the first and the
    /// last are always published. Tests set it to zero to see every change.
    /// </summary>
    public TimeSpan TopLevelInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The volume read, given the root without <c>\\?\</c>; <see cref="VolumeSpace.Read"/> when null. Tests pass one that blocks.</summary>
    public Func<string, long?>? VolumeReader { get; init; }

    /// <summary>
    /// How long the used-space figure waits for the volume before it is given up as null. A local volume answers in
    /// well under a millisecond; on a share that stopped answering the call waits as long as the SMB client does
    /// (21 to 66 s measured), and the figure does not wait that long. The read's thread itself stays parked until
    /// the call returns.
    /// </summary>
    public TimeSpan VolumeReadBound { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Read the volume whatever the root, so a folder scan can be shown to complete while the read is stalled.</summary>
    public bool ReadVolumeOfAnyRoot { get; init; }

    /// <summary>
    /// What each probe worker runs for a file; <see cref="SizeProbe.Probe"/> when null. Tests pass one that
    /// blocks, throws, records the paths it is given or reports sizes of its own.
    /// </summary>
    public ProbeFile? Probe { get; init; }

    /// <summary>
    /// Which appended files are probed; <see cref="SizeProbe.IsEligible"/> when null. A test passes the rule
    /// SL-056 replaced, to show that its fixture fails under it.
    /// </summary>
    public ProbeRule? ProbeRule { get; init; }

    /// <summary>How many worker threads probe; <see cref="ProbePass.DefaultWorkers"/> when 0.</summary>
    public int ProbeWorkers { get; init; }

    /// <summary>Files per batch of probes; <see cref="ProbePass.BatchSize"/> when 0. Tests make it small to see several.</summary>
    public int ProbeBatch { get; init; }

    /// <summary>How long a cancelled scan waits for probe workers stuck in a call before it returns without them.</summary>
    public TimeSpan ProbeAbandonAfter { get; init; } = ProbePass.DefaultAbandonAfter;

    /// <summary>
    /// Folders a sync engine manages, under which no folder's last-access time is kept (review M6); null for the
    /// OneDrive folders the environment names (<see cref="Scanner.SyncRootsFromEnvironment"/>).
    /// </summary>
    public IReadOnlyList<string>? SyncRoots { get; init; }
}
