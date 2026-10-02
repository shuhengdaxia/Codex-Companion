using System;
using System.Collections.Generic;
using System.Linq;

public sealed class Choice
{
    public string Id { get; set; }
    public string Label { get; set; }
    public string Accent { get; set; }
    public string Background { get; set; }
    public string Foreground { get; set; }
    public string Mode { get; set; }
    public string CodeTheme { get; set; }
    public Choice(string id, string label, string accent, string background, string foreground, string mode, string codeTheme)
    { Id = id; Label = label; Accent = accent; Background = background; Foreground = foreground; Mode = mode; CodeTheme = codeTheme; }
    public override string ToString() { return Label; }
}

public static class ThemeCatalog
{
    public static readonly Choice[] Themes = {
        new Choice("keep", "保持当前主题", "#0285FF", "#FFFFFF", "#0D0D0D", null, null),
        new Choice("system", "跟随系统", "#0285FF", "#FFFFFF", "#0D0D0D", "system", null),
        new Choice("codex-light", "Codex · 经典浅色", "#0285FF", "#FFFFFF", "#0D0D0D", "light", "codex"),
        new Choice("codex-dark", "Codex · 经典深色", "#339CFF", "#181818", "#FFFFFF", "dark", "codex"),
        new Choice("catppuccin", "Catppuccin · 柔和紫", "#8839EF", "#EFF1F5", "#4C4F69", "light", "catppuccin"),
        new Choice("dracula", "Dracula · 霓虹紫", "#FF79C6", "#282A36", "#F8F8F2", "dark", "dracula"),
        new Choice("nord", "Nord · 北欧蓝", "#88C0D0", "#2E3440", "#ECEFF4", "dark", "nord"),
        new Choice("forest", "Forest · 森林绿", "#5B8A57", "#F4F6EE", "#34413A", "light", "everforest")
    };
    public static void Validate(UserSettings settings)
    {
        if (!Themes.Any(t => t.Id == settings.Theme)) throw new ArgumentException("请选择列表中的主题。");
        if (settings.Skin != "native" && settings.Skin != "qq") throw new ArgumentException("请选择原生或 QQ 皮肤。");
    }
    public static void AddEdits(string id, List<object> edits)
    {
        var theme = Themes.Single(t => t.Id == id);
        if (theme.Mode == null) return;
        edits.Add(CodexConfig.Edit("desktop.appearanceTheme", theme.Mode));
        if (theme.CodeTheme == null) return;
        string prefix = "desktop.appearance" + (theme.Mode == "light" ? "Light" : "Dark");
        edits.Add(CodexConfig.Edit(prefix + "CodeThemeId", theme.CodeTheme));
        edits.Add(CodexConfig.Edit(prefix + "ChromeTheme", new Dictionary<string, object> {
            { "accent", theme.Accent }, { "accentSource", "custom" }, { "ink", theme.Foreground }, { "surface", theme.Background },
            { "contrast", theme.Mode == "light" ? 45 : 60 }, { "opaqueWindows", true },
            { "fonts", new Dictionary<string, object> { { "code", "Consolas" }, { "ui", "Microsoft YaHei UI" } } },
            { "semanticColors", new Dictionary<string, object> { { "diffAdded", "#22A06B" }, { "diffRemoved", "#E5484D" }, { "skill", "#8B5CF6" } } }
        }));
    }
}
