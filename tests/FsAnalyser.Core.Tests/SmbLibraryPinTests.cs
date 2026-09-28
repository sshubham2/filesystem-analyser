using System.Xml.Linq;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The SMB rig (Rig/SmbRig.cs) is compiled into two projects, this one and tests/window/StallRig, and calls a private
/// overload of SMBServer.Start by reflection with an exact parameter list. A package bumped in one of them only
/// compiles and then fails at run time in that one, in the opt-in network cases that run least often (HF-013). So
/// SMBLibrary and SMBLibrary.Win32 are pinned once, as properties in Directory.Build.props, and every project under
/// tests that references either takes its version from there.
/// </summary>
public class SmbLibraryPinTests
{
    private static readonly (string Package, string Property)[] Pins =
        [("SMBLibrary", "FsaSmbLibraryVersion"), ("SMBLibrary.Win32", "FsaSmbLibraryWin32Version")];

    [Fact]
    public void EveryProjectThatBuildsTheRigTakesSmbLibraryFromOnePin()
    {
        string repo = FindRepo();
        XDocument props = XDocument.Load(Path.Combine(repo, "Directory.Build.props"));
        foreach ((_, string property) in Pins)
        {
            string[] values = [.. props.Descendants(property).Select(e => e.Value.Trim())];
            Assert.True(values is [{ Length: > 0 }], $"Directory.Build.props defines {property} {values.Length} times; it is pinned there once.");
        }

        var referencing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string project in Directory.EnumerateFiles(Path.Combine(repo, "tests"), "*.csproj", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(repo, project).Replace('\\', '/');
            foreach ((string package, string property) in Pins)
            {
                // A project's own definition wins over Directory.Build.props, which the SDK imports before its body: the
                // MSBuild way to bump one project only (SL-073 review, R2-m3).
                Assert.True(!XDocument.Load(project).Descendants(property).Any(),
                    $"{relative} redefines {property}; it is pinned once in Directory.Build.props.");
                foreach (XElement reference in XDocument.Load(project).Descendants("PackageReference")
                             .Where(e => string.Equals((string?)e.Attribute("Include"), package, StringComparison.OrdinalIgnoreCase)))
                {
                    referencing.Add(relative);
                    string? version = (string?)reference.Attribute("Version") ?? reference.Element("Version")?.Value;
                    Assert.True(version == $"$({property})",
                        $"{relative} pins {package} at {version ?? "no version"}; it takes $({property}) from Directory.Build.props, as every project that compiles the rig does.");
                }
            }
        }
        Assert.Contains("tests/FsAnalyser.Core.Tests/FsAnalyser.Core.Tests.csproj", referencing);
        Assert.Contains("tests/window/StallRig/StallRig.csproj", referencing);
    }

    private static string FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FsAnalyser.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"No FsAnalyser.slnx above {AppContext.BaseDirectory}.");
    }
}
