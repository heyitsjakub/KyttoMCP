using System.Xml.Linq;
using Microsoft.Win32;

namespace Kytto.Core.Clients;

/// <summary>
/// An installed Store (MSIX) application, found by package family name.
/// </summary>
/// <remarks>
/// <para>
/// The Windows analogue of a macOS bundle identifier: a stable name for installed
/// software that does not change when the app updates or moves. Codex ships this
/// way, which is why looking for an executable at a fixed path missed it entirely.
/// </para>
/// <para>
/// Found through the registry rather than through WinRT's <c>PackageManager</c>,
/// which would mean targeting a Windows SDK version for one lookup — and rather
/// than by listing <c>WindowsApps</c>, which is ACL-denied to ordinary processes.
/// Reading a <em>known</em> path under it is allowed, and the registry is what
/// turns a family name into one.
/// </para>
/// </remarks>
public sealed record MsixPackage(string FullName, string RootFolder)
{
    private const string Repository =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>
    /// The installed package for a family name, or null. Highest version wins when
    /// an update has left more than one registered.
    /// </summary>
    public static MsixPackage? Find(string familyName)
    {
        // `Name_PublisherId` addresses a family; a full name is
        // `Name_Version_Arch__PublisherId`, so both halves have to match.
        var separator = familyName.LastIndexOf('_');
        if (separator <= 0) return null;
        var name = familyName[..separator];
        var publisher = familyName[(separator + 1)..];

        try
        {
            using var repository = Registry.CurrentUser.OpenSubKey(Repository);
            if (repository is null) return null;

            MsixPackage? best = null;
            var bestVersion = new Version(0, 0);

            foreach (var fullName in repository.GetSubKeyNames())
            {
                if (!fullName.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)) continue;
                if (!fullName.EndsWith("__" + publisher, StringComparison.OrdinalIgnoreCase)) continue;

                using var entry = repository.OpenSubKey(fullName);
                if (entry?.GetValue("PackageRootFolder") is not string root) continue;
                if (!Directory.Exists(root)) continue;

                var version = VersionOf(fullName);
                if (best is not null && version <= bestVersion) continue;

                best = new MsixPackage(fullName, root);
                bestVersion = version;
            }
            return best;
        }
        catch (Exception error) when (
            error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static Version VersionOf(string fullName)
    {
        var parts = fullName.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var version)
            ? version
            : new Version(0, 0);
    }

    /// <summary>What the package says its icon and tile colour are.</summary>
    /// <param name="LogoPath">The largest variant of the declared square logo that is on disk.</param>
    /// <param name="BackgroundColor">
    /// The tile colour the glyph is meant to sit on. Store logos are commonly a
    /// white glyph on transparency, so without this they are invisible.
    /// </param>
    public sealed record Branding(string? LogoPath, string? BackgroundColor);

    public Branding ReadBranding()
    {
        try
        {
            var manifest = Path.Combine(RootFolder, "AppxManifest.xml");
            var document = XDocument.Load(manifest);

            var visual = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "VisualElements");
            if (visual is null) return new Branding(null, null);

            var declared = visual.Attribute("Square44x44Logo")?.Value
                ?? visual.Attribute("Square150x150Logo")?.Value;
            var background = visual.Attribute("BackgroundColor")?.Value;

            return new Branding(declared is null ? null : BestVariant(declared), background);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new Branding(null, null);
        }
    }

    /// <summary>
    /// The largest file Windows would have picked for the declared logo.
    /// </summary>
    /// <remarks>
    /// A manifest names <c>assets\Square44x44Logo.png</c> and ships a dozen scaled
    /// and target-sized variants beside it. Windows resolves those at runtime; here
    /// the biggest wins, because the matrix draws at 4× and an upscaled 44px logo
    /// looks like exactly that. The plated variants are preferred over
    /// <c>altform-unplated</c>, which is drawn without its background colour.
    /// </remarks>
    private string? BestVariant(string declared)
    {
        var relative = declared.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.Combine(RootFolder, relative);
        var directory = Path.GetDirectoryName(full);
        var stem = Path.GetFileNameWithoutExtension(full);
        var extension = Path.GetExtension(full);
        if (directory is null) return null;

        string[] candidates =
        [
            $"{stem}.targetsize-256{extension}",
            $"{stem}.scale-400{extension}",
            $"{stem}.scale-200{extension}",
            $"{stem}.targetsize-96{extension}",
            $"{stem}.scale-150{extension}",
            $"{stem}{extension}",
        ];

        foreach (var candidate in candidates)
        {
            var path = Path.Combine(directory, candidate);
            // Existence is checked file by file on purpose: the directory cannot be
            // listed, but a named file under it can be read.
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
