using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTrans.Core;

public sealed class AppSettings
{
    public ApiOptions Api { get; set; } = new();
    public PromptOptions Prompt { get; set; } = new();
    public OverlayOptions Overlay { get; set; } = new();
    public HotkeyOptions Hotkeys { get; set; } = new();
    public RegionOptions Region { get; set; } = new();
    public BehaviorOptions Behavior { get; set; } = new();

    public static JsonSerializerOptions JsonOpts { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>夹紧取值范围，避免手改配置文件后崩溃。</summary>
    public void Normalize()
    {
        Api.BaseUrl = string.IsNullOrWhiteSpace(Api.BaseUrl) ? "https://api.deepseek.com" : Api.BaseUrl.Trim();
        Api.Model = string.IsNullOrWhiteSpace(Api.Model) ? "deepseek-flash" : Api.Model.Trim();
        Api.MaxTokens = Math.Clamp(Api.MaxTokens, 64, 8192);
        Api.Temperature = Math.Clamp(Api.Temperature, 0.0, 2.0);
        Api.TimeoutSeconds = Math.Clamp(Api.TimeoutSeconds, 5, 300);
        if (Api.Detail is not ("high" or "low" or "original" or "auto")) Api.Detail = "high";

        if (string.IsNullOrWhiteSpace(Prompt.GameName)) Prompt.GameName = "战狗";
        if (string.IsNullOrWhiteSpace(Prompt.TargetLanguage)) Prompt.TargetLanguage = "中文（简体）";
        if (string.IsNullOrWhiteSpace(Prompt.InputSourceLanguage)) Prompt.InputSourceLanguage = "中文（简体）";
        if (string.IsNullOrWhiteSpace(Prompt.InputTargetLanguage)) Prompt.InputTargetLanguage = "English";
        Prompt.Glossary ??= new List<GlossaryEntry>();
        Prompt.Glossary = Prompt.Glossary
            .Where(g => g != null && !string.IsNullOrWhiteSpace(g.Term))
            .Take(200)
            .ToList();
        foreach (var g in Prompt.Glossary)
        {
            g.Term = g.Term.Trim();
            g.Meaning = (g.Meaning ?? "").Trim();
        }
        if (string.IsNullOrWhiteSpace(Prompt.SourceLanguage)) Prompt.SourceLanguage = "auto";
        if (Prompt.OutputMode is not ("translated" or "bilingual" or "ocr")) Prompt.OutputMode = "translated";

        Overlay.BgColor = ColorUtil.NormalizeHex(Overlay.BgColor, "#0B0B0F");
        Overlay.TextColor = ColorUtil.NormalizeHex(Overlay.TextColor, "#FFFFFF");
        Overlay.BorderColor = ColorUtil.NormalizeHex(Overlay.BorderColor, "#7C6BFF");
        Overlay.BgOpacity = Math.Clamp(Overlay.BgOpacity, 0.0, 1.0);
        Overlay.FontSize = Math.Clamp(Overlay.FontSize, 8.0, 96.0);
        Overlay.Padding = Math.Clamp(Overlay.Padding, 0.0, 64.0);
        Overlay.CornerRadius = Math.Clamp(Overlay.CornerRadius, 0.0, 48.0);
        Overlay.BorderThickness = Math.Clamp(Overlay.BorderThickness, 0.0, 8.0);
        if (string.IsNullOrWhiteSpace(Overlay.FontFamily)) Overlay.FontFamily = "Microsoft YaHei UI";
        if (Overlay.TextAlign is not ("left" or "center" or "right")) Overlay.TextAlign = "left";
        if (Overlay.OverflowMode is not ("expand" or "shrink")) Overlay.OverflowMode = "expand";
        Overlay.MinFontSize = Math.Clamp(Overlay.MinFontSize, 6.0, 48.0);
        Behavior.InputOpacity = Math.Clamp(Behavior.InputOpacity, 0.3, 1.0);

        if (Hotkeys.TranslateVk == 0) Hotkeys.TranslateVk = 0x77; // F8
        if (Hotkeys.InputVk == 0) Hotkeys.InputVk = 0x78;         // F9

        if (Region.HasRegion) Region.Normalize();
    }
}

public sealed class ApiOptions
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-flash";

    /// <summary>DPAPI 加密后的 Base64；明文永不落盘。</summary>
    public string ApiKeyProtected { get; set; } = "";

    /// <summary>图片细节：high/original 保原图，low 缩到 512×512。</summary>
    public string Detail { get; set; } = "high";

    public int MaxTokens { get; set; } = 2048;
    public double Temperature { get; set; } = 0.2;
    public int TimeoutSeconds { get; set; } = 90;
}

public sealed class PromptOptions
{
    /// <summary>目标游戏，会写进提示词，用于术语与语气。</summary>
    public string GameName { get; set; } = "战狗";

    /// <summary>auto 或具体语言名。</summary>
    public string SourceLanguage { get; set; } = "auto";

    /// <summary>画面翻译的目标语言（默认中文）。</summary>
    public string TargetLanguage { get; set; } = "中文（简体）";

    /// <summary>F9 输入翻译的源语言（默认中文）。</summary>
    public string InputSourceLanguage { get; set; } = "中文（简体）";

    /// <summary>F9 输入翻译的目标语言（默认英文）。</summary>
    public string InputTargetLanguage { get; set; } = "English";

    /// <summary>黑话/术语对照表，注入提示词作为翻译依据。</summary>
    public List<GlossaryEntry> Glossary { get; set; } = new();

    /// <summary>translated=仅译文；bilingual=原文+译文；ocr=仅识别。</summary>
    public string OutputMode { get; set; } = "translated";

    /// <summary>附加指令，例如术语表、语气要求。</summary>
    public string ExtraInstruction { get; set; } = "";

    /// <summary>自定义 system 提示词；为空时使用内置模板。</summary>
    public string SystemPrompt { get; set; } = "";
}

public sealed class OverlayOptions
{
    public string BgColor { get; set; } = "#0B0B0F";
    public double BgOpacity { get; set; } = 0.62;
    public string TextColor { get; set; } = "#FFFFFF";
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 20;
    public double Padding { get; set; } = 10;
    public double CornerRadius { get; set; } = 8;
    public double BorderThickness { get; set; } = 1;
    public string BorderColor { get; set; } = "#7C6BFF";
    public string TextAlign { get; set; } = "left";

    /// <summary>右下角显示耗时/模型等状态。</summary>
    public bool ShowStatus { get; set; } = true;

    /// <summary>文字投影，亮色背景下更易读。</summary>
    public bool TextShadow { get; set; } = true;

    /// <summary>字号随选区高度自动放大（小选区不会过小，大选区不会过空）。</summary>
    public bool AutoScaleFont { get; set; } = true;

    /// <summary>译文超出选区时：expand=向下扩展叠加层（推荐，字不变小）；shrink=缩字塞进选区。</summary>
    public string OverflowMode { get; set; } = "expand";

    /// <summary>缩放模式下的最小字号，低于它宁可裁剪也不继续缩。</summary>
    public double MinFontSize { get; set; } = 13;

    /// <summary>垂直对齐：top/center/bottom。</summary>
    public string VerticalAlign { get; set; } = "center";

    /// <summary>文字整体缩放上限（防止超小区域字号爆炸）。</summary>
    public bool ClickThrough { get; set; } = true;
}

public sealed class HotkeyOptions
{
    public uint TranslateVk { get; set; } = 0x77; // F8
    public uint TranslateMods { get; set; }
    public uint InputVk { get; set; } = 0x78;     // F9
    public uint InputMods { get; set; }

    [JsonIgnore]
    public string TranslateText => KeyName.Format(TranslateMods, TranslateVk);

    [JsonIgnore]
    public string InputText => KeyName.Format(InputMods, InputVk);
}

public sealed class RegionOptions
{
    public bool HasRegion { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }

    [JsonIgnore] public int Width => Math.Max(0, Right - Left);
    [JsonIgnore] public int Height => Math.Max(0, Bottom - Top);
    [JsonIgnore] public bool IsUsable => HasRegion && Width >= 8 && Height >= 8;

    public void Normalize()
    {
        if (Left > Right) (Left, Right) = (Right, Left);
        if (Top > Bottom) (Top, Bottom) = (Bottom, Top);
        if (Width < 8 || Height < 8) HasRegion = false;
    }
}

public sealed class BehaviorOptions
{
    /// <summary>启动时最小化到托盘（不显示启动页）。</summary>
    public bool StartMinimized { get; set; }

    /// <summary>启动时显示启动页。</summary>
    public bool ShowStartupWindow { get; set; } = true;

    /// <summary>F9 输入框的不透明度。</summary>
    public double InputOpacity { get; set; } = 0.88;

    /// <summary>翻译成功后在叠加层提示耗时。</summary>
    public bool ShowLatency { get; set; } = true;

    /// <summary>F9 结果自动写入剪贴板。</summary>
    public bool CopyInputResult { get; set; } = true;
}

public sealed class GlossaryEntry
{
    /// <summary>原文词/缩写/黑话。</summary>
    public string Term { get; set; } = "";

    /// <summary>它在游戏里的实际含义（中文说明）。</summary>
    public string Meaning { get; set; } = "";
}

public static class KeyName
{
    public static string Format(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & Win32.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & Win32.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(VkName(vk));
        return string.Join("+", parts);
    }

    public static string VkName(uint vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x13 => "Pause",
        0x14 => "CapsLock",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x2D => "Ins",
        0x2E => "Del",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => "0x" + vk.ToString("X2"),
    };
}

public static class ColorUtil
{
    public static string NormalizeHex(string? hex, string fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var h = hex.Trim();
        if (!h.StartsWith('#')) h = "#" + h;
        if (h.Length == 4)
        {
            // #abc -> #aabbcc
            h = $"#{h[1]}{h[1]}{h[2]}{h[2]}{h[3]}{h[3]}";
        }
        if (h.Length != 7) return fallback;
        for (int i = 1; i < 7; i++)
        {
            if (!Uri.IsHexDigit(h[i])) return fallback;
        }
        return h.ToUpperInvariant();
    }
}
