using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ClassicDesk;

public enum ShellOverviewMode { Desktop, Maximized, SmallIcons, StartMenu, Search, ContextMenu, CtrlContextMenu }

/// <summary>Original, profile-driven desktop illustration. Never reads or changes the Windows shell.</summary>
public sealed class ShellProfileOverview : FrameworkElement
{
    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(nameof(Profile), typeof(ShellProfile), typeof(ShellProfileOverview),
        new FrameworkPropertyMetadata(new ShellProfile(), FrameworkPropertyMetadataOptions.AffectsRender), value => value is ShellProfile);
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(nameof(Mode), typeof(ShellOverviewMode), typeof(ShellProfileOverview),
        new FrameworkPropertyMetadata(ShellOverviewMode.Desktop, FrameworkPropertyMetadataOptions.AffectsRender), value => value is ShellOverviewMode mode && Enum.IsDefined(mode));
    public static readonly DependencyProperty ThumbnailProperty = DependencyProperty.Register(nameof(Thumbnail), typeof(bool), typeof(ShellProfileOverview),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public ShellProfile Profile { get => (ShellProfile)GetValue(ProfileProperty); set => SetValue(ProfileProperty, value); }
    public ShellOverviewMode Mode { get => (ShellOverviewMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public bool Thumbnail { get => (bool)GetValue(ThumbnailProperty); set => SetValue(ThumbnailProperty, value); }

    bool Layout => !Profile.SkipTaskbarLayout;
    bool Sizing => !Profile.SkipTaskbarSizing;
    bool AllLeft => Layout && Profile.LeftAlignedApps;
    bool StartLeft => Layout && (Profile.StartOnLeft || Profile.LeftAlignedApps);
    bool OtherLeft => AllLeft || (StartLeft && Profile.OtherSystemButtonsOnLeft);
    bool MenuPositioning => Layout && Profile.StartOnLeft && !AllLeft;
    bool MenuLeft => MenuPositioning && Profile.StartMenuOnLeft;
    bool SearchLeft => MenuLeft && Profile.SearchMenuOnLeft;
    bool Small => Mode == ShellOverviewMode.SmallIcons;
    bool ModernMenu => !Profile.ClassicContextMenu || (Mode == ShellOverviewMode.CtrlContextMenu && Profile.ClassicMenuWithCtrl);
    bool CompactTray => Layout && Profile.CompactTray;
    bool TranslucentTaskbar => Layout && Profile.TranslucentTaskbar;
    bool ClearBar => TranslucentTaskbar && !(Mode == ShellOverviewMode.Maximized && Profile.FollowMaximizedTheme);
    int IconSize => Sizing ? Math.Clamp(Small ? Profile.SmallIconSize : Profile.IconSize, 12, 40) : 32;
    int ButtonWidth => Sizing ? Math.Clamp(Small ? Profile.SmallTaskbarButtonWidth : Profile.TaskbarButtonWidth, 28, 80) : 48;
    int BarHeight => Sizing ? Math.Clamp(Profile.TaskbarHeight, 32, 64) : 48;
    ShellSkin Skin => ShellSkins.All.FirstOrDefault(skin => skin.Id == Profile.Skin) ?? ShellSkins.All[0];
    string ExplorerName => Profile.ClassicRibbon ? "经典功能区" : Profile.UseClassicNavigationBar ? "经典导航栏" : "现代命令栏";
    string LayoutName => Layout ? ShellPresets.TaskbarLayoutName(Profile) : "布局未启用 · 系统居中参考";
    public string Summary => $"{LayoutName} · {ExplorerName} · {(Profile.ClassicContextMenu ? "完整右键菜单" : "现代右键菜单")} · {Skin.Name}";
    public string Explanation
    {
        get
        {
            var notes = new List<string>();
            if (Mode == ShellOverviewMode.SmallIcons) notes.Add($"小图标场景：图标 {IconSize} px、按钮 {ButtonWidth} px；系统的小图标状态由 Windows 控制");
            else if (Mode == ShellOverviewMode.Maximized) notes.Add("最大化场景用于查看任务栏在窗口最大化或全屏时的透明状态");
            else if (Mode == ShellOverviewMode.StartMenu) notes.Add("开始场景只示意系统开始菜单的定位，不启用独立菜单或启动程序");
            else if (Mode == ShellOverviewMode.Search) notes.Add("搜索场景演示从任务栏搜索入口进入时的定位");
            if (!Layout) notes.Add("布局增强未启用：位置保留，图中居中位置仅作参考");
            if (!Sizing) notes.Add("尺寸增强未启用：保留系统尺寸，图中使用 32 / 48 / 48 参考值");
            if (AllLeft) notes.Add("全靠左使用系统原生菜单定位；开始/搜索弹层位置选项不适用");
            else if (!MenuPositioning) notes.Add("开始/搜索弹层位置需启用布局增强并将开始按钮靠左");
            else if (!Profile.StartMenuOnLeft) notes.Add("开始菜单未靠左，搜索所有入口靠左选项不生效");
            else notes.Add(Profile.SearchMenuOnLeft ? "开始与搜索所有入口均靠左" : "开始及从开始进入的搜索靠左；任务栏搜索随系统居中");
            if (Mode == ShellOverviewMode.CtrlContextMenu) notes.Add(!Profile.ClassicContextMenu ? "完整菜单未启用，Ctrl 不触发临时切换" : Profile.ClassicMenuWithCtrl ? "按住 Ctrl 右键临时显示现代菜单" : "Ctrl 临时切换已关闭，仍显示完整菜单");
            if (!Layout && (Profile.CompactTray || Profile.TranslucentTaskbar || Profile.FollowMaximizedTheme)) notes.Add("紧凑托盘、透明及最大化不透明参数仍保留；本次布局增强未启用，这些效果不生效");
            if (TranslucentTaskbar) notes.Add(Profile.FollowMaximizedTheme ? "普通桌面透明；最大化或全屏时不透明" : "普通与最大化场景均透明");
            else if (Layout && Profile.FollowMaximizedTheme) notes.Add("最大化时不透明需先启用透明任务栏");
            return string.Join("；", notes) + "。原创示意，不代表已应用到 Windows。";
        }
    }

    const double WidthUnits = 960;
    bool Condensed => !Thumbnail && ActualHeight > 0 && ActualHeight < 210;
    double NaturalHeightUnits => Thumbnail ? 400 : 480;
    double HeightUnits => Condensed && ActualWidth > 0 ? Math.Clamp(WidthUnits * ActualHeight / ActualWidth, 190, 480) : NaturalHeightUnits;
    static readonly Typeface Face = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface Strong = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    const string Ink = "#34445E", Muted = "#70829A", Edge = "#DDE5EF";
    public ShellProfileOverview()
    {
        ClipToBounds = true; UseLayoutRounding = true; SnapsToDevicePixels = true; IsHitTestVisible = false;
        System.Windows.Automation.AutomationProperties.SetName(this, "当前方案整体布局示意");
        System.Windows.Automation.AutomationProperties.SetHelpText(this, "方案参数驱动的原创桌面示意，支持普通、小图标、最大化及弹层场景；不是实机截图。");
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? (Thumbnail ? 280 : 760) : Math.Max(0, availableSize.Width);
        var height = width * NaturalHeightUnits / WidthUnits;
        return new(width, double.IsInfinity(availableSize.Height) ? height : Math.Min(height, Math.Max(0, availableSize.Height)));
    }
    static Color C(string color) => (Color)ColorConverter.ConvertFromString(color);
    static Brush B(string color) => new SolidColorBrush(C(color));
    static Pen P(string color, double thickness = 1) => new(B(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
    static void Box(DrawingContext dc, double x, double y, double w, double h, string fill, double radius = 0, string? edge = null) =>
        dc.DrawRoundedRectangle(B(fill), edge is null ? null : P(edge), new(x, y, Math.Max(0, w), Math.Max(0, h)), radius, radius);
    static void Line(DrawingContext dc, double x, double y, double x2, double y2, string color = Edge, double thickness = 1) => dc.DrawLine(P(color, thickness), new(x, y), new(x2, y2));
    static void Text(DrawingContext dc, string text, double x, double y, double size = 12, string color = Ink, bool bold = false, double width = 0)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, bold ? Strong : Face, size, B(color), 1);
        if (width > 0) { formatted.MaxTextWidth = width; formatted.MaxLineCount = 1; formatted.Trimming = TextTrimming.CharacterEllipsis; }
        dc.DrawText(formatted, new(x, y));
    }
    static void Icon(DrawingContext dc, string kind, double x, double y, double size, string color = Ink) => ShellPreview.Icon(dc, kind, x, y, size, color);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        // Fill letterboxing as well as the illustration, so an underlying legacy preview cannot leak through.
        dc.DrawRectangle(B(Skin.Sidebar), null, new Rect(0, 0, ActualWidth, ActualHeight));
        double scale = Math.Min(ActualWidth / WidthUnits, ActualHeight / HeightUnits);
        dc.PushTransform(new TranslateTransform((ActualWidth - WidthUnits * scale) / 2, (ActualHeight - HeightUnits * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, WidthUnits, HeightUnits), 16, 16));
        Backdrop(dc);
        double barY = HeightUnits - BarHeight;
        if (Condensed)
        {
            Text(dc, AppearanceTitle() + " · " + ExplorerName, 22, 10, 13, "#576C89", true);
            Text(dc, (!Layout ? "布局未启用 · " : "") + (!Sizing ? "尺寸未启用 · 系统参考" : $"图标 {IconSize} / 按钮 {ButtonWidth} / 栏高 {BarHeight}"), 580, 10, 12, Layout && Sizing ? Muted : "#997437", width: 358);
        }
        else if (!Thumbnail)
        {
            Text(dc, "CLASSICDESK / 方案桌面", 28, 22, 13, "#576C89", true);
            Text(dc, Skin.Name + " · " + AppearanceTitle(), 28, 43, 11, Muted);
            Badge(dc, 606, 20, Layout ? "布局增强" : "布局未启用", Layout);
            Badge(dc, 717, 20, Sizing ? "尺寸增强" : "尺寸未启用", Sizing);
            Badge(dc, 828, 20, ModeTitle(), true);
            Icon(dc, "folder", 30, 105, 31); Text(dc, "项目资料", 22, 143, 11, Muted);
            Icon(dc, "photo", 30, 182, 31); Text(dc, "图片", 37, 220, 11, Muted);
        }
        if (Thumbnail && (!Layout || !Sizing))
            Text(dc, (!Layout ? "布局未启用" : "") + (!Layout && !Sizing ? " · " : "") + (!Sizing ? "尺寸未启用" : ""), 67, 9, 15, "#8B6934", true);
        if (Condensed) CompactExplorer(dc, barY); else Explorer(dc, barY);
        if (!Thumbnail && Mode is not (ShellOverviewMode.StartMenu or ShellOverviewMode.Search or ShellOverviewMode.Maximized))
        {
            if (Condensed) CompactMenu(dc, barY); else DrawContextMenu(dc, barY);
        }
        if (Mode is ShellOverviewMode.StartMenu or ShellOverviewMode.Search)
        {
            if (Condensed) CompactPopup(dc, barY, Mode == ShellOverviewMode.Search); else Popup(dc, barY, Mode == ShellOverviewMode.Search);
        }
        Taskbar(dc, barY);
        if (!Thumbnail && !Condensed && Mode is not (ShellOverviewMode.Maximized or ShellOverviewMode.StartMenu or ShellOverviewMode.Search))
            Text(dc, "原创桌面示意 · 未读取系统状态", 120, barY - 23, 10.5, "#6D809B");
        dc.Pop(); dc.Pop(); dc.Pop();
    }
    string AppearanceTitle() => Profile.Appearance switch { "win11" => "Win11 方案", "compact" => "紧凑办公", "cupertino" => "宽松布局", _ => "Win10 方案" };
    string ModeTitle() => Mode switch { ShellOverviewMode.Maximized => "最大化", ShellOverviewMode.SmallIcons => "小图标", ShellOverviewMode.StartMenu => "开始菜单", ShellOverviewMode.Search => "搜索入口", ShellOverviewMode.ContextMenu => "右键菜单", ShellOverviewMode.CtrlContextMenu => "Ctrl + 右键", _ => "普通桌面" };
    static void Badge(DrawingContext dc, double x, double y, string label, bool enabled)
    {
        Box(dc, x, y, 100, 25, enabled ? "#BFFFFFFF" : "#ECFFF4DD", 12, enabled ? "#85FFFFFF" : "#E7CF9C");
        Text(dc, label, x + 10, y + 5, 10.5, enabled ? "#586C8B" : "#997437");
    }
    void Backdrop(DrawingContext dc)
    {
        dc.DrawRectangle(new LinearGradientBrush(C(Skin.Sidebar), C(Skin.Accent), 55), null, new(0, 0, WidthUnits, HeightUnits));
        // Self-authored abstract folds use the skin palette; no third-party desktop imagery.
        for (int i = 0; i < 4; i++)
        {
            dc.PushTransform(new RotateTransform(-28 + i * 17, 700, 265));
            dc.DrawEllipse(new LinearGradientBrush(C("#90FFFFFF"), C(Skin.Accent), i * 32), P("#48FFFFFF"), new(750 + i * 20, 170), 200 - i * 24, 340 - i * 28);
            dc.Pop();
        }
        dc.DrawRectangle(new LinearGradientBrush(C("#70FFFFFF"), C("#15FFFFFF"), 90), null, new(0, 0, WidthUnits, HeightUnits));
    }
    void Explorer(DrawingContext dc, double barY)
    {
        bool max = Mode == ShellOverviewMode.Maximized;
        double x = max ? 0 : Thumbnail ? 65 : 115, y = max ? 62 : Thumbnail ? 32 : 82;
        double w = max ? WidthUnits : Thumbnail ? 780 : 680, h = max ? barY - y : Thumbnail ? barY - y - 34 : barY - y - 54;
        Box(dc, x + 3, y + 6, w, h, "#1621314A", 11);
        Box(dc, x, y, w, h, "#FEFFFFFF", max ? 0 : 10, "#BFFFFFFF");
        dc.PushClip(new RectangleGeometry(new Rect(x, y, w, h), max ? 0 : 10, max ? 0 : 10));
        Box(dc, x, y, w, 31, Skin.Sidebar);
        if (Profile.ClassicRibbon)
        {
            Icon(dc, "folder", x + 12, y + 7, 17); Text(dc, "项目资料", x + 39, y + 8, 11, Ink, true);
        }
        else
        {
            Box(dc, x + 8, y + 5, 177, 27, "#FFFFFF", 6); Icon(dc, "folder", x + 17, y + 10, 15);
            Text(dc, "项目资料", x + 42, y + 10, 11); Text(dc, "×", x + 162, y + 8, 13, Muted);
            Text(dc, "+", x + 198, y + 6, 18, Muted);
        }
        Text(dc, "−", x + w - 97, y + 5, 19, Muted); Box(dc, x + w - 63, y + 12, 9, 8, "#00FFFFFF", 0, "#8594AB");
        Line(dc, x + w - 29, y + 11, x + w - 20, y + 20, "#8594AB"); Line(dc, x + w - 29, y + 20, x + w - 20, y + 11, "#8594AB");
        double contentY;
        if (Profile.ClassicRibbon)
        {
            Box(dc, x, y + 31, w, 25, "#FBFCFE"); Box(dc, x, y + 31, 57, 25, Skin.Accent);
            Text(dc, "文件", x + 16, y + 36, 11, "#FFFFFF"); Text(dc, "主页     共享     查看", x + 74, y + 36, 11, Ink);
            Box(dc, x, y + 56, w, 58, "#F9FBFE");
            string[] kinds = ["paste", "cut", "copy", "folder", "delete", "rename", "new", "details", "check"];
            string[] names = ["粘贴", "剪切", "复制", "移动到", "删除", "重命名", "新建", "属性", "选择"];
            for (int i = 0; i < kinds.Length; i++) { double ix = x + 20 + i * 62; Icon(dc, kinds[i], ix + 7, y + 61, 23); Text(dc, names[i], ix, y + 90, 10, Muted); }
            Line(dc, x + 193, y + 62, x + 193, y + 104); Line(dc, x + 378, y + 62, x + 378, y + 104);
            Address(dc, x, y + 114, w); contentY = y + 150;
        }
        else if (Profile.UseClassicNavigationBar)
        {
            Commands(dc, x, y + 31, w, true); Address(dc, x, y + 67, w); contentY = y + 103;
        }
        else
        {
            Address(dc, x, y + 31, w); Commands(dc, x, y + 67, w, false); contentY = y + 103;
        }
        Box(dc, x, contentY, 144, y + h - contentY, Skin.Sidebar); Line(dc, x + 144, contentY, x + 144, y + h);
        for (int i = 0; i < 4 && contentY + 21 + i * 28 < y + h - 14; i++)
        {
            if (i == 0) Box(dc, x + 8, contentY + 10, 128, 25, "#E3ECFA", 5);
            Icon(dc, i == 2 ? "photo" : "folder", x + 20, contentY + 16 + i * 28, 15);
            if (!Thumbnail) Text(dc, new[] { "项目资料", "文档", "图片", "此电脑" }[i], x + 45, contentY + 16 + i * 28, 11, Muted);
            else Box(dc, x + 46, contentY + 21 + i * 28, 65 - i * 6, 4, "#BDCDDF", 2);
        }
        Text(dc, Thumbnail ? "" : "名称                          修改日期               类型", x + 161, contentY + 9, 10, Muted);
        Line(dc, x + 157, contentY + 30, x + w - 16, contentY + 30);
        for (int i = 0; i < 4 && contentY + 42 + i * 30 < y + h - 12; i++)
        {
            double fy = contentY + 39 + i * 30;
            if (i == 0) Box(dc, x + 156, fy - 4, w - 171, 27, "#EAF1FD", 4);
            Icon(dc, i == 0 ? "folder" : i == 1 ? "photo" : "document", x + 166, fy, 17);
            if (Thumbnail) { Box(dc, x + 193, fy + 6, 105 - i * 7, 4, "#BCCDE2", 2); Box(dc, x + w - 195, fy + 6, 92, 4, "#DCE4EF", 2); }
            else { Text(dc, new[] { "视觉参考", "概念设计.png", "项目说明.txt", "制作清单" }[i], x + 193, fy + 1, 11); Text(dc, "2026/10/01", x + w - 196, fy + 1, 10, Muted); }
        }
        dc.Pop();
        if (!Thumbnail && !max) Text(dc, ExplorerName + (Profile.ClassicRibbon ? " · 完整工具区" : " · 保留标签页"), x + 12, y + h + 9, 11, "#506684", true);
    }
    static void Address(DrawingContext dc, double x, double y, double w)
    {
        Box(dc, x, y, w, 36, "#FAFCFF"); Text(dc, "‹   ›   ↑", x + 15, y + 7, 16, Muted);
        Box(dc, x + 94, y + 5, w - 288, 26, "#FFFFFF", 4, Edge);
        Icon(dc, "folder", x + 105, y + 11, 14); Text(dc, "此电脑  ›  文档  ›  项目资料", x + 130, y + 10, 10.5, Muted, width: w - 332);
        Box(dc, x + w - 181, y + 5, 168, 26, "#FFFFFF", 4, Edge); Icon(dc, "search", x + w - 168, y + 12, 13, Muted);
        Text(dc, "搜索项目资料", x + w - 145, y + 10, 10.5, "#91A0B4"); Line(dc, x, y + 36, x + w, y + 36);
    }
    static void Commands(DrawingContext dc, double x, double y, double w, bool classic)
    {
        Box(dc, x, y, w, 36, "#FFFFFF"); Icon(dc, "new", x + 14, y + 9, 18); Text(dc, "新建", x + 40, y + 10, 11);
        Line(dc, x + 86, y + 8, x + 86, y + 28);
        string[] kinds = ["cut", "copy", "paste", "rename", "share", "delete"];
        for (int i = 0; i < kinds.Length; i++) Icon(dc, kinds[i], x + 101 + i * 39, y + 9, 18, Muted);
        Text(dc, "排序  ⌄", x + 357, y + 10, 11, Muted); Text(dc, "查看  ⌄", x + 434, y + 10, 11, Muted);
        Text(dc, classic ? "属性" : "•••", x + w - 65, y + 10, 11, Muted); Line(dc, x, y + 36, x + w, y + 36);
    }
    void Taskbar(DrawingContext dc, double y)
    {
        // Full-width Windows taskbar: shape is independent of the cosmetic preset name.
        Box(dc, 0, y, WidthUnits, BarHeight, ClearBar ? "#69FFFFFF" : "#F8FAFE");
        Line(dc, 0, y, WidthUnits, y, ClearBar ? "#BCFFFFFF" : "#DCE4F0");
        double middle = y + BarHeight / 2.0, bw = ButtonWidth, size = Math.Min(IconSize, BarHeight - 6);
        int count = 4 + (StartLeft ? 0 : 1) + (OtherLeft ? 0 : 2);
        double first = AllLeft ? 3 * bw + 10 : WidthUnits / 2 - count * bw / 2;
        double leftX = 10;
        if (StartLeft) { Icon(dc, "start", leftX + (bw - size) / 2, middle - size / 2, size); leftX += bw; }
        if (OtherLeft)
        {
            Icon(dc, "search", leftX + (bw - size) / 2, middle - size / 2, size, Ink); leftX += bw;
            Icon(dc, "taskview", leftX + (bw - size) / 2, middle - size / 2, size);
        }
        int offset = 0;
        if (!StartLeft) { Icon(dc, "start", first + (bw - size) / 2, middle - size / 2, size); offset++; }
        if (!OtherLeft)
        {
            Icon(dc, "search", first + offset++ * bw + (bw - size) / 2, middle - size / 2, size);
            Icon(dc, "taskview", first + offset++ * bw + (bw - size) / 2, middle - size / 2, size);
        }
        string[] apps = ["folder", "browser", "photo", "notes"];
        for (int i = 0; i < apps.Length; i++)
        {
            double ax = first + (offset + i) * bw;
            if (i == 0) Box(dc, ax + 2, y + 3, bw - 4, BarHeight - 6, "#AAFFFFFF", 5);
            Icon(dc, apps[i], ax + (bw - size) / 2, middle - size / 2, size);
            if (i == 0) Box(dc, ax + bw / 2 - 7, y + BarHeight - 4, 14, 2, Skin.Accent, 1);
        }
        double trayWidth = CompactTray ? 124 : 186, trayX = WidthUnits - trayWidth;
        double gap = CompactTray ? 21 : 32;
        Text(dc, "⌃", trayX + 4, middle - 10, 16, Ink);
        dc.PushTransform(new TranslateTransform(trayX + gap, middle - 7));
        dc.DrawGeometry(null, P(Ink, 1.3), Geometry.Parse("M 0,4 Q 7,-2 14,4 M 3,7 Q 7,3 11,7 M 6,10 Q 7,8 8,10"));
        dc.DrawEllipse(B(Ink), null, new(7, 12), .8, .8); dc.Pop();
        dc.PushTransform(new TranslateTransform(trayX + gap * 2, middle - 7));
        dc.DrawGeometry(B(Ink), null, Geometry.Parse("M 0,5 L 4,5 L 8,1 L 8,13 L 4,9 L 0,9 Z"));
        dc.DrawGeometry(null, P(Ink, 1.2), Geometry.Parse("M 11,4 Q 15,7 11,10")); dc.Pop();
        Text(dc, "09:41", WidthUnits - 57, middle - 15, 10.5); Text(dc, "10/01", WidthUnits - 57, middle + 1, 10, Muted);
        if (!Thumbnail && !Condensed && Mode is not (ShellOverviewMode.StartMenu or ShellOverviewMode.Search))
        {
            string details = Sizing ? $"{(Small ? "小图标" : "图标")} {IconSize} px / 按钮 {ButtonWidth} px / 栏高 {BarHeight} px" : "尺寸未启用 · 系统参考";
            Text(dc, details, 330, y - 23, 10.5, "#607695", width: 306);
            Text(dc, CompactTray ? "紧凑托盘" : "标准托盘", trayX, y - 23, 10.5, "#607695");
            string material = ClearBar ? "透明" : TranslucentTaskbar && Profile.FollowMaximizedTheme && Mode == ShellOverviewMode.Maximized ? "最大化 · 不透明" : "不透明";
            Text(dc, material, 824, y - 23, 10.5, "#607695", width: 128);
        }
    }
    void CompactExplorer(DrawingContext dc, double barY)
    {
        double x = 25, y = 34, w = Mode == ShellOverviewMode.Maximized ? 910 : 685, h = barY - y - 9;
        Box(dc, x + 3, y + 4, w, h, "#1621314A", 8); Box(dc, x, y, w, h, "#FEFFFFFF", 8, "#BFFFFFFF");
        dc.PushClip(new RectangleGeometry(new Rect(x, y, w, h), 8, 8));
        Box(dc, x, y, w, 24, Skin.Sidebar); Icon(dc, "folder", x + 12, y + 5, 15);
        Text(dc, "项目资料", x + 36, y + 5, 11);
        if (!Profile.ClassicRibbon) { Box(dc, x + 5, y + 2, 145, 23, "#FFFFFF", 4); Icon(dc, "folder", x + 12, y + 6, 14); Text(dc, "项目资料  ×", x + 36, y + 5, 11); }
        Text(dc, "−    □    ×", x + w - 100, y + 3, 13, Muted);
        if (Profile.ClassicRibbon)
        {
            Box(dc, x, y + 24, w, 19, "#F3F7FD"); Box(dc, x, y + 24, 52, 19, Skin.Accent);
            Text(dc, "文件", x + 13, y + 26, 10, "#FFFFFF"); Text(dc, "主页     共享     查看", x + 66, y + 26, 10);
            string[] tools = ["paste", "cut", "copy", "folder", "delete", "new", "details"];
            for (int i = 0; i < tools.Length; i++) { Icon(dc, tools[i], x + 18 + i * 69, y + 48, 19); Text(dc, new[] { "粘贴", "剪切", "复制", "移动到", "删除", "新建", "属性" }[i], x + 43 + i * 69, y + 51, 10, Muted); }
            Line(dc, x, y + 74, x + w, y + 74); Text(dc, "‹  ›  ↑    此电脑  ›  文档  ›  项目资料", x + 13, y + 79, 11, Muted);
        }
        else
        {
            bool early = Profile.UseClassicNavigationBar;
            double commandY = y + (early ? 24 : 51), addressY = y + (early ? 51 : 24);
            Box(dc, x, addressY, w, 27, "#F6F9FD"); Text(dc, "‹  ›  ↑    此电脑  ›  文档  ›  项目资料", x + 13, addressY + 6, 11, Muted);
            Text(dc, "新建", x + 15, commandY + 6, 11);
            string[] tools = ["cut", "copy", "paste", "rename", "share", "delete"];
            for (int i = 0; i < tools.Length; i++) Icon(dc, tools[i], x + 82 + i * 44, commandY + 5, 17, Muted);
            Text(dc, "排序  ⌄      查看  ⌄", x + 382, commandY + 6, 11, Muted);
            Line(dc, x, commandY + 27, x + w, commandY + 27);
        }
        dc.Pop();
    }
    void CompactMenu(DrawingContext dc, double barY)
    {
        double x = 744, y = 36, w = 191, h = barY - y - 9;
        Box(dc, x + 2, y + 4, w, h, "#1D203550", ModernMenu ? 8 : 3); Box(dc, x, y, w, h, "#FEFFFFFF", ModernMenu ? 8 : 3, "#CCD8E7");
        dc.PushClip(new RectangleGeometry(new Rect(x, y, w, h), ModernMenu ? 8 : 3, ModernMenu ? 8 : 3));
        if (ModernMenu)
        {
            string[] tools = ["cut", "copy", "rename", "share", "delete"];
            for (int i = 0; i < tools.Length; i++) Icon(dc, tools[i], x + 12 + i * 35, y + 9, 15, Muted);
            Line(dc, x + 8, y + 32, x + w - 8, y + 32);
            Text(dc, "打开", x + 17, y + 39, 11); Text(dc, "显示更多选项", x + 17, y + 61, 11, Muted);
        }
        else
        {
            string[] tools = ["folder", "share", "cut", "copy", "details"];
            string[] names = ["打开", "发送到", "剪切", "复制", "属性"];
            for (int i = 0; i < tools.Length; i++) { Icon(dc, tools[i], x + 10, y + 8 + i * 21, 13, Muted); Text(dc, names[i], x + 32, y + 7 + i * 21, 11); }
        }
        dc.Pop();
    }
    void CompactPopup(DrawingContext dc, double barY, bool search)
    {
        bool left = search ? SearchLeft : MenuLeft;
        double w = search ? 450 : 360, x = left || AllLeft ? 10 : (WidthUnits - w) / 2, y = 31, h = barY - 38;
        Box(dc, x + 3, y + 4, w, h, "#25253A55", 9); Box(dc, x, y, w, h, "#FEFAFCFF", 8, "#CBFFFFFF");
        dc.PushClip(new RectangleGeometry(new Rect(x, y, w, h), 8, 8));
        Box(dc, x + 12, y + 10, w - 24, 25, "#FFFFFF", 5, Edge); Icon(dc, "search", x + 23, y + 16, 13, Muted);
        Text(dc, search ? "搜索 · 从任务栏进入" : "开始 · 菜单位置示意", x + 47, y + 16, 11, Muted);
        if (search) { Box(dc, x + 12, y + 44, w - 24, 35, "#E9F0FC", 4); Icon(dc, "folder", x + 24, y + 51, 20); Text(dc, "项目资料 · 合成结果", x + 58, y + 54, 11); }
        else
        {
            string[] tools = ["folder", "browser", "photo", "notes"];
            for (int i = 0; i < tools.Length; i++) Icon(dc, tools[i], x + 27 + i * 78, y + 47, 26);
        }
        dc.Pop();
    }
    void DrawContextMenu(DrawingContext dc, double barY)
    {
        bool modern = ModernMenu;
        double x = 643, y = Math.Min(181, barY - 223), w = 273, h = modern ? 208 : 213;
        Box(dc, x + 3, y + 5, w, h, "#1D203550", modern ? 10 : 4);
        Box(dc, x, y, w, h, "#FEFFFFFF", modern ? 9 : 3, "#CCD8E7");
        if (modern)
        {
            string[] tools = ["cut", "copy", "rename", "share", "delete"];
            for (int i = 0; i < tools.Length; i++) Icon(dc, tools[i], x + 17 + i * 51, y + 13, 18, Muted);
            Line(dc, x + 10, y + 44, x + w - 10, y + 44);
            string[] labels = ["打开", "在新标签页中打开", "固定到快速访问", "属性", "显示更多选项"];
            for (int i = 0; i < labels.Length; i++) { if (i == 0) Box(dc, x + 5, y + 50, w - 10, 27, "#EDF3FC", 4); Icon(dc, i == 4 ? "arrow" : i == 3 ? "details" : "folder", x + 16, y + 56 + i * 29, 15); Text(dc, labels[i], x + 43, y + 55 + i * 29, 11); }
        }
        else
        {
            string[] labels = ["打开", "在新窗口中打开", "发送到                 ›", "剪切", "复制", "重命名", "属性"];
            string[] tools = ["folder", "folder", "share", "cut", "copy", "rename", "details"];
            for (int i = 0; i < labels.Length; i++) { if (i == 0) Box(dc, x + 4, y + 6, w - 8, 27, "#EDF3FC", 2); Icon(dc, tools[i], x + 12, y + 12 + i * 28, 15); Text(dc, labels[i], x + 37, y + 11 + i * 28, 11); }
            Text(dc, "Ctrl+X", x + w - 62, y + 95, 10, Muted); Text(dc, "Ctrl+C", x + w - 62, y + 123, 10, Muted);
        }
        string title = Mode == ShellOverviewMode.CtrlContextMenu ? Profile.ClassicContextMenu && Profile.ClassicMenuWithCtrl ? "Ctrl + 右键 · 临时现代菜单" : "Ctrl + 右键 · 无临时切换" : modern ? "现代右键菜单" : "完整右键菜单";
        Text(dc, title, x + 4, y - 22, 11, "#506684", true, w);
    }
    void Popup(DrawingContext dc, double barY, bool search)
    {
        bool left = search ? SearchLeft : MenuLeft;
        // Full-left deliberately defers to Windows positioning. Its sample follows the native left start.
        bool nativeLeft = AllLeft;
        double w = search ? 495 : 418, h = Math.Min(search ? 296 : 310, barY - 82);
        double x = left || nativeLeft ? 12 : (WidthUnits - w) / 2, y = barY - h - 12;
        Box(dc, x + 4, y + 6, w, h, "#23253A55", 12); Box(dc, x, y, w, h, "#FDF9FBFF", 10, "#CBFFFFFF");
        Box(dc, x + 20, y + 18, w - 40, 32, "#FFFFFF", 6, "#DCE4F0"); Icon(dc, "search", x + 33, y + 26, 16, Muted);
        Text(dc, search ? "搜索：项目资料" : "搜索应用、设置和文档", x + 62, y + 27, 11.5, Muted);
        Text(dc, search ? "搜索 · 从任务栏进入" : "开始 · 系统菜单定位示意", x + 22, y + 68, 14, Ink, true);
        if (search)
        {
            Text(dc, "全部     应用     文档     设置", x + 23, y + 100, 11, Muted); Line(dc, x + 22, y + 125, x + w - 22, y + 125);
            Box(dc, x + 20, y + 142, w - 40, 54, "#E9F0FC", 6); Icon(dc, "folder", x + 36, y + 155, 28);
            Text(dc, "项目资料", x + 79, y + 151, 13, Ink, true); Text(dc, "文件夹 · 合成示例", x + 79, y + 173, 10.5, Muted);
            Icon(dc, "document", x + 39, y + 214, 22); Text(dc, "项目说明.txt", x + 79, y + 217, 11, Muted);
        }
        else
        {
            string[] kinds = ["folder", "browser", "photo", "notes", "details", "document"];
            string[] names = ["文件", "浏览器", "图片", "笔记", "设置", "文档"];
            for (int i = 0; i < kinds.Length; i++) { double ix = x + 43 + i % 3 * 125, iy = y + 106 + i / 3 * 75; Icon(dc, kinds[i], ix + 9, iy, 28); Text(dc, names[i], ix + 7, iy + 36, 11); }
        }
        string position = left ? "靠左定位" : nativeLeft ? "全靠左 · 系统原生定位" : "随系统 · 居中参考";
        Text(dc, position, x + 22, y + h - 29, 10.5, "#61779A", true, w - 44);
        if (!Thumbnail)
        {
            Box(dc, x, y - 29, w, 23, "#EFFFFFFF", 6);
            Text(dc, search && MenuLeft && !Profile.SearchMenuOnLeft ? "从开始进入的搜索靠左；此处演示任务栏入口" : !MenuPositioning && !AllLeft ? "开始/搜索定位增强当前不适用" : search ? "按当前搜索入口参数定位" : "按当前开始菜单参数定位", x + 10, y - 24, 11, "#506684", true, w - 20);
        }
    }
}
