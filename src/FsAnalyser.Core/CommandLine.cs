using System.Text;
using FsAnalyser.Core.Native;

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

    /// <summary>
    /// The arguments the process was started with, with a quoted path that ends in a backslash taken as typed (SL-007).
    /// <see cref="Arguments(string[], string)"/> does the work; this one hands it the command line as typed, and only
    /// when an argument has a quote in it, so a line Windows split cleanly never reads it.
    /// </summary>
    /// <param name="argv">The process's arguments after its own name, as .NET gives them (StartupEventArgs.Args).</param>
    /// <returns>The repaired arguments, <paramref name="argv"/> itself, or null when it cannot be taken: see
    /// <see cref="Arguments(string[], string)"/>.</returns>
    public static string[]? Arguments(string[] argv) =>
        HoldsAQuote(argv) ? Arguments(argv, Kernel32.CommandLineAsTyped()) : argv;

    /// <summary>
    /// A quoted root that ends in one backslash, <c>"D:\Data\" --report out.json</c>, reaches argv as the ONE argument
    /// <c>D:\Data" --report out.json</c>: Windows reads \" as a literal quote and goes on quoting. A '"' cannot occur in a
    /// Windows path or in the app's options, so such a quote can only be the end of a quoted argument that the backslash
    /// before it hid. This reads <paramref name="commandLine"/>, the line as typed, again with one change to Windows' rule:
    /// <b>every '"' opens or closes a quoted part and none is a literal</b>. A run of backslashes before it is halved when it
    /// is even, as Windows does, and kept whole when it is odd, which is the escape that merged the arguments (so
    /// <c>"C:\"</c> names <c>C:\</c>, and <c>"C:\\\"</c> names <c>C:\\\</c>).
    /// <para>
    /// The line is read, and not the arguments, because after a \" Windows flips the quoting of what follows: a quoted
    /// report path with a space in it arrives as two arguments, and a run of spaces in it is one. Only the typed line holds
    /// it. It is not .NET's own command-line string either, which is built again from the split arguments.
    /// </para>
    /// <para>
    /// Three steps, in order. The GATE: an <paramref name="argv"/> with no '"' in it was split cleanly, and is returned as
    /// it came without looking at the line. The ALIGNMENT: the line, split as Windows' C runtime splits it (the program's
    /// name skipped by that runtime's own rule), must give exactly <paramref name="argv"/>, element for element, or this
    /// process was not started from that line as it is read here (a <c>dotnet app.dll</c> launch, for one) and nothing
    /// is repaired. The FALLBACK for that: <paramref name="argv"/> as it is when each '"' in it is the last character of its
    /// argument, which <see cref="RootArgument"/> repairs for the root; null when a '"' is inside one, because the
    /// arguments are then merged and nothing here can say how they were typed.
    /// </para>
    /// </summary>
    /// <param name="argv">The process's arguments after its own name, as .NET gives them.</param>
    /// <param name="commandLine">The command line, program name included, as the process was started with it.</param>
    /// <returns>The arguments as typed, <paramref name="argv"/> itself, or null (see above). Never throws.</returns>
    public static string[]? Arguments(string[] argv, string commandLine)
    {
        if (!HoldsAQuote(argv))
            return argv;

        int rest = AfterProgramName(commandLine);
        if (Split(commandLine, rest, repair: false).SequenceEqual(argv, StringComparer.Ordinal))
            return [.. Split(commandLine, rest, repair: true)];

        return argv.All(QuoteOnlyAtTheEnd) ? argv : null;
    }

    private static bool HoldsAQuote(string[] argv) => argv.Any(argument => argument.Contains('"'));

    // No quote, or exactly one and it is the last character: the shape RootArgument repairs.
    private static bool QuoteOnlyAtTheEnd(string argument)
    {
        int quote = argument.IndexOf('"');
        return quote < 0 || quote == argument.Length - 1;
    }

    // Where the arguments start in a command line: after the program's name, by the C runtime's rule. A quoted name runs to
    // the next quote, with no escapes; an unquoted one to the first space or tab.
    private static int AfterProgramName(string line)
    {
        int p = 0;
        if (line.Length > 0 && line[0] == '"')
        {
            p = 1;
            while (p < line.Length && line[p] != '"')
                p++;
            if (p < line.Length)
                p++;
        }
        else
        {
            while (p < line.Length && line[p] != ' ' && line[p] != '\t')
                p++;
        }
        return p;
    }

    // The C runtime's split (ucrt's parse_cmdline) when repair is false: 2n backslashes before a quote give n backslashes and
    // a quote that opens or closes; 2n+1 give n backslashes and a literal quote; a doubled quote inside quotes is a literal
    // quote with quoting kept on; a pair of quotes gives an argument even when it is empty. With repair true, every quote
    // opens or closes, an even run before it is halved and an odd run kept whole. Spaces and tabs separate arguments outside
    // quotes. Never throws.
    private static List<string> Split(string line, int start, bool repair)
    {
        var arguments = new List<string>();
        int p = start;
        while (true)
        {
            while (p < line.Length && (line[p] == ' ' || line[p] == '\t'))
                p++;
            if (p >= line.Length)
                break;

            var argument = new StringBuilder();
            bool inQuotes = false;
            while (true)
            {
                bool copy = true;
                int backslashes = 0;
                while (p < line.Length && line[p] == '\\')
                {
                    p++;
                    backslashes++;
                }
                if (p < line.Length && line[p] == '"')
                {
                    bool even = backslashes % 2 == 0;
                    if (repair)
                    {
                        copy = false;
                        inQuotes = !inQuotes;
                        if (even)
                            backslashes /= 2;
                    }
                    else
                    {
                        if (even)
                        {
                            if (inQuotes && p + 1 < line.Length && line[p + 1] == '"')
                                p++;
                            else
                            {
                                copy = false;
                                inQuotes = !inQuotes;
                            }
                        }
                        backslashes /= 2;
                    }
                }
                argument.Append('\\', backslashes);
                if (p >= line.Length || (!inQuotes && (line[p] == ' ' || line[p] == '\t')))
                    break;
                if (copy)
                    argument.Append(line[p]);
                p++;
            }
            arguments.Add(argument.ToString());
        }
        return arguments;
    }
}
