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
    bool ClassicMenuWithCtrl = true, bool UseClassicNavigationBar = false)
{
    public static readonly int[] IconSizes = [16, 20, 24, 28, 32];
    public static readonly int[] TaskbarHeights = [40, 44, 48, 52, 56, 64];
    public static readonly int[] ButtonWidths = [32, 36, 40, 44, 48, 52, 56, 60, 64];
    public void Validate()
    {
        if (!IconSizes.Contains(IconSize) || !IconSizes.Contains(SmallIconSize) ||
            !TaskbarHeights.Contains(TaskbarHeight) || !ButtonWidths.Contains(TaskbarButtonWidth) ||
            !ButtonWidths.Contains(SmallTaskbarButtonWidth))
            throw new InvalidDataException("方案中的任务栏尺寸超出可选范围。");
        if (ClassicRibbon && UseClassicNavigationBar)
            throw new InvalidDataException("资源管理器不能同时选择两种工具栏样式。");
    }
    [JsonIgnore] public ShellPreviewOptions PreviewOptions => new(ClassicRibbon, StartOnLeft, IconSize, TaskbarHeight,
        ClassicContextMenu, TaskbarButtonWidth: TaskbarButtonWidth, SmallIconSize: SmallIconSize,
        SmallTaskbarButtonWidth: SmallTaskbarButtonWidth, OtherSystemButtonsOnLeft: OtherSystemButtonsOnLeft,
        StartMenuOnLeft: StartMenuOnLeft, SearchMenuOnLeft: SearchMenuOnLeft,
        ClassicMenuWithCtrl: ClassicMenuWithCtrl, UseClassicNavigationBar: UseClassicNavigationBar);
}

public sealed record ShellProfileSnapshot(ShellProfile Profile, string Revision);

public static class ShellProfileFile
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicDesk", "ShellProfile.json");
    public static ShellProfileSnapshot Load(string path)
    {
        if (!File.Exists(path)) return new(new(), "missing");
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("方案文件异常过大，已保留原文件。");
        var bytes = File.ReadAllBytes(path);
        var profile = JsonSerializer.Deserialize<ShellProfile>(bytes) ?? throw new InvalidDataException("方案文件为空。");
        profile.Validate(); return new(profile, Convert.ToHexString(SHA256.HashData(bytes)));
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
