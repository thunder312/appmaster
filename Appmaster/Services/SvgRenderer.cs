using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace Appmaster.Services;

/// <summary>
/// Kleiner SVG-Umwandler ohne Fremdbibliothek: zeichnet einfache Vektorgrafiken (so wie das Eulenwerk-Design-System sie nutzt:
/// path, circle, ellipse, rect, line, polyline, polygon, g, Transformationen, fill/stroke, <c>currentColor</c>) mit WPF-Mitteln und
/// liefert ein PNG. Nicht unterstützt (→ null bzw. weggelassen): Verläufe, Text, Bilder, CSS-Klassen, Filter.
/// </summary>
public static class SvgRenderer
{
    private sealed record Stil(Brush? Fill, Brush? Stroke, double Strichstaerke, PenLineCap Kappe, PenLineJoin Ecke, double Deckkraft);

    /// <param name="akzent">Farbe für <c>currentColor</c> (z. B. die Kategoriefarbe der App).</param>
    public static byte[]? ZuPng(string svg, Color akzent, int groesse = 256)
    {
        try
        {
            var einstellungen = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var leser = XmlReader.Create(new StringReader(svg), einstellungen);
            var wurzel = XDocument.Load(leser).Root;
            if (wurzel == null || wurzel.Name.LocalName != "svg") return null;

            var (vx, vy, vb, vh) = Ansichtsfeld(wurzel);
            if (vb <= 0 || vh <= 0) return null;

            var start = new Stil(Brushes.Black, null, 1, PenLineCap.Flat, PenLineJoin.Miter, 1);
            var zeichnung = new DrawingGroup();
            int gezeichnet = 0;
            Durchlaufen(wurzel, Erben(wurzel, start, akzent), akzent, zeichnung, ref gezeichnet);
            if (gezeichnet == 0) return null;

            var visual = new DrawingVisual();
            double faktor = Math.Min(groesse / vb, groesse / vh);
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new TranslateTransform((groesse - vb * faktor) / 2, (groesse - vh * faktor) / 2));
                dc.PushTransform(new ScaleTransform(faktor, faktor));
                dc.PushTransform(new TranslateTransform(-vx, -vy));
                dc.PushClip(new RectangleGeometry(new Rect(vx, vy, vb, vh)));
                dc.DrawDrawing(zeichnung);
                dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
            }
            var bmp = new RenderTargetBitmap(groesse, groesse, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    private static double Zahl(string? s, double standard = 0) =>
        double.TryParse(s?.Trim().TrimEnd('p', 'x', '%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : standard;

    private static List<double> Zahlen(string? s) =>
        Regex.Matches(s ?? "", @"-?\d*\.?\d+(?:[eE][-+]?\d+)?").Select(m => Zahl(m.Value)).ToList();

    private static (double X, double Y, double B, double H) Ansichtsfeld(XElement svg)
    {
        var z = Zahlen((string?)svg.Attribute("viewBox"));
        if (z.Count == 4) return (z[0], z[1], z[2], z[3]);
        return (0, 0, Zahl((string?)svg.Attribute("width"), 24), Zahl((string?)svg.Attribute("height"), 24));
    }

    private static string? Wert(XElement e, string name)
    {
        var stil = (string?)e.Attribute("style");
        if (stil != null)
            foreach (var teil in stil.Split(';'))
            {
                var kv = teil.Split(':', 2);
                if (kv.Length == 2 && kv[0].Trim() == name) return kv[1].Trim();
            }
        return (string?)e.Attribute(name);
    }

    private static (bool Ok, Brush? Pinsel) Farbe(string? text, Color akzent)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, null);
        var t = text.Trim();
        if (t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return (true, null);
        if (t.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) return (true, Eingefroren(akzent));
        if (t.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return (true, Eingefroren(akzent));   // Verläufe: Akzentfarbe als Ersatz
        try { return (true, Eingefroren((Color)ColorConverter.ConvertFromString(t))); }
        catch { return (false, null); }
    }

    private static Brush Eingefroren(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static Stil Erben(XElement e, Stil s, Color akzent)
    {
        var fill = s.Fill; var stroke = s.Stroke;
        var f = Farbe(Wert(e, "fill"), akzent); if (f.Ok) fill = f.Pinsel;
        var st = Farbe(Wert(e, "stroke"), akzent); if (st.Ok) stroke = st.Pinsel;
        var breite = Wert(e, "stroke-width") is { } w ? Zahl(w, s.Strichstaerke) : s.Strichstaerke;
        var kappe = Wert(e, "stroke-linecap") switch { "round" => PenLineCap.Round, "square" => PenLineCap.Square, "butt" => PenLineCap.Flat, _ => s.Kappe };
        var ecke = Wert(e, "stroke-linejoin") switch { "round" => PenLineJoin.Round, "bevel" => PenLineJoin.Bevel, "miter" => PenLineJoin.Miter, _ => s.Ecke };
        var deckkraft = Wert(e, "opacity") is { } o ? Zahl(o, 1) : 1;   // gilt nur für das Element selbst (als Gruppendeckkraft)
        return new Stil(fill, stroke, breite, kappe, ecke, deckkraft);
    }

    private static Transform? Verwandlung(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var gruppe = new TransformGroup();
        // SVG wendet die Liste von rechts nach links auf die Punkte an; WPF-TransformGroup von links nach rechts → Reihenfolge umkehren.
        foreach (Match m in Regex.Matches(text, @"(\w+)\s*\(([^)]*)\)").Reverse())
        {
            var a = Zahlen(m.Groups[2].Value);
            switch (m.Groups[1].Value)
            {
                case "translate": gruppe.Children.Add(new TranslateTransform(a.ElementAtOrDefault(0), a.ElementAtOrDefault(1))); break;
                case "scale": gruppe.Children.Add(new ScaleTransform(a.ElementAtOrDefault(0), a.Count > 1 ? a[1] : a.ElementAtOrDefault(0))); break;
                case "rotate": gruppe.Children.Add(new RotateTransform(a.ElementAtOrDefault(0), a.ElementAtOrDefault(1), a.ElementAtOrDefault(2))); break;
                case "matrix" when a.Count == 6: gruppe.Children.Add(new MatrixTransform(a[0], a[1], a[2], a[3], a[4], a[5])); break;
            }
        }
        return gruppe.Children.Count == 0 ? null : gruppe;
    }

    private static Geometry? Form(XElement e)
    {
        double N(string n, double d = 0) => Zahl((string?)e.Attribute(n), d);
        switch (e.Name.LocalName)
        {
            case "path":
                return (string?)e.Attribute("d") is { Length: > 0 } d ? Geometry.Parse(d) : null;
            case "circle":
                return new EllipseGeometry(new Point(N("cx"), N("cy")), N("r"), N("r"));
            case "ellipse":
                return new EllipseGeometry(new Point(N("cx"), N("cy")), N("rx"), N("ry"));
            case "rect":
            {
                double rx = N("rx", N("ry")), ry = N("ry", rx);
                return new RectangleGeometry(new Rect(N("x"), N("y"), N("width"), N("height")), rx, ry);
            }
            case "line":
                return new LineGeometry(new Point(N("x1"), N("y1")), new Point(N("x2"), N("y2")));
            case "polyline":
            case "polygon":
            {
                var z = Zahlen((string?)e.Attribute("points"));
                if (z.Count < 4) return null;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(z[0], z[1]), e.Name.LocalName == "polygon", e.Name.LocalName == "polygon");
                    for (int i = 2; i + 1 < z.Count; i += 2) c.LineTo(new Point(z[i], z[i + 1]), true, false);
                }
                g.Freeze();
                return g;
            }
        }
        return null;
    }

    private static void Durchlaufen(XElement knoten, Stil geerbt, Color akzent, DrawingGroup ziel, ref int gezeichnet)
    {
        foreach (var e in knoten.Elements())
        {
            var name = e.Name.LocalName;
            if (name is "defs" or "clipPath" or "mask" or "style" or "title" or "desc" or "metadata" or "linearGradient" or "radialGradient" or "filter") continue;
            if (Wert(e, "display") == "none" || Wert(e, "visibility") == "hidden") continue;

            var stil = Erben(e, geerbt, akzent);
            var transform = Verwandlung((string?)e.Attribute("transform"));

            if (name is "g" or "svg" or "a")
            {
                var gruppe = new DrawingGroup { Opacity = stil.Deckkraft };
                if (transform != null) gruppe.Transform = transform;
                int vorher = gezeichnet;
                Durchlaufen(e, stil, akzent, gruppe, ref gezeichnet);
                if (gezeichnet > vorher) ziel.Children.Add(gruppe);
                continue;
            }

            Geometry? geo;
            try { geo = Form(e); } catch { continue; }
            if (geo == null) continue;
            if (stil.Fill == null && stil.Stroke == null) continue;

            Pen? stift = null;
            if (stil.Stroke != null && stil.Strichstaerke > 0)
                stift = new Pen(stil.Stroke, stil.Strichstaerke) { StartLineCap = stil.Kappe, EndLineCap = stil.Kappe, LineJoin = stil.Ecke };
            var zeichnung = new GeometryDrawing(stil.Fill, stift, geo);
            if (transform != null || stil.Deckkraft < 1)
            {
                var huelle = new DrawingGroup { Opacity = stil.Deckkraft };
                if (transform != null) huelle.Transform = transform;
                huelle.Children.Add(zeichnung);
                ziel.Children.Add(huelle);
            }
            else ziel.Children.Add(zeichnung);
            gezeichnet++;
        }
    }
}
