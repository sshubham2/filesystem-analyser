namespace FsAnalyser.Core;

/// <summary>The app's optional path argument.</summary>
public static class CommandLine
{
    /// <summary>
    /// Undoes Windows argv escaping of a quoted path that ends in a backslash: typed as "C:\" or
    /// "D:\My Folder\", it arrives as <c>C:"</c> or <c>D:\My Folder"</c>, because \" is an escaped quote.
    /// A trailing quote can only come from that escaping, since '"' cannot appear in a Windows path.
    /// </summary>
    public static string RootArgument(string argument) =>
        argument.EndsWith('"') ? string.Concat(argument.AsSpan(0, argument.Length - 1), @"\") : argument;
}
