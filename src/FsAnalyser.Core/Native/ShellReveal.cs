using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FsAnalyser.Core.Native;

public enum RevealKind
{
    Exact,
    Ancestor,
    Failed
}

public enum RevealReason
{
    None,
    NameUnaddressable,
    NoLongerExists
}

public sealed record RevealPlan(IReadOnlyList<string> Candidates, string? UnaddressableName, string Requested = "");

public sealed record RevealOutcome(
    RevealKind Kind,
    string Requested,
    string? Shown,
    RevealReason Reason,
    string? UnaddressableName,
    bool Hidden,
    bool System,
    int HResult);

/// <summary>
/// Reveals an item in Windows Explorer by jumping to it with the item selected.
/// When Explorer mis-parses a name, or an item was deleted, it navigates to the nearest addressable ancestor.
/// </summary>
public static partial class ShellReveal
{
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int ERROR_FILE_NOT_FOUND_HR = unchecked((int)0x80070002);
    private const int ERROR_PATH_NOT_FOUND_HR = unchecked((int)0x80070003);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder,
        uint cidl,
        IntPtr apidl,
        uint dwFlags);

    [LibraryImport("shell32.dll")]
    private static partial void ILFree(IntPtr pidl);

    /// <summary>
    /// Pure planner: turns a path into an ordered list of reveal candidates and detects unaddressable names.
    /// Does no I/O.
    /// </summary>
    public static RevealPlan Plan(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A path is required.", nameof(path));

        string display = LongPath.ToDisplay(path);
        if (display.Length == 2 && char.IsAsciiLetter(display[0]) && display[1] == ':')
            display += '\\';

        display = display.Replace('/', '\\');

        if (!Path.IsPathFullyQualified(display))
            throw new ArgumentException($"'{path}' is not an absolute path.", nameof(path));

        string root;
        string remainder;

        if (display.StartsWith(@"\\"))
        {
            // UNC path: \\server\share[\more]
            int serverEnd = display.IndexOf('\\', 2);
            if (serverEnd < 0)
                throw new ArgumentException($"'{path}' is not a valid UNC path.", nameof(path));

            int shareEnd = display.IndexOf('\\', serverEnd + 1);
            if (shareEnd < 0)
            {
                root = display;
                remainder = string.Empty;
            }
            else
            {
                root = display[..shareEnd];
                remainder = display[(shareEnd + 1)..];
            }
        }
        else
        {
            // Drive path: X:\...
            if (display.Length < 3 || display[1] != ':' || display[2] != '\\')
                throw new ArgumentException($"'{path}' is not a valid drive path.", nameof(path));

            root = display[..3]; // e.g. "C:\"
            remainder = display[3..];
        }

        string[] parts = remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        int misparsedIndex = -1;
        string? unaddressableName = null;

        for (int i = 0; i < parts.Length; i++)
        {
            if (IsMisparsed(parts[i], out string badName))
            {
                misparsedIndex = i;
                unaddressableName = badName;
                break;
            }
        }

        var candidates = new List<string>();

        if (misparsedIndex >= 0)
        {
            // Candidates start at the misparsed component's parent
            for (int k = misparsedIndex; k >= 1; k--)
                candidates.Add(Combine(root, parts, k));
            candidates.Add(root);
        }
        else
        {
            // Candidates start at the item itself down to the root
            for (int k = parts.Length; k >= 1; k--)
                candidates.Add(Combine(root, parts, k));
            if (parts.Length == 0)
                candidates.Add(root);
            else if (!candidates.Contains(root, StringComparer.OrdinalIgnoreCase))
                candidates.Add(root);
        }

        return new RevealPlan(candidates, unaddressableName, display);
    }

    private static bool IsMisparsed(string component, out string name)
    {
        name = component;
        if (component.EndsWith('.') || component.EndsWith(' '))
            return true;

        int dot = component.IndexOf('.');
        string baseName = dot >= 0 ? component[..dot] : component;
        baseName = baseName.TrimEnd(' ');

        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && baseName.Length >= 4)
        {
            string suffix = baseName[3..];
            if (suffix.Length == 1 && (char.IsDigit(suffix[0]) || suffix[0] is '¹' or '²' or '³'))
                return true;
        }

        if (baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase) && baseName.Length >= 4)
        {
            string suffix = baseName[3..];
            if (suffix.Length == 1 && (char.IsDigit(suffix[0]) || suffix[0] is '¹' or '²' or '³'))
                return true;
        }

        return false;
    }

    private static string Combine(string root, string[] parts, int count)
    {
        if (count == 0)
            return root;

        var sb = new StringBuilder(root);
        if (!root.EndsWith('\\'))
            sb.Append('\\');

        for (int i = 0; i < count; i++)
        {
            if (i > 0)
                sb.Append('\\');
            sb.Append(parts[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Executes the reveal loop against candidates in the plan using a given tryReveal delegate.
    /// Unit-testable without calling Explorer.
    /// </summary>
    public static RevealOutcome Walk(RevealPlan plan, Func<string, int> tryReveal) =>
        Walk(plan, tryReveal, plan.Requested);

    public static RevealOutcome Walk(RevealPlan plan, Func<string, int> tryReveal, string? requested)
    {
        string requestedPath = !string.IsNullOrEmpty(requested)
            ? requested
            : (!string.IsNullOrEmpty(plan.Requested) ? plan.Requested : (plan.Candidates.Count > 0 ? plan.Candidates[0] : string.Empty));

        if (plan.Candidates.Count == 0)
        {
            return new RevealOutcome(
                RevealKind.Failed,
                requestedPath,
                null,
                RevealReason.None,
                plan.UnaddressableName,
                false,
                false,
                ERROR_PATH_NOT_FOUND_HR);
        }

        bool firstCandidateNotFound = false;

        for (int i = 0; i < plan.Candidates.Count; i++)
        {
            string candidate = plan.Candidates[i];
            int hr = tryReveal(candidate);

            if (hr == 0) // S_OK
            {
                bool isExact = (i == 0 && plan.UnaddressableName == null);
                RevealKind kind = isExact ? RevealKind.Exact : RevealKind.Ancestor;
                RevealReason reason = RevealReason.None;
                if (!isExact)
                {
                    reason = firstCandidateNotFound
                        ? RevealReason.NoLongerExists
                        : (plan.UnaddressableName != null ? RevealReason.NameUnaddressable : RevealReason.None);
                }

                bool isHidden = false;
                bool isSystem = false;
                if (isExact && plan.Candidates.Count > 1)
                {
                    try
                    {
                        string prefixed = LongPath.ToExtended(requestedPath);
                        FileAttributes attrs = File.GetAttributes(prefixed);
                        isHidden = (attrs & FileAttributes.Hidden) != 0;
                        isSystem = (attrs & FileAttributes.System) != 0;
                    }
                    catch
                    {
                        isHidden = false;
                        isSystem = false;
                    }
                }

                return new RevealOutcome(
                    kind,
                    requestedPath,
                    candidate,
                    reason,
                    plan.UnaddressableName,
                    isHidden,
                    isSystem,
                    0);
            }

            if (hr == ERROR_FILE_NOT_FOUND_HR || hr == ERROR_PATH_NOT_FOUND_HR)
            {
                if (i == 0)
                    firstCandidateNotFound = true;
                continue;
            }

            // Any other failing HRESULT stops immediately
            return new RevealOutcome(
                RevealKind.Failed,
                requestedPath,
                null,
                RevealReason.None,
                plan.UnaddressableName,
                false,
                false,
                hr);
        }

        // Every candidate was not found
        return new RevealOutcome(
            RevealKind.Failed,
            requestedPath,
            null,
            firstCandidateNotFound ? RevealReason.NoLongerExists : RevealReason.None,
            plan.UnaddressableName,
            false,
            false,
            ERROR_PATH_NOT_FOUND_HR);
    }

    private static int TryReveal(string candidate)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            int hr = SHParseDisplayName(candidate, IntPtr.Zero, out pidl, 0, out _);
            if (hr != 0)
                return hr;

            hr = SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);
            return hr;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
                ILFree(pidl);
        }
    }

    /// <summary>
    /// Launches a background STA thread to reveal the item in Explorer without blocking the caller.
    /// The returned Task never faults.
    /// </summary>
    public static Task<RevealOutcome> RevealAsync(string path)
    {
        var tcs = new TaskCompletionSource<RevealOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                RevealPlan plan;
                try
                {
                    plan = Plan(path);
                }
                catch (ArgumentException)
                {
                    tcs.SetResult(new RevealOutcome(
                        RevealKind.Failed,
                        path ?? string.Empty,
                        null,
                        RevealReason.None,
                        null,
                        false,
                        false,
                        E_INVALIDARG));
                    return;
                }

                RevealOutcome outcome = Walk(plan, TryReveal, path);
                tcs.SetResult(outcome);
            }
            catch (Exception ex)
            {
                tcs.SetResult(new RevealOutcome(
                    RevealKind.Failed,
                    path ?? string.Empty,
                    null,
                    RevealReason.None,
                    null,
                    false,
                    false,
                    ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005)));
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }
}
