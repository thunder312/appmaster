using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Appmaster.Views;

/// <summary>
/// Das Appmaster-Logo im Eulenwerk-App-Stil: dunkle Kachel (48er-Raster, rx 12), Linienzeichnung eines Gehirns
/// (Lucide „brain“, MIT) mit Strich 2,6 in der Kategoriefarbe Administration, Mittelnaht und Windung in der hellen Variante.
/// Die Kachel bleibt in beiden Themes gleich.
/// </summary>
public sealed class AppLogo : Border
{
    public const string Kachel = "#1B1F20";
    public const string Akzent = "#2BC4D9";
    public const string Hell = "#7FDDEA";

    // Lucide-Raster 24 → Kachel 48: Skalierung 1,25, zentriert (Verschiebung 9)
    private const string Umriss =
        "M17.598 6.5A3 3 0 1 0 12 5a3 3 0 1 0-5.598 1.5 " +
        "M17.997 5.125a4 4 0 0 1 2.526 5.77 M18 18a4 4 0 0 0 2-7.464 " +
        "M19.967 17.483A4 4 0 1 1 12 18a4 4 0 1 1-7.967-.517 " +
        "M6 18a4 4 0 0 1-2-7.464 M6.003 5.125a4 4 0 0 0-2.526 5.77";
    private const string Details = "M12 18V5 M15 13a4.5 4.5 0 0 1-3-4 4.5 4.5 0 0 1-3 4";

    public AppLogo()
    {
        CornerRadius = new CornerRadius(12);
        Background = Pinsel(Kachel);
        SnapsToDevicePixels = true;

        var leinwand = new Canvas { Width = 48, Height = 48 };
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(1.25, 1.25));
        transform.Children.Add(new TranslateTransform(9, 9));
        var gruppe = new Canvas { RenderTransform = transform };
        gruppe.Children.Add(Linie(Umriss, Akzent));
        gruppe.Children.Add(Linie(Details, Hell));
        leinwand.Children.Add(gruppe);
        Child = new Viewbox { Child = leinwand, Stretch = Stretch.Uniform };
    }

    private static Brush Pinsel(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    private static Path Linie(string daten, string farbe) => new()
    {
        Data = Geometry.Parse(daten),
        Stroke = Pinsel(farbe),
        StrokeThickness = 2.6 / 1.25,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    /// <summary>Rendert das Logo als Bild (für Fenstersymbol und die .ico-Datei).</summary>
    public static System.Windows.Media.Imaging.BitmapSource Rendern(int groesse)
    {
        var logo = new AppLogo { Width = groesse, Height = groesse };
        logo.Measure(new Size(groesse, groesse));
        logo.Arrange(new Rect(0, 0, groesse, groesse));
        logo.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(groesse, groesse, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(logo);
        bmp.Freeze();
        return bmp;
    }
}
