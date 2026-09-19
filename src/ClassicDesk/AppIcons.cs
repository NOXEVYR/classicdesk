using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        "document", "picture", "music", "video", "archive", "code", "drive", "browser", "notes", "back", "up", "refresh", "add", "close", "search"
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

    // Standard pre-anime icons stay unchanged when switching skins.
    static ImageSource Draw(string key)
    {
        if (key == "brand")
        {
            var brand = new BitmapImage(new Uri("pack://application:,,,/ClassicDesk;component/Assets/brand-starlight.png", UriKind.Absolute));
            brand.Freeze(); return brand;
        }
        var group = new DrawingGroup(); using (var dc = group.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 24, 24));
            var pen = P("#9194AD", 1.6);
            string path = key switch
            {
                "brand" => "M4,5 L20,5 20,17 4,17 Z M8,21 L16,21 M12,17 L12,21 M8,9 L11,9 11,13 8,13 Z M14,9 L17,9 M14,12 L17,12",
                "appearance" => "M4,4 L10,4 10,10 4,10 Z M14,4 L20,4 20,10 14,10 Z M4,14 L10,14 10,20 4,20 Z M14,14 L20,14 20,20 14,20 Z",
                "taskbar" => "M3,5 L21,5 21,19 3,19 Z M3,14 L21,14 M6,17 L8,17 M11,17 L13,17 M17,17 L18,17",
                "folder" => "M3,7 L3,19 Q3,21 5,21 L19,21 Q21,21 21,19 L21,8 12,8 9,4 5,4 Q3,4 3,7 Z M3,10 L21,10",
                "context-menu" => "M5,3 L19,3 Q21,3 21,5 L21,19 Q21,21 19,21 L5,21 Q3,21 3,19 L3,5 Q3,3 5,3 Z M7,8 L17,8 M7,12 L14,12 M7,16 L16,16",
                "history" => "M3,10 A9,9 0 1 1 4,17 M3,4 L3,10 9,10 M12,7 L12,12 16,14",
                "layout" => "M3,4 L21,4 21,20 3,20 Z M9,4 L9,20 M9,11 L21,11",
                "start" => "M4,4 L10,4 10,10 4,10 Z M14,4 L20,4 20,10 14,10 Z M4,14 L10,14 10,20 4,20 Z M14,14 L20,14 20,20 14,20 Z",
                "system" => "M4,7 L20,7 M4,17 L20,17 M8,4 L8,10 M16,14 L16,20",
                "picture" => "M3,4 L21,4 21,20 3,20 Z M3,17 L9,11 14,16 17,13 21,17 M15,8 L16,8",
                "tray" => "M3,8 L7,8 10,14 14,14 17,8 21,8 21,20 3,20 Z M12,3 L12,10 M9,7 L12,10 15,7",
                "music" => "M9,18 L9,5 20,3 20,16 M9,9 L20,7 M9,18 A3,2 0 1 1 8,16 M20,16 A3,2 0 1 1 19,14",
                "video" => "M4,4 L20,4 20,20 4,20 Z M10,8 L16,12 10,16 Z",
                "archive" => "M3,4 L21,4 21,8 3,8 Z M5,8 L19,8 19,21 5,21 Z M10,12 L14,12",
                "code" => "M8,6 L2,12 8,18 M16,6 L22,12 16,18 M14,3 L10,21",
                "drive" => "M6,4 L18,4 22,17 22,21 2,21 2,17 Z M2,17 L22,17 M17,19 L19,19",
                "back" => "M14,5 L7,12 14,19 M7,12 L21,12",
                "up" => "M5,10 L12,3 19,10 M12,3 L12,21",
                "refresh" => "M20,9 A8,8 0 1 0 20,16 M20,3 L20,9 14,9",
                "add" => "M12,4 L12,20 M4,12 L20,12",
                "close" => "M5,5 L19,19 M19,5 L5,19",
                "search" => "M17,10 A7,7 0 1 1 3,10 A7,7 0 1 1 17,10 M15,15 L21,21",
                _ => "M5,3 L14,3 20,9 20,21 5,21 Z M14,3 L14,9 20,9 M8,13 L16,13 M8,17 L14,17"
            };
            if (key == "brand")
            {
                dc.DrawRoundedRectangle(G("#4F5DB9", "#1C297F"), null, new Rect(0, 0, 24, 24), 6, 6);
                dc.PushTransform(new ScaleTransform(.75, .75, 12, 12)); pen = P("#FFFFFF", 1.7);
            }
            dc.DrawGeometry(null, pen, Geometry.Parse(path)); if (key == "brand") dc.Pop();
        }
        group.Freeze(); var image = new DrawingImage(group); image.Freeze(); return image;
    }
}
