using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClassicDesk;

/// <summary>Resolution-independent original desktop artwork, not a system screenshot.</summary>
public sealed class DesktopBackdrop : FrameworkElement
{
    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(nameof(Variant), typeof(int), typeof(DesktopBackdrop), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public int Variant { get => (int)GetValue(VariantProperty); set => SetValue(VariantProperty,value); }
    public bool Thumbnail { get; init; }
    public static ImageSource Anime => Art(4);
    static readonly Dictionary<int, ImageSource> ArtCache = new();
    static ImageSource Art(int variant)
    {
        if (ArtCache.TryGetValue(variant, out var cached)) return cached;
        string name = variant switch { 5 => "skin-sakura.png", 6 => "skin-cloud.png", 7 => "skin-moon.png", _ => "starlight-anime.png" };
        return ArtCache[variant] = LoadArt(name);
    }
    static ImageSource LoadArt(string name)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri("pack://application:,,,/ClassicDesk;component/Assets/" + name, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
    public DesktopBackdrop() { ClipToBounds = true; IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        double w=ActualWidth,h=ActualHeight; if(w<=0||h<=0)return;
        Color C(string value)=>(Color)ColorConverter.ConvertFromString(value);
        Brush B(string value)=>new SolidColorBrush(C(value));
        var palette = Variant switch { 1 => new[]{"#E3EBFF","#96B6F2","#7793DF"}, 2 => new[]{"#E9EDFA","#BBC8E8","#869ACC"}, 3=>new[]{"#F4EEF9","#D9CEEC","#AF9ED2"}, 4=>new[]{"#EEF0FD","#D7D5EF","#ACA1D0"}, _=>new[]{"#DAEBFF","#9EC6F4","#6599DD"} };
        dc.PushClip(new RectangleGeometry(new Rect(0,0,w,h),14,14));
        dc.DrawRectangle(new LinearGradientBrush(C(palette[0]),C(palette[1]),40),null,new Rect(0,0,w,h));
        if(Variant>=4) dc.DrawRectangle(new ImageBrush(Art(Variant)){Stretch=Stretch.UniformToFill,AlignmentX=AlignmentX.Right,AlignmentY=AlignmentY.Center},null,new Rect(0,0,w,h));
        else if(Variant==0) {
            for(int n=0;n<4;n++) { var x=w*.60+(n%2)*w*.12; var y=h*.13+(n/2)*h*.34; dc.DrawRectangle(new LinearGradientBrush(C("#38FFFFFF"),C("#B0FFFFFF"),20),new Pen(B("#55FFFFFF"),1),new Rect(x,y,w*.105,h*.29)); }
        } else {
            dc.PushTransform(new ScaleTransform(w/800,h/240));
            for(int n=0;n<8;n++) {
                dc.PushTransform(new RotateTransform(-35+n*9,560,230));
                dc.DrawEllipse(new LinearGradientBrush(C("#D0FFFFFF"),C(palette[2]),n*18),new Pen(B("#50FFFFFF"),.7),new Point(570,95),125-n*8,190-n*11); dc.Pop();
            } dc.Pop();
        }
        dc.DrawRectangle(new LinearGradientBrush(C("#55FFFFFF"),C("#08FFFFFF"),0),null,new Rect(0,0,w,h));
        if(Thumbnail) {
            double x=w*.10,y=h*.13,fw=w*.64,fh=h*.57;
            dc.DrawRoundedRectangle(B("#150F2150"),null,new Rect(x+2,y+4,fw,fh),7,7);
            dc.DrawRoundedRectangle(B("#F2FFFFFF"),new Pen(B("#CBFFFFFF"),1),new Rect(x,y,fw,fh),7,7);
            dc.DrawRoundedRectangle(B("#E8EDFA"),null,new Rect(x+1,y+1,fw-2,16),6,6);
            for(int n=0;n<3;n++) dc.DrawEllipse(B(n==0?"#CFBDDC":n==1?"#BCD0EA":"#B6C3EA"),null,new Point(x+9+n*8,y+8),2.3,2.3);
            dc.DrawRoundedRectangle(B("#EDF0FA"),null,new Rect(x+6,y+22,fw*.24,fh-28),3,3);
            for(int n=0;n<3;n++) { dc.DrawImage(AppIcons.Get(n==0?"folder":n==1?"picture":"document"),new Rect(x+fw*.33,y+23+n*13,11,11)); dc.DrawRoundedRectangle(B("#D8DEEE"),null,new Rect(x+fw*.33+17,y+27+n*13,fw*.42-n*5,3),1.5,1.5); }
            double barW=w-16,barX=(w-barW)/2;
            dc.DrawRoundedRectangle(B("#EFFFFFFF"),new Pen(B("#F5FFFFFF"),1),new Rect(barX,h-28,barW,21),Variant==3?7:4,Variant==3?7:4);
            bool centered=Variant is 1 or 3 or 4; double first=centered?w/2-37:w/2-23;
            if(!centered)dc.DrawImage(AppIcons.Get("start"),new Rect(barX+7,h-24,12,12));
            string[] icons=centered?["start","folder","picture","context-menu"]:["folder","picture","context-menu"];
            for(int n=0;n<icons.Length;n++)dc.DrawImage(AppIcons.Get(icons[n]),new Rect(first+n*19,h-24,12,12));
        }
        dc.Pop();
    }
}

public enum ShellPreviewKind { Taskbar, Explorer, ContextMenu }

public sealed record ShellPreviewOptions(
    bool ClassicRibbon = true, bool StartOnLeft = true, int IconSize = 24, int TaskbarHeight = 48,
    bool ClassicContextMenu = true, bool Mica = true, int TaskbarButtonWidth = 44,
    int SmallIconSize = 16, int SmallTaskbarButtonWidth = 32, bool OtherSystemButtonsOnLeft = true,
    bool StartMenuOnLeft = true, bool SearchMenuOnLeft = false, bool ClassicMenuWithCtrl = true,
    bool UseClassicNavigationBar = false, bool LeftAlignedApps = false);

/// <summary>Original vector proposal, never a live shell capture. No handles, OS reads or actions.
/// Mica is retained only for API compatibility; no material-setting capability is implied.</summary>
public sealed class ShellPreview : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(ShellPreviewKind), typeof(ShellPreview),
        new FrameworkPropertyMetadata(ShellPreviewKind.Taskbar, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is ShellPreviewKind kind && Enum.IsDefined(kind));
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(nameof(Options), typeof(ShellPreviewOptions), typeof(ShellPreview),
        new FrameworkPropertyMetadata(new ShellPreviewOptions(), FrameworkPropertyMetadataOptions.AffectsRender), value => value is ShellPreviewOptions);
    public static readonly DependencyProperty BeforeProperty = DependencyProperty.Register(nameof(Before), typeof(bool), typeof(ShellPreview),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public ShellPreviewKind Kind { get => (ShellPreviewKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public ShellPreviewOptions Options { get => (ShellPreviewOptions)GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }
    public bool Before { get => (bool)GetValue(BeforeProperty); set => SetValue(BeforeProperty, value); }
    double NaturalHeight => Kind switch { ShellPreviewKind.Taskbar => 108, ShellPreviewKind.Explorer => 176, _ => 174 };
    static readonly Typeface Face = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface Bold = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    const string Ink = "#354052", Muted = "#89919E", Edge = "#E3E7ED", Blue = "#397DEA";
    public ShellPreview()
    {
        ClipToBounds = true; UseLayoutRounding = true; SnapsToDevicePixels = true;
        System.Windows.Automation.AutomationProperties.SetName(this, "Windows 原生布局参数示意");
        System.Windows.Automation.AutomationProperties.SetHelpText(this, "原创矢量示意，不是当前系统截图，不代表组件已启用。");
    }
    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 760 : Math.Max(0, availableSize.Width),
        double.IsInfinity(availableSize.Height) ? NaturalHeight : Math.Min(NaturalHeight, Math.Max(0, availableSize.Height)));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (ActualWidth <= 0 || ActualHeight <= 0) return;
        // Normal layout stays 1:1. Narrow rendering scales the drawing without stretching its icons.
        var scale = Math.Min(ActualWidth / 640, ActualHeight / NaturalHeight);
        var width = ActualWidth / scale;
        dc.PushTransform(new TranslateTransform(0, (ActualHeight - NaturalHeight * scale) / 2)); dc.PushTransform(new ScaleTransform(scale, scale));
        if (Kind == ShellPreviewKind.Taskbar) Taskbar(dc, width);
        else if (Kind == ShellPreviewKind.Explorer) Explorer(dc, width);
        else DrawContextMenu(dc, width);
        dc.Pop(); dc.Pop();
    }
    static Color C(string value) => (Color)ColorConverter.ConvertFromString(value);
    static Brush B(string value) => new SolidColorBrush(C(value));
    static Brush G(string top, string bottom) => new LinearGradientBrush(C(top), C(bottom), 90);
    static Pen P(string color, double thickness = 1) => new(B(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
    static void Rect(DrawingContext dc, double x, double y, double w, double h, string fill, double radius = 0, string? stroke = null) =>
        dc.DrawRoundedRectangle(B(fill), stroke is null ? null : P(stroke), new(x, y, Math.Max(0, w), Math.Max(0, h)), radius, radius);
    static void Line(DrawingContext dc, double x, double y, double x2, double y2, string color = Edge, double thickness = 1) => dc.DrawLine(P(color, thickness), new(x, y), new(x2, y2));
    static void Shape(DrawingContext dc, string geometry, string? fill = null, string? stroke = null, double thickness = 1.3) =>
        dc.DrawGeometry(fill is null ? null : B(fill), stroke is null ? null : P(stroke, thickness), Geometry.Parse(geometry));
    static void Text(DrawingContext dc, string text, double x, double y, double size = 11, string color = Ink, bool bold = false, double width = 0)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, bold ? Bold : Face, size, B(color), 1);
        if (width > 0) { formatted.MaxTextWidth = width; formatted.MaxLineCount = 1; formatted.Trimming = TextTrimming.CharacterEllipsis; }
        dc.DrawText(formatted, new(x, y));
    }
    static void Icon(DrawingContext dc, string kind, double x, double y, double size, string color = Ink)
    {
        dc.PushTransform(new TranslateTransform(x, y)); dc.PushTransform(new ScaleTransform(size / 24, size / 24));
        switch (kind)
        {
            case "folder":
                Shape(dc, "M 2,6 Q 2,4 4,4 L 10,4 L 13,7 L 20,7 Q 22,7 22,9 L 22,19 Q 22,21 20,21 L 4,21 Q 2,21 2,19 Z", "#E4A833");
                Rect(dc, 5, 8, 15, 9, "#FFF6DC", 1);
                Shape(dc, "M 3,10 L 21,10 Q 23,10 22.7,12 L 21.4,19.4 Q 21.1,21 19.5,21 L 4,21 Q 2,21 1.8,19 L 1.2,12 Q 1,10 3,10 Z", "#F7C75B");
                Line(dc, 4, 11.5, 20, 11.5, "#FFE5A0", 1); break;
            case "browser":
                dc.DrawEllipse(G("#5D6BC9", "#268BE5"), null, new(12, 12), 10.5, 10.5);
                Shape(dc, "M 3,14 C 7,21 18,21 22,11 C 19,15 15,15 12,13 C 8,10 4,10 3,14 Z", "#1D7CCC");
                Shape(dc, "M 3,11 C 8,3 15,5 17,9 C 18,12 13,14 9,11 C 13,16 20,15 22,10 C 20,1 8,-1 3,11 Z", "#6E7BD4"); break;
            case "photo":
                Rect(dc, 1, 2, 22, 20, "#E3EEFF", 4); dc.DrawEllipse(B("#F5C454"), null, new(17, 8), 3, 3);
                Shape(dc, "M 2,19 L 8,10 L 14,17 L 18,12 L 22,18 L 22,21 L 2,21 Z", "#5499D8"); break;
            case "notes":
                Rect(dc, 3, 1, 18, 22, "#D9E9FC", 3); Rect(dc, 3, 1, 18, 5, "#74A9E4", 2);
                for (int i = 0; i < 3; i++) Line(dc, 7, 10 + i * 4, 17, 10 + i * 4, "#77A2CF", 1.5); break;
            case "start":
                foreach (var pos in new[] { new Point(2, 2), new Point(13, 2), new Point(2, 13), new Point(13, 13) }) Rect(dc, pos.X, pos.Y, 9, 9, Blue, .8); break;
            case "search":
                dc.DrawEllipse(null, P(color, 1.7), new(10, 10), 6.5, 6.5); Line(dc, 15, 15, 21, 21, color, 1.7); break;
            case "taskview":
                Rect(dc, 2, 5, 15, 14, "#DBE5F2", 2, "#8B9AB0"); Rect(dc, 8, 3, 14, 14, "#F6F9FF", 2, "#8B9AB0"); break;
            case "copy":
                Rect(dc, 7, 3, 14, 16, "#FFFFFF", 2, color); Shape(dc, "M 16,22 L 5,22 Q 3,22 3,20 L 3,9", null, color); break;
            case "paste":
                Rect(dc, 5, 5, 15, 17, "#F6E2B8", 2, "#B29B73"); Rect(dc, 9, 2, 7, 5, "#FFF6DF", 1, "#B29B73"); Line(dc, 9, 11, 16, 11, "#B29B73"); Line(dc, 9, 15, 16, 15, "#B29B73"); break;
            case "cut":
                dc.DrawEllipse(null, P(color, 1.4), new(6, 17), 3.2, 3.2); dc.DrawEllipse(null, P(color, 1.4), new(18, 17), 3.2, 3.2);
                Shape(dc, "M 8,15 L 18,3 M 16,15 L 6,3", null, color, 1.4); break;
            case "rename":
                Rect(dc, 2, 7, 20, 11, "#FFFFFF", 2, color); Shape(dc, "M 14,3 L 14,22 M 11,3 L 17,3 M 11,22 L 17,22", null, color); break;
            case "share": Shape(dc, "M 9,8 L 9,16 L 19,16 M 15,12 L 19,16 L 15,20 M 6,4 L 3,4 L 3,21 L 10,21", null, color, 1.5); break;
            case "delete": Shape(dc, "M 5,7 L 6,21 L 18,21 L 19,7 M 3,6 L 21,6 M 8,6 L 8,3 L 16,3 L 16,6 M 10,10 L 10,17 M 14,10 L 14,17", null, color); break;
            case "check": Shape(dc, "M 4,12 L 9,17 L 20,6", null, color, 1.8); break;
            case "new":
                Icon(dc, "folder", 0, 0, 24); dc.DrawEllipse(B("#4E5899"), P("#FFFFFF", 1.2), new(18, 17), 5.5, 5.5); Line(dc, 15, 17, 21, 17, "#FFFFFF", 1.4); Line(dc, 18, 14, 18, 20, "#FFFFFF", 1.4); break;
            case "details":
                Rect(dc, 4, 2, 16, 21, "#F9FBFE", 2, "#98A6B8"); dc.DrawEllipse(B(Blue), null, new(12, 8), 1.1, 1.1); Line(dc, 12, 12, 12, 18, Blue, 1.8); break;
            case "pin": Shape(dc, "M 7,3 L 17,3 L 16,11 L 20,15 L 4,15 L 8,11 Z M 12,15 L 12,22", null, color); break;
            case "arrow": Shape(dc, "M 8,5 L 15,12 L 8,19", null, color, 1.5); break;
            default: Rect(dc, 5, 3, 14, 19, "#F5F8FD", 2, "#8C9DB3"); Line(dc, 8, 10, 16, 10, "#95A8BF"); Line(dc, 8, 14, 16, 14, "#95A8BF"); break;
        }
        dc.Pop(); dc.Pop();
    }
    static void Frame(DrawingContext dc, double w, double h, string fill = "#FFFFFF")
    { Rect(dc, 1, 2, w - 2, h - 3, "#060F253F", 10); Rect(dc, .5, .5, w - 1, h - 3, fill, 9, Edge); }
    void Taskbar(DrawingContext dc, double w)
    {
        var o = Before ? new ShellPreviewOptions(StartOnLeft: false, IconSize: 32, TaskbarHeight: 48, OtherSystemButtonsOnLeft: false) : Options;
        int height = Math.Clamp(o.TaskbarHeight, 32, 64), size = Math.Clamp(o.IconSize, 12, Math.Min(40, height - 8));
        int buttonWidth = Math.Clamp(o.TaskbarButtonWidth, 28, 80); double barY = 13 + (64 - height) / 2.0;
        Frame(dc, w, 108, "#F5F7FB");
        // The native taskbar remains a full-width strip, not a floating replacement Dock.
        Rect(dc, 1, barY, w - 2, height, "#FFFFFF");
        Line(dc, 1, barY, w - 1, barY, "#E1E7F0"); Line(dc, 1, barY + height, w - 1, barY + height, "#E1E7F0");
        double middle = barY + height / 2.0;
        bool allLeft = o.LeftAlignedApps;
        bool left = !allLeft && o.StartOnLeft, otherLeft = left && o.OtherSystemButtonsOnLeft;
        int centeredSystemButtons = (left ? 0 : 1) + (otherLeft ? 0 : 2);
        // Wide-button examples show fewer sample apps before touching the left controls or tray.
        double reservedEdge = otherLeft ? 218 : 143;
        int appCount = Math.Clamp((int)Math.Floor((w - (allLeft ? 171 : 2 * reservedEdge)) / buttonWidth) - centeredSystemButtons, 1, 4);
        int centeredCount = appCount + centeredSystemButtons;
        double first = allLeft ? 18 : w / 2 - centeredCount * buttonWidth / 2.0;
        if (left) Icon(dc, "start", 29, middle - 11, 22);
        if (otherLeft)
        {
            Rect(dc, 66, middle - 14, 111, 28, "#F5F7FA", 14, "#E6EAF0");
            Icon(dc, "search", 76, middle - 8, 16, "#697589"); Text(dc, "搜索", 101, middle - 8, 11, "#818B9B");
            Icon(dc, "taskview", 190, middle - 10, 20);
        }
        int offset = 0;
        if (!left) { Icon(dc, "start", first + (buttonWidth - 22) / 2, middle - 11, 22); offset++; }
        if (!otherLeft)
        {
            Icon(dc, "search", first + offset++ * buttonWidth + (buttonWidth - 20) / 2, middle - 10, 20, "#58677C");
            Icon(dc, "taskview", first + offset++ * buttonWidth + (buttonWidth - 21) / 2, middle - 10.5, 21);
        }
        var kinds = new[] { "folder", "browser", "photo", "notes" };
        for (int i = 0; i < appCount; i++)
        {
            double x = first + (offset + i) * buttonWidth;
            if (i == 0) Rect(dc, x + 3, barY + 4, buttonWidth - 6, height - 8, "#EDF3FD", 5);
            Icon(dc, kinds[i], x + (buttonWidth - size) / 2, middle - size / 2.0, size);
            if (i == 0) Rect(dc, x + buttonWidth / 2 - 7, barY + height - 5, 14, 2.5, Blue, 1.25);
        }
        double trayX = w - 135;
        ShapeAt(dc, "M 0,4 L 4,0 L 8,4", trayX, middle - 2, "#667286");
        ShapeAt(dc, "M 0,4 Q 6,-2 12,4 M 3,7 Q 6,4 9,7 M 6,10 L 6,10", trayX + 22, middle - 5, "#667286");
        Text(dc, "09:41", w - 71, middle - 16, 11, "#526078"); Text(dc, "2026/9/11", w - 88, middle + 1, 9, "#8490A2");
        Text(dc, size + " px 图标", 17, 87, 10, "#6F7E92"); Text(dc, buttonWidth + " px 按钮", 94, 87, 10, "#6F7E92"); Text(dc, height + " px 高度", 178, 87, 10, "#6F7E92");
        // Separate sample for Windows' small-button state, not extra applications on the real taskbar.
        double sampleX = 278, small = Math.Clamp(o.SmallIconSize, 12, 32) * .68, smallWidth = Math.Clamp(o.SmallTaskbarButtonWidth, 24, 80) * .68;
        Rect(dc, sampleX, 80.5, smallWidth + 7, 24, "#EBEFF6", 4);
        Icon(dc, "folder", sampleX + (smallWidth + 7 - small) / 2, 92.5 - small / 2, small);
        Text(dc, $"小图标 {o.SmallIconSize} / 按钮 {o.SmallTaskbarButtonWidth}", sampleX + smallWidth + 15, 87, 9.5, Muted);
        if (!Before)
        {
            bool menuLeft = o.StartOnLeft && o.StartMenuOnLeft;
            string searchScope = !menuLeft ? "随系统" : o.SearchMenuOnLeft ? "所有入口" : "仅从开始";
            string placement = allLeft ? "全靠左 · 菜单与搜索原生定位" : $"菜单{(menuLeft ? "靠左" : "随系统")} · 搜索{searchScope}";
            Text(dc, placement, w - (w >= 720 ? 209 : 155), 87, w >= 720 ? 9.5 : 9, Muted, width: w >= 720 ? 195 : 142);
        }
    }
    static void ShapeAt(DrawingContext dc, string data, double x, double y, string stroke)
    { dc.PushTransform(new TranslateTransform(x, y)); Shape(dc, data, null, stroke); dc.Pop(); }
    static void WindowButtons(DrawingContext dc, double w)
    {
        Line(dc, w - 88, 12, w - 80, 12, "#8792A3"); Rect(dc, w - 57, 8, 7, 7, "#00FFFFFF", 0, "#8792A3");
        Line(dc, w - 29, 8, w - 22, 15, "#8792A3"); Line(dc, w - 29, 15, w - 22, 8, "#8792A3");
    }
    void Explorer(DrawingContext dc, double w)
    {
        bool ribbon = !Before && Options.ClassicRibbon, early = !Before && !ribbon && Options.UseClassicNavigationBar;
        Frame(dc, w, 176); dc.PushClip(new RectangleGeometry(new Rect(1, 1, w - 2, 172), 9, 9));
        Rect(dc, 1, 1, w - 2, 26, "#F1F4F8"); WindowButtons(dc, w);
        if (ribbon)
        {
            Icon(dc, "folder", 12, 5, 15); Text(dc, "设计资料", 36, 5, 10.5, "#657186");
            Icon(dc, "check", 123, 6, 13, "#7B91A9"); Icon(dc, "new", 144, 5, 15);
            Rect(dc, 1, 25, 55, 22, "#4387DD"); Text(dc, "文件", 17, 29, 10, "#FFFFFF");
            Rect(dc, 56, 25, 52, 22, "#FFFFFF", 0, "#E6EAF0"); Text(dc, "主页", 71, 29, 10);
            Text(dc, "共享", 124, 29, 10, "#758296"); Text(dc, "查看", 177, 29, 10, "#758296");
            Ribbon(dc, w); Address(dc, w, 102, 28); ExplorerFiles(dc, w, 130);
        }
        else
        {
            Rect(dc, 8, 4, 171, 24, "#FFFFFF", 6); Icon(dc, "folder", 18, 7, 15); Text(dc, "设计资料", 42, 7, 10.5, "#657186");
            Line(dc, 160, 10, 165, 15, "#99A4B4"); Line(dc, 160, 15, 165, 10, "#99A4B4");
            Line(dc, 193, 10, 193, 18, "#8F9AAC"); Line(dc, 189, 14, 197, 14, "#8F9AAC");
            if (early) { CommandBar(dc, w, 28); Address(dc, w, 63, 32); }
            else { Address(dc, w, 28, 35); CommandBar(dc, w, 63); }
            ExplorerFiles(dc, w, 98);
        }
        dc.Pop();
    }
    static void Address(DrawingContext dc, double w, double y, double h)
    {
        Rect(dc, 1, y, w - 2, h, "#FBFCFE");
        ShapeAt(dc, "M 7,0 L 1,6 L 7,12 M 1,6 L 14,6", 16, y + h / 2 - 6, "#718095");
        ShapeAt(dc, "M 1,0 L 7,6 L 1,12", 43, y + h / 2 - 6, "#ADB8C7");
        ShapeAt(dc, "M 0,6 L 6,0 L 12,6 M 6,0 L 6,13", 70, y + h / 2 - 6, "#7C8A9E");
        double searchWidth = Math.Clamp(w * .23, 145, 190), boxHeight = h - 9;
        Rect(dc, 101, y + 4, w - 121 - searchWidth, boxHeight, "#FFFFFF", 4, "#DFE5ED");
        Icon(dc, "folder", 111, y + (h - 14) / 2, 14); Text(dc, "此电脑   ›   文档   ›   设计资料", 135, y + (h - 13) / 2, 10.5, "#6E7B8E");
        Rect(dc, w - searchWidth - 10, y + 4, searchWidth, boxHeight, "#FFFFFF", 4, "#DFE5ED");
        Icon(dc, "search", w - searchWidth, y + (h - 13) / 2, 13, "#98A5B6"); Text(dc, "搜索 设计资料", w - searchWidth + 23, y + (h - 13) / 2, 10, "#9AA5B4");
        Line(dc, 1, y + h, w - 1, y + h);
    }
    static void CommandBar(DrawingContext dc, double w, double y)
    {
        Rect(dc, 1, y, w - 2, 35, "#FFFFFF"); Icon(dc, "new", 14, y + 8, 18); Text(dc, "新建", 39, y + 10, 10.5);
        ShapeAt(dc, "M 0,0 L 3,3 L 6,0", 72, y + 15, "#8D98A8"); Line(dc, 87, y + 9, 87, y + 27);
        var kinds = new[] { "cut", "copy", "paste", "rename", "share", "delete" };
        for (int i = 0; i < kinds.Length; i++) Icon(dc, kinds[i], 102 + i * 38, y + 8, 18, "#7D8FA6");
        Line(dc, 338, y + 9, 338, y + 27);
        Text(dc, "排序", 356, y + 10, 10.5, "#6B778A"); ShapeAt(dc, "M 0,0 L 3,3 L 6,0", 390, y + 16, "#8998AB");
        Text(dc, "查看", 425, y + 10, 10.5, "#6B778A"); ShapeAt(dc, "M 0,0 L 3,3 L 6,0", 458, y + 16, "#8998AB");
        for (int i = 0; i < 3; i++) dc.DrawEllipse(B("#7B899B"), null, new(505 + i * 5, y + 18), 1, 1);
        Line(dc, 1, y + 35, w - 1, y + 35);
    }
    static void Ribbon(DrawingContext dc, double w)
    {
        Rect(dc, 1, 47, w - 2, 55, "#FBFCFE");
        Icon(dc, "paste", 18, 51, 23); Text(dc, "粘贴", 18, 77, 9.5);
        Icon(dc, "cut", 62, 52, 14); Text(dc, "剪切", 83, 51, 9.5); Icon(dc, "copy", 62, 73, 14); Text(dc, "复制", 83, 72, 9.5);
        Icon(dc, "copy", 123, 52, 14); Text(dc, "复制路径", 120, 73, 8.5, "#7E8A9C");
        foreach (var x in new[] { 169, 323, 430, 543 }) Line(dc, x, 53, x, 93, "#E2E7EF");
        var kinds = new[] { "folder", "copy", "delete", "rename" }; var names = new[] { "移动到", "复制到", "删除", "重命名" };
        for (int i = 0; i < 4; i++) { Icon(dc, kinds[i], 182 + i * 35, 53, 19, "#8396AB"); Text(dc, names[i], 176 + i * 35, 77, 9, "#68788B"); }
        Icon(dc, "new", 340, 51, 24); Text(dc, "新建文件夹", 331, 77, 9.5); Icon(dc, "document", 392, 53, 17); Text(dc, "新建项", 383, 77, 9);
        Icon(dc, "details", 448, 52, 23); Text(dc, "属性", 449, 77, 9.5); Icon(dc, "folder", 490, 53, 17); Text(dc, "打开", 511, 54, 9.5); Text(dc, "编辑", 490, 77, 9.5, "#8693A5");
        Icon(dc, "check", 558, 54, 15, Blue); Text(dc, "全部选择", 581, 53, 9.5); Text(dc, "取消选择", 581, 76, 9.5, "#8591A2");
        foreach (var label in new[] { ("剪贴板", 83.0), ("组织", 239.0), ("新建", 365.0), ("打开", 475.0), ("选择", 597.0) }) Text(dc, label.Item1, label.Item2 - 14, 90, 8, "#9AA4B2");
        Line(dc, 1, 102, w - 1, 102);
    }
    static void ExplorerFiles(DrawingContext dc, double w, double y)
    {
        Rect(dc, 1, y, 139, 173 - y, "#F8FAFD"); Line(dc, 140, y, 140, 173);
        Rect(dc, 8, y + 7, 124, 23, "#EAF1FC", 4); Icon(dc, "folder", 23, y + 11, 14); Text(dc, "文档", 47, y + 11, 10, "#5476A8");
        if (y < 112) { Icon(dc, "photo", 23, y + 40, 14); Text(dc, "图片", 47, y + 40, 10, "#929EAF"); }
        Text(dc, "名称", 161, y + 3, 9, "#97A1AF"); Text(dc, "修改日期", w * .60, y + 3, 9, "#97A1AF"); Text(dc, "类型", w - 124, y + 3, 9, "#97A1AF");
        Line(dc, 149, y + 20, w - 8, y + 20, "#EDF0F5");
        Icon(dc, "folder", 161, y + 25, 15); Text(dc, "视觉参考", 184, y + 25, 10, "#6B7B91"); Text(dc, "2026/9/11  09:41", w * .60, y + 26, 9, "#A0AABA"); Text(dc, "文件夹", w - 124, y + 26, 9, "#A0AABA");
        if (y < 112) { Icon(dc, "document", 161, y + 48, 15); Text(dc, "项目说明.txt", 184, y + 48, 10, "#8997AA"); Text(dc, "2026/9/10  16:20", w * .60, y + 49, 9, "#B0B9C6"); Text(dc, "文本文档", w - 124, y + 49, 9, "#A0AABA"); }
    }
    void DrawContextMenu(DrawingContext dc, double w)
    {
        bool classic = !Before && Options.ClassicContextMenu;
        Frame(dc, w, 174, "#F5F7FB"); double x = 14, y = 9, menuW = 296;
        Rect(dc, x + 1, y + 3, menuW, 154, "#0C243951", classic ? 4 : 9);
        Rect(dc, x, y, menuW, 154, "#FFFFFF", classic ? 3 : 8, "#DAE1EB");
        if (classic)
        {
            var rows = new[] { ("folder", "打开", ""), ("folder", "在新窗口中打开", ""), ("share", "发送到", "›"), ("cut", "剪切", "Ctrl+X"), ("copy", "复制", "Ctrl+C"), ("rename", "重命名", "F2"), ("details", "属性", "Alt+Enter") };
            for (int i = 0; i < rows.Length; i++)
            {
                double rowY = y + 7 + i * 20;
                if (i == 0) Rect(dc, x + 4, rowY - 2, menuW - 8, 20, "#EEF4FD", 2);
                if (i is 2 or 6) Line(dc, x + 31, rowY - 3, x + menuW - 10, rowY - 3, "#E9EDF3");
                Icon(dc, rows[i].Item1, x + 10, rowY + 1, 14, "#8A98AA"); Text(dc, rows[i].Item2, x + 33, rowY, 10.5, "#506077", i == 0);
                Text(dc, rows[i].Item3, x + menuW - 71, rowY + 1, 9, "#A1AAB7");
            }
        }
        else
        {
            var kinds = new[] { "cut", "copy", "rename", "share", "delete" };
            for (int i = 0; i < 5; i++) Icon(dc, kinds[i], x + 20 + i * 53, y + 9, 18, "#7E8FA5");
            Line(dc, x + 9, y + 35, x + menuW - 9, y + 35);
            var rows = new[] { ("folder", "打开"), ("new", "在新标签页中打开"), ("pin", "固定到快速访问"), ("details", "属性"), ("arrow", "显示更多选项") };
            for (int i = 0; i < rows.Length; i++)
            {
                double rowY = y + 41 + i * 21;
                if (i == 0) Rect(dc, x + 5, rowY - 2, menuW - 10, 20, "#F0F5FC", 4);
                if (i == 4) Line(dc, x + 12, rowY - 3, x + menuW - 12, rowY - 3);
                Icon(dc, rows[i].Item1, x + 13, rowY + 1, 14, "#8998AD"); Text(dc, rows[i].Item2, x + 38, rowY, 10.5, "#596A80");
            }
        }
        double infoX = 345, infoW = w - infoX - 22;
        Icon(dc, "folder", infoX, 19, 30); Text(dc, "设计资料", infoX + 43, 20, 12, "#50627B", true); Text(dc, "文件夹的右键菜单", infoX + 43, 40, 9.5, "#9BA6B5");
        Line(dc, infoX, 69, w - 23, 69, "#E1E7F0");
        Text(dc, classic ? "完整命令，直接展开" : "Windows 11 菜单结构", infoX, 82, 12, "#596E8C", true, infoW);
        Text(dc, classic ? "发送到、复制、重命名与属性直接可见。" : "更多 Shell 扩展收在“显示更多选项”中。", infoX, 106, 10.5, "#8C9AAE", width: infoW);
        if (classic && Options.ClassicMenuWithCtrl)
        {
            Rect(dc, infoX, 134, 37, 24, "#FFFFFF", 5, "#D8E1EF"); Text(dc, "Ctrl", infoX + 8, 139, 10, "#6B809E");
            Text(dc, "+ 右键  ·  临时使用 Windows 11 菜单", infoX + 46, 139, 10, "#8D9DB4", width: infoW - 46);
        }
        else Text(dc, classic ? "Ctrl 临时切换已关闭" : "点击“显示更多选项”展开完整命令", infoX, 140, 10, "#8D9DB4", width: infoW);
    }
}

