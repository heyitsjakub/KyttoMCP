using System.Drawing;
using System.IO;
using Kytto.App.Web;
using Kytto.Core.Clients;

namespace Kytto.App.Tests;

/// <summary>
/// The rule: every client's icon comes out the same size.
/// </summary>
/// <remarks>
/// <para>
/// A column of icons that disagree about how big an icon is reads as a rendering
/// bug, and being scannable in one glance is the entire reason they are drawn
/// rather than named.
/// </para>
/// <para>
/// It is easy to break without noticing, because the sources disagree about what
/// "the icon" includes: a Store logo may be a small glyph in a large transparent
/// canvas, an executable's icon fills its own, and Kytto draws its own tiles.
/// These pin the result rather than the method.
/// </para>
/// </remarks>
public class ClientIconTests
{
    private const int Side = 64;

    /// <summary>How much of the square the artwork is allowed to differ by.</summary>
    /// <remarks>
    /// Not exact equality: a non-square logo is fitted by its longer side, so its
    /// shorter one is legitimately smaller. This catches the failure that matters —
    /// one client's icon coming out visibly smaller than the rest.
    /// </remarks>
    private const int Tolerance = 6;

    private static Bitmap Render(ClientId client)
    {
        var png = ClientIcons.Png(client.Raw());
        Assert.True(png is not null, $"{client.Raw()} produced no icon at all");
        using var buffer = new MemoryStream(png!);
        return new Bitmap(buffer);
    }

    [Fact]
    public void EveryClientProducesAnIcon()
    {
        foreach (var client in ClientIds.All)
        {
            using var icon = Render(client);
            Assert.Equal(Side, icon.Width);
            Assert.Equal(Side, icon.Height);
        }
    }

    /// <summary>
    /// The one that actually catches the bug: not the canvas, which is trivially
    /// equal, but how much of it the artwork fills.
    /// </summary>
    [Fact]
    public void EveryClientsArtworkIsTheSameSize()
    {
        var widths = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var client in ClientIds.All)
        {
            using var icon = Render(client);
            var ink = InkBounds(icon);
            widths[client.Raw()] = (int)Math.Max(ink.Width, ink.Height);
        }

        var smallest = widths.MinBy(entry => entry.Value);
        var largest = widths.MaxBy(entry => entry.Value);

        Assert.True(
            largest.Value - smallest.Value <= Tolerance,
            $"icons differ in size: {smallest.Key} is {smallest.Value}px but " +
            $"{largest.Key} is {largest.Value}px " +
            $"({string.Join(", ", widths.Select(entry => $"{entry.Key}={entry.Value}"))})");
    }

    /// <summary>
    /// A glyph that sits off-centre in its source canvas must not sit off-centre in
    /// the column.
    /// </summary>
    [Fact]
    public void EveryClientsArtworkIsCentred()
    {
        foreach (var client in ClientIds.All)
        {
            using var icon = Render(client);
            var ink = InkBounds(icon);

            var horizontal = Math.Abs((ink.Left + ink.Right) / 2 - Side / 2.0);
            var vertical = Math.Abs((ink.Top + ink.Bottom) / 2 - Side / 2.0);

            Assert.True(horizontal <= 2, $"{client.Raw()} is {horizontal:0.#}px off-centre horizontally");
            Assert.True(vertical <= 2, $"{client.Raw()} is {vertical:0.#}px off-centre vertically");
        }
    }

    [Fact]
    public void AnUnknownClientIdIsNotAnIcon() => Assert.Null(ClientIcons.Png("notAClient"));

    [Fact]
    public void LoadedPackageArtworkOwnsItsPixelsAfterTheSourceIsGone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kytto-icon-{Guid.NewGuid():N}.png");
        try
        {
            using (var source = new Bitmap(8, 8))
            {
                source.SetPixel(3, 4, Color.CornflowerBlue);
                source.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }

            using var loaded = ClientIcons.LoadUnlocked(path);
            Assert.NotNull(loaded);
            File.Delete(path);

            // Saving forces GDI+ to read every pixel after LoadUnlocked's stream
            // has been disposed. A bitmap still backed by that stream can fail here.
            using var output = new MemoryStream();
            loaded.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            Assert.NotEmpty(output.ToArray());
            Assert.Equal(Color.CornflowerBlue.ToArgb(), loaded.GetPixel(3, 4).ToArgb());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static RectangleF InkBounds(Bitmap image)
    {
        int minX = image.Width, minY = image.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image.GetPixel(x, y).A <= 16) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return new RectangleF(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
