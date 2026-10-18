using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FsAnalyser.App;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-113, review M3 (obligation O-018): the class "a figure worded at least that is not the store's lower bound" is closed in the
/// type system, and this holds the closure. AtLeastSeamCorpus.json enumerates the class by who produces its members: the values a
/// caller can hold (the argument types), the ways a bound is made (the producers) and the places one is carried (the carriers), and
/// what attempt 1's text rule did with each spelling of the first, the 'before'. The App's composers take the bound whole and nothing
/// else converts to it; only the store's own read makes one, and only the tracker puts one on a row, found in the IL of every method of
/// Core (a text rule cannot see a target-typed new(), a local function or a ternary); and no other member of Core or of the App's
/// words carries one. A slice that needs another producer, carrier or composer adds a row on purpose.
/// </summary>
public class AtLeastSeamTests
{
    private static readonly Assembly CoreAssembly = typeof(AtLeastFigures).Assembly;

    private static JsonElement Corpus
    {
        get
        {
            string path = Path.Combine(FailCriticalErrorsTests.FindRepo(), @"tests\FsAnalyser.Core.Tests\AtLeastSeamCorpus.json");
            return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        }
    }

    // A name with a generic argument from Core (Nullable<AtLeastFigures>) carries no assembly name, so Core is where the argument is looked for.
    private static Type Resolve(string name) =>
        Type.GetType(name, _ => CoreAssembly, (assembly, typeName, _) => (assembly ?? CoreAssembly).GetType(typeName) ?? Type.GetType(typeName))
            ?? throw new InvalidOperationException($"the corpus names a type nobody can find: {name}");

    // ---- The bound itself ----

    /// <summary>
    /// A bound cannot be made, changed or converted to from outside the store: no public constructor, no setter, no conversion operator
    /// either way, no field a caller can reach, and its constructor internal to Core, to whom Core.Tests alone is granted.
    /// </summary>
    [Fact]
    public void TheBoundHasNoWayInButTheStoresOwnRead()
    {
        Type t = typeof(AtLeastFigures);
        Assert.True(t.IsValueType && t.IsVisible);
        Assert.Empty(t.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        ConstructorInfo[] constructors = t.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Single(constructors);
        Assert.True(constructors[0].IsAssembly, "the constructor is not internal");
        Assert.All(t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), f => Assert.True(f.IsInitOnly && f.IsPrivate, $"{f.Name} can be reached or changed"));
        Assert.All(t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), p => Assert.Null(p.SetMethod));
        Assert.DoesNotContain(t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), m => m.Name is "op_Implicit" or "op_Explicit");
        Assert.DoesNotContain(t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), m => m.Name == "<Clone>$"); // not a record: no 'with'
    }

    /// <summary>
    /// Every value a caller can hold (the corpus's arguments), against every composer: none is assignable to the composer's parameter,
    /// and no conversion to the bound exists on either type. The composers are the non-private methods of TopLevelRowText with the word
    /// in their name, and each takes the bound and nothing else.
    /// </summary>
    [Fact]
    public void NoComposerTakesAnythingButTheBoundAndNothingConvertsToIt()
    {
        JsonElement composers = Corpus.GetProperty("composers");
        Type holder = typeof(TopLevelRowText);
        Assert.Equal(composers.GetProperty("type").GetString(), holder.FullName);
        Type bound = Resolve(composers.GetProperty("parameter").GetString()!);

        string[] listed = composers.GetProperty("methods").EnumerateArray().Select(e => e.GetString()!).Order().ToArray();
        MethodInfo[] found = holder.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsPrivate && m.Name.Contains("AtLeast", StringComparison.Ordinal)).ToArray();
        Assert.Equal(listed, found.Select(m => m.Name).Order());
        foreach (MethodInfo m in found)
        {
            ParameterInfo[] parameters = m.GetParameters();
            Assert.True(parameters.Length == 1 && parameters[0].ParameterType == bound, $"{m.Name} takes {string.Join(", ", parameters.Select(p => p.ParameterType.Name))}, not only the bound");
            Assert.Equal(typeof(string), m.ReturnType);
        }

        int checkedArguments = 0;
        foreach (JsonElement argument in Corpus.GetProperty("arguments").EnumerateArray())
        {
            string id = argument.GetProperty("id").GetString()!;
            Type type = Resolve(argument.GetProperty("type").GetString()!);
            foreach (MethodInfo m in found)
            {
                Type parameter = m.GetParameters()[0].ParameterType;
                Assert.False(parameter.IsAssignableFrom(type), $"{id} ({type.Name}) is assignable to {m.Name}'s parameter");
                Assert.False(HasConversion(type, bound) || HasConversion(bound, type), $"{id} ({type.Name}) converts to or from the bound");
            }
            checkedArguments++;
        }
        Assert.True(checkedArguments >= 10, $"{checkedArguments} argument types in the corpus");
    }

    private static bool HasConversion(Type from, Type to) =>
        from.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Concat(to.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Any(m => m.Name is "op_Implicit" or "op_Explicit" && m.ReturnType == to && m.GetParameters() is [{ } p] && p.ParameterType == from);

    // ---- Who produces and who carries ----

    /// <summary>
    /// Read from the IL of every method of Core, as the callers of the bound's constructor, of the row's AtLeast setter (an object
    /// initializer, a with, a plain assignment all call it) and of the store's AtLeast read. Each set is the corpus's, no more.
    /// </summary>
    [Fact]
    public void OnlyTheStoreMakesABoundAndOnlyTheTrackerReadsItAndPutsItOnARow()
    {
        JsonElement producers = Corpus.GetProperty("producers");
        ConstructorInfo constructor = typeof(AtLeastFigures).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        MethodInfo setter = typeof(TopLevelRow).GetProperty(nameof(TopLevelRow.AtLeast))!.SetMethod!;
        MethodInfo read = typeof(ScanStore).GetMethod(nameof(ScanStore.AtLeast))!;

        Assert.Equal(Names(producers, "construct"), Callers(CoreAssembly, [0x73], constructor.MetadataToken));
        Assert.Equal(Names(producers, "putOnARow"), Callers(CoreAssembly, [0x28, 0x6F], setter.MetadataToken));
        Assert.Equal(Names(producers, "readTheStore"), Callers(CoreAssembly, [0x28, 0x6F], read.MetadataToken));
    }

    /// <summary>
    /// Every field, property, method return and parameter of Core (and of the App's words, compiled here as source) whose type is the
    /// bound or the bound that may be absent, besides the type's own members: the places a bound sits or travels through.
    /// </summary>
    [Fact]
    public void NothingCarriesABoundButWhatTheCorpusLists()
    {
        JsonElement carriers = Corpus.GetProperty("carriers");
        Assert.Equal(Names(carriers, "core"), Carriers(CoreAssembly.GetTypes()));
        Assert.Equal(Names(carriers, "appText"), Carriers(typeof(TopLevelRowText).Assembly.GetTypes().Where(t => t.Namespace == "FsAnalyser.App")));
    }

    private static string[] Names(JsonElement section, string list) =>
        section.GetProperty(list).EnumerateArray().Select(e => e.GetString()!).Order().ToArray();

    private static readonly BindingFlags Everything =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static string[] Carriers(IEnumerable<Type> types)
    {
        var carriers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Type type in types)
        {
            if (type == typeof(AtLeastFigures))
                continue;
            string owner = Outermost(type);
            foreach (FieldInfo f in type.GetFields(Everything).Where(f => IsBound(f.FieldType)))
                carriers.Add($"{owner}.{Original(f.Name)}");
            foreach (PropertyInfo p in type.GetProperties(Everything).Where(p => IsBound(p.PropertyType)))
                carriers.Add($"{owner}.{p.Name}");
            foreach (MethodBase m in type.GetMethods(Everything).Cast<MethodBase>().Concat(type.GetConstructors(Everything)))
            {
                bool returns = m is MethodInfo mi && IsBound(mi.ReturnType);
                if (returns || m.GetParameters().Any(p => IsBound(p.ParameterType)))
                    carriers.Add($"{owner}.{Original(m.Name)}");
            }
        }
        return [.. carriers];
    }

    private static bool IsBound(Type t) =>
        t == typeof(AtLeastFigures) || (Nullable.GetUnderlyingType(t) == typeof(AtLeastFigures)) || (t.IsByRef && IsBound(t.GetElementType()!));

    /// <summary>The method that wrote a compiler-made name (a property accessor, a lambda, a backing field), as the corpus names it.</summary>
    private static string Original(string name)
    {
        Match generated = Regex.Match(name, @"^<(?<name>[^>]+)>");
        if (generated.Success && generated.Groups["name"].Value.Length > 0)
            return generated.Groups["name"].Value;
        return Regex.Replace(name, @"^(get|set)_", "");
    }

    private static string Outermost(Type t)
    {
        while (t.DeclaringType is not null)
            t = t.DeclaringType;
        return t.FullName!;
    }

    /// <summary>
    /// The methods of the assembly whose IL holds one of the opcodes followed by the metadata token. A byte pattern, not a decode: a
    /// token that appears by chance after the opcode byte would name a caller that is not one, which the Fact would report, and 32 bits
    /// followed by an opcode make that a theory, not a risk.
    /// </summary>
    private static string[] Callers(Assembly assembly, byte[] opcodes, int token)
    {
        var callers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Type type in assembly.GetTypes())
        {
            foreach (MethodBase m in type.GetMethods(Everything).Cast<MethodBase>().Concat(type.GetConstructors(Everything)))
            {
                byte[]? il;
                try
                {
                    il = m.GetMethodBody()?.GetILAsByteArray();
                }
                catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
                {
                    continue;
                }
                if (il is null)
                    continue;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    if (Array.IndexOf(opcodes, il[i]) >= 0 && BitConverter.ToInt32(il, i + 1) == token)
                    {
                        callers.Add($"{Outermost(type)}.{Original(m.Name)}");
                        break;
                    }
                }
            }
        }
        return [.. callers];
    }

    // ---- One guard for both surfaces ----

    /// <summary>
    /// The tree asks the store for a bound only under the guard the stopped list's marks use (ScanResult.MarksAtLeast, SL-113, review m3),
    /// so a completed scan's tree never does, whatever its store holds. The wiring is in the App's window and its row, which no test
    /// project compiles, so it is held here as text: every FolderTreeSession the window builds is given the guard, and the row's
    /// only read of Unfinished sits behind the session's answer.
    /// </summary>
    [Fact]
    public void TheTreeAsksTheStoreForABoundOnlyUnderTheListsGuard()
    {
        string app = Path.Combine(FailCriticalErrorsTests.FindRepo(), @"src\FsAnalyser.App");
        string window = StripComments(File.ReadAllText(Path.Combine(app, "MainWindow.xaml.cs")));
        MatchCollection sessions = Regex.Matches(window, @"new\s+FolderTreeSession\s*\(([^;]*)\)\s*;");
        Assert.Single(sessions);
        Assert.Matches(@"^\s*result\.Store\s*,\s*result\.Unreadable\s*,\s*result\.MarksAtLeast\s*$", sessions[0].Groups[1].Value);
        Assert.Contains("result?.StoppedTopLevel()", window);
        Assert.DoesNotContain("result?.TopLevel", window.Replace("result?.TopLevel\u200b", ""));

        string node = StripComments(File.ReadAllText(Path.Combine(app, "FolderTreeNode.cs")));
        MatchCollection reads = Regex.Matches(node, @"[\w.]*\.Unfinished\s*\(");
        Assert.Single(reads);
        Assert.Matches(@"_session\.ReadsBounds\s*&&\s*_session\.Store\s+is\s*\{\s*\}\s*store\s*&&\s*store\.Unfinished\s*\(", node);
    }

    private static string StripComments(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*[\s\S]*?\*/", ""), @"//[^\r\n]*", "");
    // ---- The 'before' ----

    /// <summary>
    /// Attempt 1's text rule against each spelling the corpus lists, as it was measured: it fired on the six spellings it names and let
    /// the other twelve through, every one of which hands a number to the wording. Each of those rows names an argument type, which
    /// NoComposerTakesAnythingButTheBoundAndNothingConvertsToIt refuses whatever it is spelled as. The correct form in the rule's own
    /// terms (the bound's size, a long) is a row too: the type does not allow it.
    /// </summary>
    [Fact]
    public void TheOldTextRuleLetTheseSpellingsThroughAndTheirTypesAreRefused()
    {
        JsonElement legacy = Corpus.GetProperty("legacy");
        Regex[] patterns = [.. legacy.GetProperty("patterns").EnumerateArray().Select(e => new Regex(e.GetString()!))];
        var arguments = new HashSet<string>(Corpus.GetProperty("arguments").EnumerateArray().Select(a => a.GetProperty("id").GetString()!));
        int fired = 0, letThrough = 0;
        foreach (JsonElement row in legacy.GetProperty("rows").EnumerateArray())
        {
            string id = row.GetProperty("id").GetString()!, text = row.GetProperty("text").GetString()!;
            Assert.Contains(row.GetProperty("argument").GetString()!, arguments);
            bool didFire = patterns.Any(p => p.IsMatch(text));
            Assert.True(didFire == row.GetProperty("fired").GetBoolean(), $"{id} ({text}): the old rule {(didFire ? "fired" : "was quiet")}, the corpus says otherwise");
            if (didFire)
                fired++;
            else
                letThrough++;
        }
        Assert.Equal((6, 12), (fired, letThrough));
    }
}
