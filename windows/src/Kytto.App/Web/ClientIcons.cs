using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Kytto.Core.Clients;
using Kytto.Core.Settings;

namespace Kytto.App.Web;

/// <summary>
/// A client's real application icon, as a PNG.
/// </summary>
/// <remarks>
/// <para>
/// You recognise Cursor by its icon faster than by reading the word "Cursor",
/// which is the whole argument for spending native code on this: the matrix has
/// one column per client and their names do not fit in one.
/// </para>
/// <para>
/// The web layer asks for an opaque client id and gets a picture back. It never
/// learns that an icon comes from an executable or a Store package, or that
/// software has icons at all (§3.2).
/// </para>
/// <para>
/// Nothing here ships a copy of anyone's logo. Every icon is read from software
/// already installed on this machine, which is the same thing macOS does through
/// <c>NSWorkspace</c> — and the reason a client that is not installed gets a drawn
/// placeholder rather than a bundled brand asset.
/// </para>
/// </remarks>
internal static class ClientIcons
{
    /// <summary>
    /// Drawn at nearly 3× the 22px the matrix shows them at, so they stay sharp on a
    /// high-DPI display and when the row height changes.
    /// </summary>
    private const int Side = 64;

    /// <summary>
    /// The square every client's icon occupies, as a fraction of the side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The rule: every client's icon takes up the same room.</strong> A
    /// column of icons that disagree about how big an icon is reads as a rendering
    /// bug, and being scannable in one glance is the whole reason they are drawn
    /// rather than named.
    /// </para>
    /// <para>
    /// Holding the rule takes two constants, because the sources present
    /// differently and both presentations are legitimate. Codex declares a tile
    /// colour and ships a white glyph to sit on it; Claude Desktop declares
    /// <c>transparent</c> and ships finished artwork; VS Code has no manifest at
    /// all, just an icon in its executable. What has to match is the <em>outer
    /// footprint</em> — so a plate fills it, and artwork with no plate fills it too.
    /// </para>
    /// <para>
    /// Sources also pad differently inside their own canvas — Claude's logo is 76px
    /// of ink in an 88px square, Codex's fills all 88 — so everything is trimmed to
    /// its ink before being fitted, or the padding becomes a size difference.
    /// </para>
    /// </remarks>
    private const double Footprint = 0.90;

    /// <summary>
    /// A glyph drawn <em>on</em> a plate, which has to be smaller than the plate
    /// carrying it.
    /// </summary>
    private const double GlyphOnPlate = 0.62;

    /// <summary>The corner radius of the plate, as a fraction of the side.</summary>
    private const double PlateRadius = 0.22;

    private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    internal static byte[]? Png(string rawClientId)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(rawClientId, out var cached)) return cached;
        }

        if (ClientIds.FromRaw(rawClientId) is not { } clientId) return null;
        var descriptor = ClientRegistry.Descriptor(clientId);

        byte[] png;
        try
        {
            using var image = Render(descriptor);
            using var buffer = new MemoryStream();
            image.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
            png = buffer.ToArray();
        }
        catch (Exception error) when (error is IOException or ArgumentException
                                           or UnauthorizedAccessException or ExternalException
                                           or System.ComponentModel.Win32Exception or OutOfMemoryException)
        {
            return null;
        }

        lock (Gate)
        {
            Cache[rawClientId] = png;
        }
        return png;
    }

    /// <summary>
    /// Four outcomes, in the order that gives the truest picture.
    /// </summary>
    /// <remarks>
    /// A Store package carries proper branding, so it is asked first. An ordinary
    /// installer has an icon in its executable. A client that is only a command
    /// line tool never had one — but it <em>is</em> installed, and drawing it the
    /// same grey as an uninstalled client would be a lie. Grey is reserved for the
    /// last case: a config that outlived the software that wrote it.
    /// </remarks>
    private static Bitmap Render(ClientDescriptor descriptor)
    {
        foreach (var family in descriptor.PackageFamilyNames)
        {
            if (MsixPackage.Find(family) is not { } package) continue;
            if (FromPackage(package) is { } branded) return branded;
        }

        if (InstalledExecutable(descriptor) is { } executable)
        {
            if (FromExecutable(executable) is { } extracted) return extracted;
        }

        return IsCommandLineTool(descriptor)
            ? Tile(descriptor, Color.FromArgb(0x22, 0x22, 0x22))
            : Tile(descriptor, Color.FromArgb(0x60, 0x60, 0x60));
    }

    /// <summary>
    /// A Store app's own logo, on the tile colour it declares.
    /// </summary>
    /// <remarks>
    /// The declared logo is commonly a white glyph on transparency, meant to be
    /// composited onto <c>BackgroundColor</c>. Drawn as-is it is invisible on a
    /// light background, which is exactly what it looked like before this existed.
    /// </remarks>
    private static Bitmap? FromPackage(MsixPackage package)
    {
        var branding = package.ReadBranding();
        if (branding.LogoPath is not { } logoPath) return null;

        using var logo = LoadUnlocked(logoPath);
        if (logo is null) return null;

        var result = new Bitmap(Side, Side);
        using var graphics = Prepare(result);

        // A declared tile colour means the logo is a glyph meant to sit on it; a
        // manifest that says `transparent` — as Claude Desktop's does — means the
        // artwork is already finished and is the icon itself.
        if (ParseColor(branding.BackgroundColor) is { } fill)
        {
            using var plate = Plate();
            using var brush = new SolidBrush(fill);
            graphics.FillPath(brush, plate);
            DrawFitted(graphics, logo, GlyphOnPlate);
        }
        else
        {
            DrawFitted(graphics, logo, Footprint);
        }
        return result;
    }

    /// <summary>The best icon in an executable's resources.</summary>
    /// <remarks>
    /// <c>PrivateExtractIcons</c> rather than <c>Icon.ExtractAssociatedIcon</c>,
    /// which only ever returns 32×32 — upscaling that to 64 is visibly soft next to
    /// a Store logo read at its native size.
    /// </remarks>
    private static Bitmap? FromExecutable(string executable)
    {
        var handles = new IntPtr[1];
        var ids = new int[1];
        var extracted = PrivateExtractIconsW(executable, 0, Side, Side, handles, ids, 1, 0);
        if (extracted == 0 || handles[0] == IntPtr.Zero)
        {
            // Some executables only carry small icons; take whatever is there.
            using var associated = Icon.ExtractAssociatedIcon(executable);
            return associated is null ? null : Scale(associated);
        }

        try
        {
            using var icon = Icon.FromHandle(handles[0]);
            return Scale(icon);
        }
        finally
        {
            DestroyIcon(handles[0]);
        }
    }

    /// <summary>
    /// Reads a file without holding it open.
    /// </summary>
    /// <remarks>
    /// <c>Image.FromFile</c> keeps a lock on the file for the lifetime of the
    /// bitmap, and these live under <c>WindowsApps</c>, where an open handle would
    /// block the package from updating.
    /// </remarks>
    internal static Bitmap? LoadUnlocked(string path)
    {
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var loaded = new Bitmap(stream);
            // GDI+ requires an image's source stream to remain open for the image's
            // whole lifetime. Clone while it is open so the returned bitmap owns
            // its pixels and neither the file nor an in-memory stream is retained.
            return new Bitmap(loaded);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static Color? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // Manifests also allow the literal "transparent", which means no plate.
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return null;

        var text = value.TrimStart('#');
        if (text.Length != 6) return null;
        return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? Color.FromArgb(0xFF, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF)
            : null;
    }

    private static bool IsCommandLineTool(ClientDescriptor descriptor) =>
        descriptor.InstallKeys.Count == 0 && descriptor.PackageFamilyNames.Count == 0;

    private static string? InstalledExecutable(ClientDescriptor descriptor)
    {
        var home = KyttoPaths.Home;
        foreach (var key in descriptor.InstallKeys)
        {
            try
            {
                var path = new PlatformPath("", key).Resolve(home);
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException)
            {
                // A malformed registry entry, not a missing application.
            }
        }
        return null;
    }

    private static Bitmap Scale(Icon icon)
    {
        var result = new Bitmap(Side, Side);
        using var graphics = Prepare(result);
        // An executable's icon is finished artwork with no plate behind it, so it
        // fills the footprint the same way a plate would.
        using var source = icon.ToBitmap();
        DrawFitted(graphics, source, Footprint);
        return result;
    }

    private static Graphics Prepare(Bitmap target)
    {
        var graphics = Graphics.FromImage(target);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        return graphics;
    }

    /// <summary>The rounded square a plated icon fills — the shared footprint.</summary>
    private static GraphicsPath Plate()
    {
        var size = (float)(Side * Footprint);
        var inset = (Side - size) / 2;
        return RoundedRectangle(inset, inset, size, size, (float)(Side * PlateRadius));
    }

    /// <summary>
    /// Draws artwork centred in the content box, trimmed of whatever transparent
    /// margin its author left around it and scaled to the same size as every other
    /// client's.
    /// </summary>
    /// <remarks>
    /// Aspect ratio is kept. A logo that is not square — Codex's is 88×86 — is
    /// fitted by its longer side, so squaring it up would visibly squash it.
    /// </remarks>
    private static void DrawFitted(Graphics graphics, Bitmap artwork, double fill)
    {
        var ink = InkBounds(artwork);
        var box = (float)(Side * fill);

        var scale = Math.Min(box / ink.Width, box / ink.Height);
        var width = ink.Width * scale;
        var height = ink.Height * scale;

        graphics.DrawImage(
            artwork,
            new RectangleF((Side - width) / 2, (Side - height) / 2, width, height),
            ink,
            GraphicsUnit.Pixel);
    }

    /// <summary>The bounding box of everything that is not transparent.</summary>
    /// <remarks>
    /// Vendors pad their logos differently — Claude Desktop leaves a margin, Codex
    /// does not — and without this the padding becomes a size difference in a
    /// column that is meant to be scannable. Alpha above a low threshold rather
    /// than any alpha at all, so an anti-aliased edge does not count as ink.
    /// </remarks>
    private static RectangleF InkBounds(Bitmap artwork)
    {
        const int Threshold = 16;
        int minX = artwork.Width, minY = artwork.Height, maxX = -1, maxY = -1;

        for (var y = 0; y < artwork.Height; y++)
        {
            for (var x = 0; x < artwork.Width; x++)
            {
                if (artwork.GetPixel(x, y).A <= Threshold) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        // A fully transparent image has no ink to centre; use the whole canvas
        // rather than dividing by zero.
        return maxX < minX || maxY < minY
            ? new RectangleF(0, 0, artwork.Width, artwork.Height)
            : new RectangleF(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>A rounded tile with the client's initials, drawn to match the row.</summary>
    private static Bitmap Tile(ClientDescriptor descriptor, Color background)
    {
        var result = new Bitmap(Side, Side);
        using var graphics = Prepare(result);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using (var plate = Plate())
        using (var brush = new SolidBrush(background))
        {
            graphics.FillPath(brush, plate);
        }

        // Sized off the same content box the artwork uses, so two initials and a
        // logo carry the same weight in the column.
        using var font = new Font("Segoe UI", (float)(Side * GlyphOnPlate * 0.55), FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.FromArgb(0xE8, 0xE8, 0xE8));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        graphics.DrawString(Initials(descriptor.DisplayName), font, text,
            new RectangleF(0, 0, Side, Side), format);

        return result;
    }

    private static string Initials(string displayName)
    {
        var words = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2
            ? $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}"
            : displayName[..Math.Min(2, displayName.Length)].ToUpperInvariant();
    }

    private static GraphicsPath RoundedRectangle(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int PrivateExtractIconsW(
        string file, int index, int width, int height,
        IntPtr[] icons, int[] ids, int count, int flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
