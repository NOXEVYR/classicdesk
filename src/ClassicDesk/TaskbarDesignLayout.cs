using System.Windows;

namespace ClassicDesk;

public sealed record TaskbarDesignApp(string AppId, string Title);
public sealed record TaskbarDesignButton(string AppId, string Title, int WindowCount, bool LabelVisible, Rect Bounds);
public sealed record TaskbarDesignResult(Rect Bar, Rect Start, Rect Applications, Rect Tray,
    IReadOnlyList<TaskbarDesignButton> Buttons, bool Combined, int OverflowCount, string Explanation);

/// <summary>Pure, bounded layout calculation over synthetic windows; does not enumerate or move real windows.</summary>
public static class TaskbarDesignLayout
{
    public static IReadOnlyList<TaskbarDesignApp> Examples { get; } = Array.AsReadOnly(new[] {
        new TaskbarDesignApp("explorer", "文档 · 文件资源管理器"), new("explorer", "素材 · 文件资源管理器"),
        new("browser", "项目资料 · 浏览器"), new("browser", "设计参考 · 浏览器"),
        new("editor", "ClassicDesk · 编辑器"), new("notes", "工作笔记")
    });

    public static TaskbarDesignResult Calculate(TaskbarDesignOptions options, IReadOnlyList<TaskbarDesignApp> windows, double width, double height)
    {
        options.Validate(); ArgumentNullException.ThrowIfNull(windows);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 120 || height < 120 || width > 16384 || height > 16384)
            throw new ArgumentOutOfRangeException(nameof(width), "预览屏幕尺寸必须在 120–16384 之间。");
        if (windows.Count > 256 || windows.Any(w => string.IsNullOrEmpty(w.AppId) || w.Title is null))
            throw new ArgumentException("预览最多接受 256 个有应用身份的窗口。", nameof(windows));
        bool vertical = options.Dock is "left" or "right";
        double extent = vertical ? height : width, thickness = options.Height;
        var bar = options.Dock switch { "top" => new Rect(0, 0, width, thickness), "left" => new Rect(0, 0, thickness, height),
            "right" => new Rect(width - thickness, 0, thickness, height), _ => new Rect(0, height - thickness, width, thickness) };
        // Clip margins and the tray to retain a usable start button even in a tiny preview.
        double margin = Math.Min(options.EdgeMargin, (extent - 60) / 2), startWidth = Math.Min(40, extent - 2 * margin);
        double trayRequested = options.EffectiveTrayItems.Count == 0 ? 0 : 6 + options.EffectiveTrayItems.Sum(id => !vertical && id == "clock" ? 56 : 22) + Math.Max(0, options.EffectiveTrayItems.Count - 1) * options.TrayGap;
        double trayWidth = Math.Min(trayRequested, Math.Max(0, (extent - margin * 2 - startWidth - 16) * .55));
        double trayAt = extent - margin - trayWidth, appMinimum = margin + startWidth + 8;
        double capacity = Math.Max(0, trayAt - 8 - appMinimum);
        bool labels = !vertical && options.Combine is "whenFull" or "neverWithLabels";
        double expandedWidth = labels ? options.LabelWidth : Math.Max(36, options.IconSize + 16);
        bool combined = options.Combine == "always" || options.Combine == "whenFull" && windows.Count * expandedWidth > capacity;
        if (combined) labels = false;
        var groups = combined ? windows.GroupBy(w => w.AppId).Select(g => (Id: g.Key, Title: g.First().Title, Count: g.Count())).ToArray()
            : windows.Select(w => (Id: w.AppId, Title: w.Title, Count: 1)).ToArray();
        double buttonWidth = labels ? options.LabelWidth : Math.Max(36, options.IconSize + 16);
        int visible = Math.Min(groups.Length, (int)Math.Floor(capacity / buttonWidth));
        double appLength = visible * buttonWidth, startAt = margin, appsAt = appMinimum;
        if (options.Alignment == "center")
        {
            startAt = Math.Clamp((extent - startWidth - 8 - appLength) / 2, margin, Math.Max(margin, trayAt - 8 - startWidth - 8 - appLength));
            appsAt = startAt + startWidth + 8;
        }
        else if (options.Alignment == "separate") appsAt = Math.Clamp((extent - appLength) / 2, appMinimum, Math.Max(appMinimum, trayAt - 8 - appLength));
        Rect Axis(double at, double length) => vertical ? new Rect(bar.X, at, thickness, length) : new Rect(at, bar.Y, length, thickness);
        var buttons = groups.Take(visible).Select((g, index) => new TaskbarDesignButton(g.Id, g.Title, g.Count, labels, Axis(appsAt + index * buttonWidth, buttonWidth))).ToArray();
        string explanation = combined ? "相同应用按身份合并" : labels ? "每个窗口独立显示文字标签" : "每个窗口独立显示图标";
        if (vertical && options.Combine is "whenFull" or "neverWithLabels") explanation += " · 纵向缩略隐藏文字，方案保留标签设置";
        if (groups.Length > visible) explanation += $" · {groups.Length - visible} 个按钮超出可用区域";
        double trayUsed = 4; int shownTray = 0;
        foreach (var id in options.EffectiveTrayItems) {
            double length = !vertical && id == "clock" ? 56 : 22;
            if (trayUsed + length > trayWidth - 2) break;
            shownTray++; trayUsed += length + options.TrayGap;
        }
        if (shownTray < options.EffectiveTrayItems.Count) explanation += $" · {options.EffectiveTrayItems.Count - shownTray} 个托盘项目在缩略图中省略";
        return new(bar, Axis(startAt, startWidth), Axis(appsAt, appLength), Axis(trayAt, trayWidth), buttons, combined, groups.Length - visible, explanation);
    }
}
