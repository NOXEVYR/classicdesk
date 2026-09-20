using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace ClassicDesk;

/// <summary>A proposed appearance, never a claim about the live Windows shell.</summary>
public sealed record ShellProfile(bool StartOnLeft = true, int IconSize = 24, int TaskbarHeight = 48,
    bool ClassicRibbon = true, bool ClassicContextMenu = true,
    int TaskbarButtonWidth = 44, int SmallIconSize = 16, int SmallTaskbarButtonWidth = 32,
    bool OtherSystemButtonsOnLeft = true, bool StartMenuOnLeft = true, bool SearchMenuOnLeft = false,
    bool ClassicMenuWithCtrl = true, bool UseClassicNavigationBar = false, string Appearance = "win10", string Skin = "classic",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SkipTaskbarLayout = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SkipTaskbarSizing = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool CompactTray = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool TranslucentTaskbar = false,
    // Keep the persisted key for existing profile/journal fingerprints; the new native
    // runtime uses it for opaque foreground apps and a transparent desktop.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool FollowMaximizedTheme = false)
{
    public static readonly int[] IconSizes = [16, 20, 24, 28, 32];
    public static readonly int[] TaskbarHeights = [40, 44, 48, 52, 56, 64];
    public static readonly int[] ButtonWidths = [32, 36, 40, 44, 48, 52, 56, 60, 64];
    public void Validate()
    {
        if (Appearance is not ("win10" or "win11" or "compact" or "cupertino"))
            throw new InvalidDataException("无法识别此布局方案。");
        if (!ShellSkins.All.Any(s => s.Id == Skin))
            throw new InvalidDataException("无法识别此界面皮肤。");
        if (!IconSizes.Contains(IconSize) || !IconSizes.Contains(SmallIconSize) ||
            !TaskbarHeights.Contains(TaskbarHeight) || !ButtonWidths.Contains(TaskbarButtonWidth) ||
            !ButtonWidths.Contains(SmallTaskbarButtonWidth))
            throw new InvalidDataException("方案中的任务栏尺寸超出可选范围。");
        if (ClassicRibbon && UseClassicNavigationBar)
            throw new InvalidDataException("资源管理器不能同时选择两种工具栏样式。");
        if (FollowMaximizedTheme && !TranslucentTaskbar)
            throw new InvalidDataException("窗口明暗跟随需要启用透明任务栏。");
    }
    [JsonIgnore] public ShellPreviewOptions PreviewOptions => new(ClassicRibbon, StartOnLeft, IconSize, TaskbarHeight,
        ClassicContextMenu, TaskbarButtonWidth: TaskbarButtonWidth, SmallIconSize: SmallIconSize,
        SmallTaskbarButtonWidth: SmallTaskbarButtonWidth, OtherSystemButtonsOnLeft: OtherSystemButtonsOnLeft,
        StartMenuOnLeft: StartMenuOnLeft, SearchMenuOnLeft: SearchMenuOnLeft,
        ClassicMenuWithCtrl: ClassicMenuWithCtrl, UseClassicNavigationBar: UseClassicNavigationBar);
}

/// <summary>Per-review choices; projecting a draft never changes its saved values.</summary>
public sealed record ShellFeatureSelection(bool Layout = true, bool Sizing = true, bool Explorer = false, bool ContextMenu = false)
{
    public ShellProfile Apply(ShellProfile draft) => draft with {
        SkipTaskbarLayout = !Layout, SkipTaskbarSizing = !Sizing, CompactTray = Layout && draft.CompactTray, TranslucentTaskbar = Layout && draft.TranslucentTaskbar, FollowMaximizedTheme = Layout && draft.FollowMaximizedTheme,
        ClassicRibbon = Explorer && draft.ClassicRibbon,
        UseClassicNavigationBar = Explorer && draft.UseClassicNavigationBar,
        ClassicContextMenu = ContextMenu && draft.ClassicContextMenu
    };
}

public sealed record ShellProfileSnapshot(ShellProfile Profile, string Revision);

public sealed record ShellPreset(string Name, string Description, ShellProfile Profile);
public static class ShellPresets
{
    public static IReadOnlyList<ShellPreset> All { get; } = Array.AsReadOnly(new[] {
        new ShellPreset("Win10 方案", "经典功能区与完整菜单。开始靠左、应用居中，保留熟悉的操作习惯。", new ShellProfile()),
        new ShellPreset("Win11 方案", "开始与应用居中，使用现代命令栏和简洁右键菜单。", new ShellProfile(StartOnLeft: false, ClassicRibbon: false, ClassicContextMenu: false, Appearance: "win11")),
        new ShellPreset("紧凑办公", "更小的图标与任务栏；经典导航栏保留标签页。", new ShellProfile(IconSize: 20, TaskbarHeight: 40, TaskbarButtonWidth: 36, ClassicRibbon: false, UseClassicNavigationBar: true, Appearance: "compact")),
        new ShellPreset("宽松布局", "28 px 居中图标、更宽按钮与经典导航栏，保留 Windows 操作方式。", new ShellProfile(StartOnLeft: false, IconSize: 28, TaskbarHeight: 56, TaskbarButtonWidth: 56, ClassicRibbon: false, ClassicContextMenu: false, UseClassicNavigationBar: true, Appearance: "cupertino"))
    });
    public static int Index(ShellProfile profile) => All.Select((preset, index) => (preset, index)).First(item => item.preset.Profile.Appearance == profile.Appearance).index;
    public static string DisplayName(ShellProfile profile) => All[Index(profile)].Name;
    public static IReadOnlyList<string> Changes(ShellProfile before, ShellProfile after)
    {
        var names = new[] { "开始按钮位置", "图标大小", "任务栏高度", "功能区", "完整右键菜单", "按钮宽度", "小图标尺寸", "小按钮宽度", "系统按钮位置", "开始菜单位置", "搜索菜单位置", "Ctrl 菜单切换", "经典导航栏", "布局方案", "界面皮肤", "任务栏布局增强", "任务栏尺寸增强", "紧凑系统托盘", "透明任务栏", "应用不透明、桌面透明" };
        var properties = new[] { "StartOnLeft", "IconSize", "TaskbarHeight", "ClassicRibbon", "ClassicContextMenu", "TaskbarButtonWidth", "SmallIconSize", "SmallTaskbarButtonWidth", "OtherSystemButtonsOnLeft", "StartMenuOnLeft", "SearchMenuOnLeft", "ClassicMenuWithCtrl", "UseClassicNavigationBar", "Appearance", "Skin", "SkipTaskbarLayout", "SkipTaskbarSizing", "CompactTray", "TranslucentTaskbar", "FollowMaximizedTheme" };
        return properties.Select((name, i) => (Property: typeof(ShellProfile).GetProperty(name)!, Label: names[i]))
            .Where(item => !Equals(item.Property.GetValue(before), item.Property.GetValue(after))).Select(item => item.Label).ToArray();
    }
}

public sealed record ShellSkin(string Id, string Name, string Description, int Backdrop, string Accent, string Sidebar, string Workspace);
public static class ShellSkins
{
    public static IReadOnlyList<ShellSkin> All { get; } = Array.AsReadOnly(new[] {
        new ShellSkin("classic", "原生浅色", "冷白与雾蓝，清楚、熟悉的 Windows 质感。", 0, "#627CDD", "#F1F4FC", "#F5F5F7"),
        new ShellSkin("frost", "雾白 · 极简", "银白与轻雾紫，克制的留白与柔和层次。", 3, "#8079B3", "#F3F2F8", "#F7F6F9"),
        new ShellSkin("starlight", "星空 · 二次元", "银发与星空，延续原来的二次元插画。", 4, "#9474CF", "#F6F2FC", "#F8F6FC"),
        new ShellSkin("sakura", "樱月 · 二次元", "淡樱、弯月与远山，无人物的粉紫天空。", 5, "#B374A2", "#FCF1F7", "#FCF7FA"),
        new ShellSkin("cloud", "云海 · 二次元", "奶白云海与晴空，无人物的清透雾蓝。", 6, "#537FAC", "#EEF5FC", "#F5F8FC"),
        new ShellSkin("moon", "月夜 · 二次元", "月光、群山与薄雾，无人物的静谧蓝紫。", 7, "#767DB8", "#F0F1FA", "#F5F5FB")
    });
    public static int Index(ShellProfile profile) => All.Select((skin, index) => (skin, index)).First(item => item.skin.Id == profile.Skin).index;
}

public static class ShellProfileFile
{
    public static ShellProfile Import(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 65536) throw new InvalidDataException("方案文件不能超过 64 KB。");
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("请选择 ClassicDesk 导出的方案 JSON。");
        var properties = document.RootElement.EnumerateObject().ToArray();
        var known = typeof(ShellProfile).GetProperties().Where(p => p.Name != nameof(ShellProfile.PreviewOptions)).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (properties.Length == 0 || properties.Any(p => !known.Contains(p.Name)) || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
            throw new InvalidDataException("方案包含未知或重复参数，或没有可识别的设置。");
        return Decode(document.RootElement);
    }
    static ShellProfile Decode(JsonElement root)
    {
        var value = root.Deserialize<ShellProfile>() ?? throw new InvalidDataException("方案内容为空。");
        // Older files combined skin and layout in Appearance. Migrate in memory only;
        // keep every functional parameter and retain the original bytes until Save.
        if (!root.TryGetProperty(nameof(ShellProfile.Skin), out _))
        {
            if (value.Appearance == "starlight") value = value with { Appearance = "cupertino", Skin = "starlight" };
            else if (value.Appearance == "cupertino") value = value with { Skin = "frost" };
        }
        value.Validate(); return value;
    }
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicDesk", "ShellProfile.json");
    public static ShellProfileSnapshot Load(string path)
    {
        if (!File.Exists(path)) return new(new(), "missing");
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("方案文件异常过大，已保留原文件。");
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        var profile = Decode(document.RootElement);
        return new(profile, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    public static ShellProfile Read(string path) => Load(path).Profile;
    public static string Revision(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
    public static string Save(string path, ShellProfile profile, string expectedRevision)
    {
        profile.Validate();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!; Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".shell-profile-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var coordination = new FileStream(path + ".save.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (Revision(path) != expectedRevision) throw new IOException("方案已被其他程序修改，请重新打开后核对。本次编辑仍保留。");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(profile, new JsonSerializerOptions { WriteIndented = true });
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            if (Revision(path) != expectedRevision) throw new IOException("保存前发现方案发生变化，未覆盖原文件。");
            // Preserve the previous local plan. This does not back up or modify Windows settings.
            if (expectedRevision == "missing") File.Move(temporary, path, false);
            else File.Replace(temporary, path, path + ".previous");
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
