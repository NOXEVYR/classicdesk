using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassicDesk;

/// <summary>Design-only workspace. Never passed to the shell activation planner.</summary>
public sealed record TaskbarDesignOptions(string Dock = "bottom", string Combine = "always",
    string Alignment = "separate", int IconSize = 24, int Height = 48, int LabelWidth = 132,
    int EdgeMargin = 8, bool Segmented = false, int TrayGap = 6, string[]? VisibleTrayItems = null)
{
    public static readonly string[] Docks = ["bottom", "top", "left", "right"];
    public static readonly string[] CombineModes = ["always", "whenFull", "neverWithLabels", "neverNoLabels"];
    public static readonly string[] Alignments = ["separate", "center", "left"];
    [JsonIgnore] public IReadOnlyList<string> EffectiveTrayItems => TaskbarDesignTray.Items
        .Where(t => (VisibleTrayItems ?? ["clock", "volume", "network", "input", "showDesktop"]).Contains(t.Id)).Select(t => t.Id).ToArray();
    public void Validate()
    {
        if (!Docks.Contains(Dock) || !CombineModes.Contains(Combine) || !Alignments.Contains(Alignment))
            throw new InvalidDataException("无法识别布局位置、合并方式或对齐方式。");
        if (!ShellProfile.IconSizes.Contains(IconSize) || !ShellProfile.TaskbarHeights.Contains(Height) ||
            LabelWidth < 80 || LabelWidth > 240 || EdgeMargin < 0 || EdgeMargin > 32 || TrayGap < 0 || TrayGap > 16)
            throw new InvalidDataException("布局尺寸超出范围。");
        var source = VisibleTrayItems ?? EffectiveTrayItems;
        if (source.Count > TaskbarDesignTray.Items.Count || source.Any(id => !TaskbarDesignTray.Items.Any(t => t.Id == id)) ||
            source.Distinct(StringComparer.Ordinal).Count() != source.Count)
            throw new InvalidDataException("托盘项目包含未知或重复项。");
    }
}

public sealed record TaskbarDesignTrayItem(string Id, string Name, string Glyph);
public static class TaskbarDesignTray
{
    public static IReadOnlyList<TaskbarDesignTrayItem> Items { get; } = Array.AsReadOnly(new[] {
        new TaskbarDesignTrayItem("search", "搜索", "⌕"), new("taskView", "任务视图", "▤"), new("widgets", "小组件", "▦"),
        new("showDesktop", "显示桌面", "│"), new("actionCenter", "操作中心", "▣"), new("clock", "时钟", "12:30"),
        new("quickSettings", "快速设置", "☷"), new("volume", "音量", "♪"), new("power", "电源", "▰"),
        new("input", "输入指示", "中"), new("network", "网络", "≋"), new("microphone", "麦克风", "♩"),
        new("location", "定位", "◎"), new("touchKeyboard", "触摸键盘", "⌨"), new("pen", "手写笔", "✎"),
        new("other", "其他系统托盘图标", "⋯"), new("bluetooth", "蓝牙设备", "ᛒ")
    });
}

public sealed record TaskbarDesignSnapshot(TaskbarDesignOptions Options, string Revision);
public static class TaskbarDesignProfileFile
{
    const int Limit = 65536;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly HashSet<string> Known = ["Dock", "Combine", "Alignment", "IconSize", "Height", "LabelWidth", "EdgeMargin", "Segmented", "TrayGap", "VisibleTrayItems"];
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicDesk", "TaskbarDesign.json");
    public static string Fingerprint(TaskbarDesignOptions value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value with {
        VisibleTrayItems = TaskbarDesignTray.Items.Where(t => value.EffectiveTrayItems.Contains(t.Id)).Select(t => t.Id).ToArray()
    }, Json)));
    public static string Revision(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(ReadBytes(path))) : "missing";
    static byte[] ReadBytes(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > Limit) throw new InvalidDataException("布局方案不能超过 64 KB，原文件已保留。");
        using var memory = new MemoryStream(); file.CopyTo(memory); return memory.ToArray();
    }
    static TaskbarDesignOptions Decode(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("请选择 ClassicDesk 布局草稿 JSON。");
        var properties = document.RootElement.EnumerateObject().ToArray();
        if (properties.Length == 0 || properties.Any(p => !Known.Contains(p.Name)) || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
            throw new InvalidDataException("布局方案包含未知或重复参数。");
        var result = document.RootElement.Deserialize<TaskbarDesignOptions>() ?? throw new InvalidDataException("布局方案为空。");
        result.Validate(); return result;
    }
    public static TaskbarDesignSnapshot Load(string path)
    {
        if (!File.Exists(path)) return new(new(), "missing");
        var bytes = ReadBytes(path); return new(Decode(bytes), Convert.ToHexString(SHA256.HashData(bytes)));
    }
    public static TaskbarDesignOptions Import(string path) => Decode(ReadBytes(path));
    public static string Save(string path, TaskbarDesignOptions options, string expectedRevision)
    {
        options.Validate(); var bytes = JsonSerializer.SerializeToUtf8Bytes(options, Json);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!; Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, ".layout-design-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var coordination = new FileStream(path + ".save.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (Revision(path) != expectedRevision) throw new IOException("布局方案已被其他程序修改，本次草稿仍保留。");
            if (expectedRevision != "missing") Decode(ReadBytes(path));
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            if (Revision(path) != expectedRevision) throw new IOException("保存前检测到文件变化，原文件已保留。");
            if (expectedRevision == "missing") File.Move(temporary, path, false); else File.Replace(temporary, path, path + ".previous");
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
