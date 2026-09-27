using System.Reflection;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// Every Trait in this project gates an opt-in test: one that signs in to a network address, runs an SMB
/// server, or registers a real cloud sync root. Each must be left out of the default run by a clause of the
/// project's default filter, or every `dotnet test` would do what only a person may ask for (SL-049). No stage
/// runs the opt-in filters by itself, so this default-suite check is what holds the rule.
/// </summary>
/// <remarks>
/// The filter checked is the csproj's FsaDefaultTestFilter, which the build bakes in as assembly metadata. It
/// is never the filter of the run: an explicit --filter reaches MSBuild as a global VSTestTestCaseFilter, and a
/// check of that would fail every targeted run (SL-049, M1).
/// </remarks>
public class OptInTraitTests
{
    /// <summary>The Traits this project had when the guard was written, so a reflection that finds none fails.</summary>
    private static readonly (string Name, string Value)[] Known =
        [("Network", "TEST-NET-1"), ("Network", "SMB-LOOPBACK"), ("Cloud", "CF-SYNC-ROOT")];

    [Fact]
    public void EveryOptInTraitIsExcludedByTheDefaultFilter()
    {
        Assembly tests = typeof(OptInTraitTests).Assembly;
        string? filter = tests.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "DefaultTestFilter")?.Value;
        Assert.True(filter is not null,
            "No AssemblyMetadata DefaultTestFilter: FsAnalyser.Core.Tests.csproj bakes $(FsaDefaultTestFilter), the default VSTestTestCaseFilter, into the test assembly.");

        List<(string Name, string Value)> traits = TraitsOf(tests);
        foreach ((string Name, string Value) known in Known)
            Assert.Contains(known, traits);
        List<string> unfiltered = Unfiltered(traits, filter!);
        Assert.True(unfiltered.Count == 0,
            $"The default filter '{filter}' runs these opt-in Traits in every run that names no filter: {string.Join(", ", unfiltered)}. " +
            "Add a Name!=Value clause for each to FsaDefaultTestFilter in FsAnalyser.Core.Tests.csproj.");
    }

    /// <summary>The guard's failure branch: filters that leave a Trait in, which it must catch.</summary>
    [Theory]
    [InlineData("Network!=TEST-NET-1&Network!=SMB-LOOPBACK&Cloud!=CF-SYNC-ROOT", "")]
    [InlineData("Network!=TEST-NET-1&Network!=SMB-LOOPBACK", "Cloud=CF-SYNC-ROOT")] // a clause missing
    [InlineData("Network!=TEST-NET-1&Network!=SMB-LOOPBACK&Cloud!=CF-SYNC-ROOT2", "Cloud=CF-SYNC-ROOT")] // a longer value
    [InlineData("Network!=TEST-NET-1&Network!=SMB-LOOPBACK&Cloud!=CF-SYNC", "Cloud=CF-SYNC-ROOT")] // a shorter value
    [InlineData("Network!=TEST-NET-1|Network!=SMB-LOOPBACK|Cloud!=CF-SYNC-ROOT", "Network=TEST-NET-1,Network=SMB-LOOPBACK,Cloud=CF-SYNC-ROOT")] // OR: true for every test
    [InlineData("(Network!=TEST-NET-1&Network!=SMB-LOOPBACK&Cloud!=CF-SYNC-ROOT)", "Network=TEST-NET-1,Network=SMB-LOOPBACK,Cloud=CF-SYNC-ROOT")]
    [InlineData("Cloud=CF-SYNC-ROOT", "Network=TEST-NET-1,Network=SMB-LOOPBACK,Cloud=CF-SYNC-ROOT")] // an opt-in run's own filter
    [InlineData("FullyQualifiedName~ArchitectureRulesTests", "Network=TEST-NET-1,Network=SMB-LOOPBACK,Cloud=CF-SYNC-ROOT")]
    [InlineData("", "Network=TEST-NET-1,Network=SMB-LOOPBACK,Cloud=CF-SYNC-ROOT")]
    public void AFilterThatLeavesATraitInIsCaught(string filter, string expected)
    {
        Assert.Equal(expected, string.Join(",", Unfiltered(Known, filter)));
    }

    /// <summary>
    /// The Traits, as Name=Value, that <paramref name="filter"/> does not leave out. A filter leaves a Trait out
    /// only as one of its '&amp;'-joined clauses written exactly Name!=Value. A filter with '|' or a parenthesis is
    /// refused whole, since an OR can make every clause true for every test.
    /// </summary>
    internal static List<string> Unfiltered(IEnumerable<(string Name, string Value)> traits, string filter)
    {
        bool readable = filter.Length > 0 && filter.IndexOfAny(['|', '(', ')']) < 0;
        HashSet<string> clauses = readable ? filter.Split('&').ToHashSet(StringComparer.Ordinal) : [];
        return traits
            .Where(t => !clauses.Contains($"{t.Name}!={t.Value}"))
            .Select(t => $"{t.Name}={t.Value}")
            .ToList();
    }

    /// <summary>Every [Trait(name, value)] on a class or a method of <paramref name="assembly"/>, once each.</summary>
    internal static List<(string Name, string Value)> TraitsOf(Assembly assembly)
    {
        const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var traits = new List<(string Name, string Value)>();
        foreach (Type type in assembly.GetTypes())
        {
            IEnumerable<CustomAttributeData> attributes = type.GetCustomAttributesData()
                .Concat(type.GetMethods(Declared).SelectMany(m => m.GetCustomAttributesData()));
            foreach (CustomAttributeData attribute in attributes)
            {
                if (!typeof(TraitAttribute).IsAssignableFrom(attribute.AttributeType)
                        || attribute.ConstructorArguments is not [{ Value: string name }, { Value: string value }])
                    continue;
                if (!traits.Contains((name, value)))
                    traits.Add((name, value));
            }
        }
        return traits;
    }
}
