using System.IO;
using System.Reflection;
using System.Xml.Linq;

namespace Kytto.App.Tests;

public sealed class ReleaseMetadataTests
{
    private const string CurrentRelease = "1.0.6.3";

    [Fact]
    public void AppAssemblyAndProjectCarryTheCurrentReleaseVersion()
    {
        var assembly = typeof(Kytto.App.App).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.NotNull(informational);
        Assert.Equal(CurrentRelease, informational.Split('+', 2)[0]);
        Assert.Equal(new Version(1, 0, 6, 3), assembly.GetName().Version);

        var project = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", "Kytto.App", "Kytto.App.csproj"));
        Assert.Equal(CurrentRelease, project.Descendants("Version").Single().Value);
    }

    [Fact]
    public void ReleaseNotesAndPackagingUseTheProjectVersionAsTheirSingleSource()
    {
        var root = RepositoryRoot();
        var releaseNotes = Path.Combine(root, "docs", "releases", $"{CurrentRelease}.md");
        Assert.True(File.Exists(releaseNotes), $"Missing release notes: {releaseNotes}");

        var script = File.ReadAllText(Path.Combine(root, "scripts", "release.ps1"));
        var installer = File.ReadAllText(Path.Combine(root, "scripts", "installer.iss"));

        Assert.Contains("$projectXml.Project.PropertyGroup.Version", script, StringComparison.Ordinal);
        Assert.Contains("/DAppVersionSlug=$version", script, StringComparison.Ordinal);
        Assert.Contains("AppVersion={#AppVersion}", installer, StringComparison.Ordinal);
        Assert.DoesNotContain(CurrentRelease, installer, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "KyttoMCP.slnx")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate KyttoMCP.slnx above {AppContext.BaseDirectory}.");
    }
}
