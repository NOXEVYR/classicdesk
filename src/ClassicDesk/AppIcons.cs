using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClassicDesk;

/// <summary>
/// Original ClassicDesk artwork. Code-native vector geometry; no icon fonts,
/// platform resource extraction, Apple artwork, or third-party icon assets.
/// Frozen images are safe to share between windows and render at any DPI.
/// </summary>
public static class AppIcons
{
    static readonly ConcurrentDictionary<string, ImageSource> Images = new(StringComparer.Ordinal);
    public static IReadOnlyList<string> Keys { get; } = Array.AsReadOnly(new[]
    {
        "brand", "appearance", "taskbar", "folder", "start", "layout", "context-menu", "history", "system", "tray",
        "document", "picture", "music", "video", "archive", "code", "drive", "back", "up", "refresh", "add", "close", "search"
    });

    public static ImageSource Get(string key) => Images.GetOrAdd(key ?? "document", Draw);
    public static Image View(string key, double size = 28) => new()
    {
        Source = Get(key), Width = size, Height = size, Stretch = Stretch.Uniform,
        SnapsToDevicePixels = true, UseLayoutRounding = true, IsHitTestVisible = false
    };

    static Color C(string value) => (Color)ColorConverter.ConvertFromString(value);
    static Brush B(string value) => new SolidColorBrush(C(value));
    static Brush G(string top, string bottom) => new LinearGradientBrush(C(top), C(bottom), 90);
    static Pen P(string color, double width = 2) => new(B(color), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
    static Geometry Shape(string data) => Geometry.Parse(data);
    static void Path(DrawingContext dc, string data, string? fill = null, string? stroke = null, double width = 2) => dc.DrawGeometry(fill is null ? null : B(fill), stroke is null ? null : P(stroke, width), Shape(data));
    static void Line(DrawingContext dc, double x, double y, double x2, double y2, string color = "#FFFFFF", double width = 2) => dc.DrawLine(P(color, width), new(x, y), new(x2, y2));
    static void Dot(DrawingContext dc, double x, double y, double radius, string color = "#FFFFFF") => dc.DrawEllipse(B(color), null, new(x, y), radius, radius);
    static void Round(DrawingContext dc, double x, double y, double width, double height, double radius, Brush fill, string? stroke = null, double strokeWidth = 1) => dc.DrawRoundedRectangle(fill, stroke is null ? null : P(stroke, strokeWidth), new(x, y, width, height), radius, radius);

    static ImageSource Draw(string key)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            // Transparent extent keeps every glyph aligned, including soft shadows.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 64, 64));
            var colors = key switch
            {
                "brand" => ("#78CBFF", "#246DF2"),
                "appearance" => ("#A5BEFF", "#626BE9"),
                "taskbar" => ("#73BCFF", "#2879D9"),
                "folder" => ("#FFD47D", "#ED9D30"),
                "start" => ("#C1A3FF", "#8854D7"),
                "layout" => ("#80DCCE", "#2B9D9A"),
                "context-menu" => ("#98A9E8", "#6A7FCC"),
                "history" => ("#FFC38B", "#E78342"),
                "system" => ("#C5CEDC", "#7B899E"),
                "tray" => ("#94B6E4", "#597DB4"),
                "picture" => ("#A1DDC0", "#4CA57A"),
                "music" => ("#FFADBD", "#DC5D85"),
                "video" => ("#CDB2FF", "#9167CB"),
                "archive" => ("#E4C291", "#B88B59"),
                "code" => ("#8DCEDC", "#428FA7"),
                "drive" => ("#C5D0DF", "#7A8CA6"),
                "document" => ("#E6EEF9", "#B2C5E0"),
                _ => ("#F7FAFF", "#DCE6F4")
            };
            Tile(dc, colors.Item1, colors.Item2);
            switch (key)
            {
                case "brand": Brand(dc); break;
                case "appearance": Appearance(dc); break;
                case "taskbar": Taskbar(dc); break;
                case "folder": Folder(dc); break;
                case "start": Start(dc); break;
                case "layout": Layout(dc); break;
                case "context-menu": ContextMenu(dc); break;
                case "history": History(dc); break;
                case "system": System(dc); break;
                case "tray": Tray(dc); break;
                case "picture": Picture(dc); break;
                case "music": Music(dc); break;
                case "video": Video(dc); break;
                case "archive": Archive(dc); break;
                case "code": Code(dc); break;
                case "drive": Drive(dc); break;
                case "back": Path(dc, "M 36,20 L 24,31 L 36,42", null, "#4F6D93", 3.7); break;
                case "up": Path(dc, "M 21,29 L 32,19 L 43,29 M 32,20 L 32,44", null, "#4F6D93", 3.5); break;
                case "refresh": Refresh(dc); break;
                case "add": Line(dc, 21, 31, 43, 31, "#4F6D93", 3.5); Line(dc, 32, 20, 32, 42, "#4F6D93", 3.5); break;
                case "close": Line(dc, 23, 22, 41, 40, "#4F6D93", 3.4); Line(dc, 23, 40, 41, 22, "#4F6D93", 3.4); break;
                case "search": dc.DrawEllipse(null, P("#4F6D93", 3.3), new(28, 27), 10, 10); Line(dc, 35.5, 34.5, 44, 43, "#4F6D93", 3.7); break;
                default: Document(dc); break;
            }
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing); image.Freeze(); return image;
    }

    static void Tile(DrawingContext dc, string top, string bottom)
    {
        Round(dc, 4, 5, 56, 55, 14, B("#081F3554"));
        Round(dc, 4, 3, 56, 55, 14, G(top, bottom), "#20FFFFFF", .7);
    }

    static void Brand(DrawingContext dc)
    {
        Round(dc, 12, 16, 40, 29, 5, B("#203158A8"));
        Round(dc, 12, 14, 40, 29, 5, G("#FFFFFF", "#D9EBFF"), "#80FFFFFF");
        Round(dc, 15, 17, 34, 21, 3, G("#84D5FA", "#5694ED"));
        dc.PushClip(new RectangleGeometry(new Rect(15, 17, 34, 21), 3, 3));
        Path(dc, "M 14,33 C 23,21 30,22 38,31 C 43,36 46,32 51,26 L 51,40 L 14,40 Z", "#7BE3DB");
        Path(dc, "M 14,36 C 26,25 37,33 51,30 L 51,41 L 14,41 Z", "#477EE8");
        dc.Pop();
        Round(dc, 25, 43, 14, 3, 1.5, B("#EAF5FF"));
        Round(dc, 19, 48, 26, 5, 2.5, B("#F0FFFFFF"));
        foreach (var x in new[] { 23.0, 29.0, 35.0, 41.0 }) Dot(dc, x, 50.5, 1.5, x < 30 ? "#67AAED" : "#72C7CB");
    }

    static void Appearance(DrawingContext dc)
    {
        Round(dc, 15, 16, 29, 32, 7, B("#263D3B91"));
        Round(dc, 13, 13, 30, 32, 7, G("#FFFFFF", "#DCE7FF"));
        Round(dc, 24, 23, 27, 27, 7, G("#385AB5", "#212F75"), "#91C5D8FF");
        Dot(dc, 26, 26, 6, "#F8C76B");
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            Line(dc, 26 + Math.Cos(a) * 8, 26 + Math.Sin(a) * 8, 26 + Math.Cos(a) * 9.4, 26 + Math.Sin(a) * 9.4, "#F4BB57", 1.7);
        }
        Path(dc, "M 42,29 C 35,28 31,34 34,40 C 37,46 45,46 48,40 C 42,42 37,36 42,29 Z", "#EAF4FF");
        Dot(dc, 46, 30, 1.2, "#DDEBFF");
    }

    static void Taskbar(DrawingContext dc)
    {
        Round(dc, 12, 16, 40, 32, 5, B("#214067A4"));
        Round(dc, 12, 14, 40, 32, 5, G("#EAF8FF", "#D1E9FC"));
        Round(dc, 15, 17, 34, 19, 2.5, G("#91CFF0", "#4D95DC"));
        Round(dc, 14, 37, 36, 7, 2, G("#FFFFFFFF", "#EAF5FF"));
        for (var i = 0; i < 4; i++) Round(dc, 17 + i * 7, 39, 4, 3, 1, B(i == 0 ? "#F3BC61" : "#70A6D9"));
        Dot(dc, 46, 40.5, 1, "#68A0CE");
        Line(dc, 26, 50, 38, 50, "#EAF6FF", 2.3);
    }

    static void Folder(DrawingContext dc)
    {
        Path(dc, "M 12,23 Q 12,18 17,18 L 26,18 L 31,23 L 47,23 Q 51,23 51,27 L 51,45 Q 51,49 47,49 L 17,49 Q 12,49 12,44 Z", "#A46922");
        Path(dc, "M 12,21 Q 12,17 17,17 L 26,17 L 31,22 L 47,22 Q 51,22 51,27 L 51,43 L 12,43 Z", "#FFE3A1");
        Round(dc, 17, 24, 29, 19, 2, G("#FFFFFF", "#F7F3E6"));
        Line(dc, 22, 29, 39, 29, "#DFD9C9", 1.7);
        Path(dc, "M 15,30 L 50,30 Q 54,30 53,34 L 49,46 Q 48,49 44,49 L 17,49 Q 13,49 13,45 L 11,35 Q 10,30 15,30 Z", "#F9C858");
        Path(dc, "M 14,33 L 49,33", null, "#FFF0B9", 1.4);
    }

    static void Start(DrawingContext dc)
    {
        var colors = new[] { "#FFFFFF", "#F4E9FF", "#EBDDFF", "#DBCDFF" };
        for (var i = 0; i < 4; i++)
        {
            var x = 16 + i % 2 * 18; var y = 15 + i / 2 * 18;
            Round(dc, x, y + 2, 14, 14, 4, B("#208040A6"));
            Round(dc, x, y, 14, 14, 4, G(colors[i], "#DAFFFFFF"), "#70FFFFFF", .7);
        }
    }

    static void Layout(DrawingContext dc)
    {
        Round(dc, 12, 15, 40, 33, 5, B("#223A716D"));
        Round(dc, 12, 13, 40, 33, 5, G("#FFFFFF", "#E2FAF4"));
        Round(dc, 15, 17, 10, 25, 2, G("#B0E4DC", "#83CFC4"));
        Round(dc, 28, 17, 20, 7, 2, B("#A5DCD3"));
        Round(dc, 28, 27, 9, 15, 2, B("#7CC4B8"));
        Round(dc, 40, 27, 8, 15, 2, B("#BEDFD9"));
    }

    static void ContextMenu(DrawingContext dc)
    {
        Round(dc, 13, 14, 38, 37, 6, B("#18314183"));
        Round(dc, 12, 12, 40, 37, 6, G("#FFFFFF", "#EDF1FD"));
        Round(dc, 16, 17, 32, 8, 2.5, B("#D8E3FB"));
        Path(dc, "M 20,21 L 22,23 L 25,19", null, "#6384C8", 1.6);
        Line(dc, 29, 21, 43, 21, "#6384C8", 1.6);
        Line(dc, 20, 31, 43, 31, "#A0AFCA", 1.6);
        Line(dc, 20, 40, 35, 40, "#A0AFCA", 1.6);
        Path(dc, "M 41,37 L 44,40 L 41,43", null, "#8B9CBB", 1.6);
    }

    static void History(DrawingContext dc)
    {
        dc.DrawEllipse(B("#20A65921"), null, new(33, 33), 17, 17);
        dc.DrawEllipse(G("#FFFFFF", "#FFF1D9"), null, new(33, 30), 17, 17);
        for (var i = 0; i < 4; i++)
        {
            var a = i * Math.PI / 2;
            Line(dc, 33 + Math.Cos(a) * 12, 30 + Math.Sin(a) * 12, 33 + Math.Cos(a) * 13, 30 + Math.Sin(a) * 13, "#D19B68", 1.4);
        }
        Path(dc, "M 33,20 L 33,30 L 40,34", null, "#B97642", 2.4);
        Path(dc, "M 13,27 C 9,38 16,48 27,50", null, "#FFFFFF", 2.9);
        Path(dc, "M 9,27 L 14,22 L 18,28", null, "#FFFFFF", 2.6);
    }

    static void System(DrawingContext dc)
    {
        var gear = new StreamGeometry();
        using (var g = gear.Open())
        {
            const int teeth = 10;
            for (var i = 0; i < teeth * 4; i++)
            {
                var angle = i * Math.PI * 2 / (teeth * 4) - Math.PI / 2;
                var r = i % 4 is 1 or 2 ? 19 : 15.5;
                var p = new Point(32 + Math.Cos(angle) * r, 31 + Math.Sin(angle) * r);
                if (i == 0) g.BeginFigure(p, true, true); else g.LineTo(p, true, false);
            }
        }
        dc.DrawGeometry(G("#FFFFFF", "#DCE4F0"), P("#70FFFFFF", .8), gear);
        dc.DrawEllipse(G("#73849B", "#A9B9CC"), P("#FFFFFF", 1.1), new(32, 31), 9.5, 9.5);
        dc.DrawEllipse(G("#F7FAFF", "#C9D5E5"), null, new(32, 31), 4.2, 4.2);
    }

    static void Tray(DrawingContext dc)
    {
        Round(dc, 12, 35, 40, 13, 4, B("#224E638D"));
        Round(dc, 12, 32, 40, 13, 4, G("#FFFFFF", "#DDEAFF"));
        Path(dc, "M 17,39 L 21,39 M 27,39 L 31,39 M 37,39 L 41,39 M 47,39 L 48,39", null, "#819CBD", 2.1);
        Path(dc, "M 23,26 L 28,21 M 41,26 L 36,21", null, "#EAF3FF", 2.7);
        Line(dc, 32, 16, 32, 25, "#FFFFFF", 2.7);
    }

    static void Document(DrawingContext dc)
    {
        Path(dc, "M 21,12 L 38,12 L 48,22 L 48,48 Q 48,51 44,51 L 21,51 Q 17,51 17,47 L 17,16 Q 17,12 21,12 Z", "#2442638A");
        Path(dc, "M 20,10 L 38,10 L 48,20 L 48,46 Q 48,49 44,49 L 20,49 Q 16,49 16,45 L 16,14 Q 16,10 20,10 Z", "#FAFDFF");
        Path(dc, "M 38,10 L 38,17 Q 38,20 41,20 L 48,20 Z", "#CCE0F5");
        Round(dc, 22, 26, 15, 3, 1.5, B("#7299C6"));
        Line(dc, 23, 34, 41, 34, "#B9CEE5", 2);
        Line(dc, 23, 40, 36, 40, "#B9CEE5", 2);
    }

    static void Picture(DrawingContext dc)
    {
        Round(dc, 12, 17, 40, 31, 4, B("#23507866"));
        Round(dc, 12, 14, 40, 31, 4, G("#FFFFFF", "#EAF6EB"));
        Round(dc, 15, 17, 34, 25, 2, G("#A0DDDA", "#D6EFE5"));
        dc.PushClip(new RectangleGeometry(new Rect(15, 17, 34, 25), 2, 2));
        Dot(dc, 41, 23, 3.5, "#FFF4B2");
        Path(dc, "M 14,39 L 26,25 L 37,39 L 45,30 L 51,40 L 51,45 L 14,45 Z", "#82BFA1");
        Path(dc, "M 14,43 L 30,32 L 45,44 Z", "#4C977B");
        dc.Pop();
    }

    static void Music(DrawingContext dc)
    {
        Path(dc, "M 27,39 L 27,19 L 46,15 L 46,35", null, "#FFFFFF", 3.6);
        Path(dc, "M 27,20 L 46,16 L 46,22 L 27,26 Z", "#FFFFFF");
        dc.DrawEllipse(G("#FFFFFF", "#FFE3EB"), null, new(21, 41), 7, 5);
        dc.DrawEllipse(G("#FFFFFF", "#FFE3EB"), null, new(40, 37), 7, 5);
    }

    static void Video(DrawingContext dc)
    {
        Round(dc, 12, 17, 40, 29, 5, B("#284F327C"));
        Round(dc, 12, 14, 40, 29, 5, G("#FFFFFF", "#F0E7FF"));
        Round(dc, 15, 17, 34, 5, 1.5, B("#B499D5"));
        foreach (var x in new[] { 20.0, 30.0, 40.0 }) Line(dc, x, 17, x - 3, 22, "#F7EFFF", 2.4);
        Path(dc, "M 28,27 L 28,38 L 39,32.5 Z", "#A480CE");
    }

    static void Archive(DrawingContext dc)
    {
        Round(dc, 15, 21, 34, 29, 3, B("#2E6A492D"));
        Round(dc, 15, 18, 34, 29, 3, G("#FFEDCF", "#E9C995"));
        Round(dc, 12, 14, 40, 10, 3, G("#FFF3DA", "#F3D3A3"));
        Round(dc, 25, 29, 14, 7, 2, B("#B68E61"));
        Line(dc, 29, 32.5, 35, 32.5, "#FFE9C5", 1.8);
    }

    static void Code(DrawingContext dc)
    {
        Round(dc, 11, 17, 42, 31, 5, B("#2B23586D"));
        Round(dc, 11, 14, 42, 31, 5, G("#EAF8FC", "#D6ECF2"));
        Path(dc, "M 23,25 L 18,30 L 23,35 M 41,25 L 46,30 L 41,35 M 34,23 L 30,38", null, "#4389A4", 2.5);
    }

    static void Drive(DrawingContext dc)
    {
        Path(dc, "M 19,16 L 45,16 Q 49,16 50,21 L 53,42 Q 53,48 48,48 L 16,48 Q 11,48 11,42 L 14,21 Q 15,16 19,16 Z", "#29425068");
        Path(dc, "M 19,13 L 45,13 Q 49,13 50,18 L 53,39 Q 53,45 48,45 L 16,45 Q 11,45 11,39 L 14,18 Q 15,13 19,13 Z", "#E3EBF5");
        Round(dc, 13, 35, 38, 9, 3, G("#FFFFFF", "#C6D3E4"));
        dc.DrawEllipse(G("#FAFDFF", "#B2C1D5"), null, new(32, 25), 10, 6.2);
        dc.DrawEllipse(B("#8B9CB3"), null, new(32, 25), 2.8, 1.9);
        Dot(dc, 45, 39.5, 1.5, "#55AA9A");
        Line(dc, 18, 39.5, 28, 39.5, "#A0B0C6", 1.7);
    }

    static void Refresh(DrawingContext dc)
    {
        Path(dc, "M 44,26 C 40,16 26,16 21,25 C 16,34 22,44 32,44 C 37,44 41,41 43,37", null, "#4F6D93", 3.2);
        Path(dc, "M 44,18 L 44,27 L 35,27", null, "#4F6D93", 3.2);
    }
}
