using System.Text.Json;
using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The class 'member-name clashes inside MainWindow' (SL-001, O-018), closed as a class. MainWindow is a partial class whose XAML
/// generates one field per x:Name and whose code-behind declares members, in a namespace holding the App's other types. The members
/// come from ordinary input, a builder naming a TextBlock, a property or a pure class, so the class is the three ways one name is
/// produced twice: an x:Name and a declared member, a method and a data member, an x:Name and a type that MainWindow's own code
/// calls by name. <c>MainWindowNameClashCorpus.json</c> enumerates them as rows fed to <see cref="NameClashes.Find"/>, the design's
/// own first two names among them (a class CoverageText beside a TextBlock named so; a Coverage property beside the Coverage
/// method), and the last test runs the checker over the real tree. The compiler already refuses the first two ways, but only once a
/// build reaches them; the third it does not refuse until a call through the type name fails.
/// </summary>
public class MainWindowNameClashTests
{
    private static readonly string Repo = FailCriticalErrorsTests.FindRepo();

    public static TheoryData<string> Rows()
    {
        using JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, @"tests\FsAnalyser.Core.Tests\MainWindowNameClashCorpus.json")));
        var rows = new TheoryData<string>();
        foreach (JsonElement row in corpus.RootElement.GetProperty("rows").EnumerateArray())
            rows.Add(row.GetProperty("name").GetString()!);
        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void EveryRowOfTheCorpusIsFlaggedOrQuietAsItsRowSays(string name)
    {
        using JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, @"tests\FsAnalyser.Core.Tests\MainWindowNameClashCorpus.json")));
        JsonElement row = corpus.RootElement.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("name").GetString() == name);

        string[] found = NameClashes.Find(
            row.GetProperty("xaml").GetString()!,
            row.GetProperty("code").GetString()!,
            [.. row.GetProperty("types").EnumerateArray().Select(t => t.GetString()!)]);
        string[] expect = [.. row.GetProperty("expect").EnumerateArray().Select(e => e.GetString()!)];

        Assert.Equal(expect, found);
    }

    /// <summary>The corpus holds rows that fire for each way, and rows that stay quiet.</summary>
    [Fact]
    public void TheCorpusHoldsAFiringRowForEveryRuleAndQuietRows()
    {
        using JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, @"tests\FsAnalyser.Core.Tests\MainWindowNameClashCorpus.json")));
        string[][] expects = [.. corpus.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("expect").EnumerateArray().Select(e => e.GetString()!).ToArray())];

        foreach (string rule in new[] { "x-name-member-clash", "member-kind-clash", "x-name-hides-type" })
            Assert.Contains(expects, e => e.Any(x => x.StartsWith(rule + ":", StringComparison.Ordinal)));
        Assert.True(expects.Count(e => e.Length == 0) >= 3, "the corpus needs rows that must stay quiet");
    }

    /// <summary>MainWindow as it is: no x:Name is a declared member or a type, and no name is a method and a data member at once.</summary>
    [Fact]
    public void MainWindowAsItIsHasNoNameProducedTwice()
    {
        string xaml = File.ReadAllText(Path.Combine(Repo, @"src\FsAnalyser.App\MainWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(Repo, @"src\FsAnalyser.App\MainWindow.xaml.cs"));
        string[] types = [.. Directory.EnumerateFiles(Path.Combine(Repo, @"src\FsAnalyser.App"), "*.cs")
            .SelectMany(file => NameClashes.TypeNames(File.ReadAllText(file)))
            .Distinct(StringComparer.Ordinal)];

        string[] found = NameClashes.Find(xaml, code, types);

        Assert.Empty(found);
        // The checker did read both files: the names this slice added are among what it read, and a template's name is not.
        IReadOnlyList<string> xNames = NameClashes.XNames(xaml);
        Assert.Contains("CoveragePanel", xNames);
        Assert.Contains("CoverageLines", xNames);
        Assert.Contains("OutcomeDetail", xNames);
        Assert.DoesNotContain("ShareText", xNames);
        IReadOnlyList<(string Name, string Kind)> members = NameClashes.Members(code);
        Assert.Contains(("Coverage", "method"), members);
        Assert.Contains(("CoverageModel", "data"), members);
        Assert.Contains(("ShowCoverage", "method"), members);
        Assert.Contains(("DriveChoice", "type"), members);
        Assert.True(members.Count > 50, $"the checker read {members.Count} members from MainWindow.xaml.cs");
        Assert.Contains("CoveragePanelText", types);
        Assert.DoesNotContain("CoverageText", types);
    }
}

/// <summary>
/// The checker: a text reading of MainWindow's XAML and code-behind, as the build sees them, with no compiler. It reads the
/// x:Names, the members at the class's own indentation (methods, properties, fields, events, nested types) and the App's type names.
/// </summary>
internal static class NameClashes
{
    private static readonly Regex XName = new(@"x:Name\s*=\s*""(\w+)""", RegexOptions.Compiled);

    // A member at the class's indentation: modifiers, a type (generic, nullable, array or tuple), a name, then what follows the
    // name, which tells a method ('(') from a property ('{' or '=>'), a field ('=' or ';') or an event.
    private static readonly Regex Member = new(
        @"^ {4}(?:\[[^\]]*\]\s*)?(?:(?:public|private|internal|protected|static|readonly|sealed|partial|async|override|abstract|virtual|unsafe|const|new|volatile|extern|required)\s+)*" +
        @"(?<event>event\s+)?(?<type>\([^)]*\)\??|[\w.]+(?:<[^>]*>)?(?:\[[,\s]*\])*\??)\s+(?<name>\w+)\s*(?<tail>\(|\{|=>|=|;)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex NestedType = new(
        @"^ {4}(?:(?:public|private|internal|protected|static|sealed|abstract|partial|readonly|file)\s+)*(?:class|struct|record(?:\s+struct)?|enum|interface)\s+(?<name>\w+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex TopType = new(
        @"^(?:(?:public|internal|sealed|static|abstract|partial|readonly|file)\s+)*(?:class|struct|record(?:\s+struct)?|enum|interface)\s+(?<name>\w+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // The elements that open a name scope of their own: an x:Name inside one is a name in that template or style, never a field of the
    // window (the folder tree's rows name a TextBlock 'ShareText' in a DataTemplate, beside a type of that name).
    private static readonly string[] OwnNameScopes = ["DataTemplate", "HierarchicalDataTemplate", "ControlTemplate", "ItemsPanelTemplate", "Style", "Window.Resources"];

    /// <summary>The x:Names the window gets a field for: those in its own name scope, not inside a template, a style or the resources.</summary>
    internal static IReadOnlyList<string> XNames(string xaml)
    {
        foreach (string scope in OwnNameScopes)
        {
            string tag = Regex.Escape(scope);
            xaml = Regex.Replace(xaml, $@"<{tag}\b[^>]*/>", "", RegexOptions.Singleline);
            xaml = Regex.Replace(xaml, $@"<{tag}\b[^>]*?(?<!/)>.*?</{tag}>", "", RegexOptions.Singleline);
        }
        return [.. XName.Matches(xaml).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];
    }

    internal static IReadOnlyList<string> TypeNames(string source) =>
        [.. TopType.Matches(source).Select(m => m.Groups["name"].Value)];

    /// <summary>Each declared member's name with its kind: 'method', 'data' (property, field, event) or 'type'.</summary>
    internal static IReadOnlyList<(string Name, string Kind)> Members(string code)
    {
        var members = new List<(string, string)>();
        foreach (Match m in Member.Matches(code))
        {
            // A nested type reads as a type and a name too ('private sealed record Row(...)'): it is read once, as a type, below.
            if (m.Groups["type"].Value is "class" or "struct" or "record" or "enum" or "interface")
                continue;
            string tail = m.Groups["tail"].Value;
            members.Add((m.Groups["name"].Value, tail == "(" && !m.Groups["event"].Success ? "method" : "data"));
        }
        foreach (Match m in NestedType.Matches(code))
            members.Add((m.Groups["name"].Value, "type"));
        return members;
    }

    /// <summary>
    /// The clashes, each as '&lt;rule&gt;:&lt;name&gt;', ordered by rule and then name. <paramref name="types"/> are the type names of
    /// the App's namespace.
    /// </summary>
    internal static string[] Find(string xaml, string code, IReadOnlyCollection<string> types)
    {
        IReadOnlyList<string> xNames = XNames(xaml);
        IReadOnlyList<(string Name, string Kind)> members = Members(code);
        var found = new List<string>();

        foreach (string name in xNames.Where(x => members.Any(m => m.Name == x)).Distinct())
            found.Add("x-name-member-clash:" + name);
        foreach (IGrouping<string, (string Name, string Kind)> group in members.GroupBy(m => m.Name))
        {
            string[] kinds = [.. group.Select(m => m.Kind).Distinct()];
            if (kinds.Length > 1 && !xNames.Contains(group.Key))
                found.Add("member-kind-clash:" + group.Key);
        }
        foreach (string name in xNames.Where(x => types.Contains(x)))
            found.Add("x-name-hides-type:" + name);

        return [.. found.OrderBy(entry => entry[..entry.IndexOf(':')], StringComparer.Ordinal).ThenBy(entry => entry, StringComparer.Ordinal)];
    }
}
