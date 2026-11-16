namespace GameTrans.Core;

/// <summary>
/// 界面语言（简体中文 / English）。所有用户可见文案统一通过 <see cref="T"/> 二选一，
/// 语言切换后新建的窗口立即生效，已打开的设置页会就地重建。
/// </summary>
public static class Loc
{
    public const string Chinese = "zh";
    public const string English = "en";

    private static string _language = Chinese;

    /// <summary>当前界面语言代码：<c>zh</c> / <c>en</c>。</summary>
    public static string Language => _language;

    public static bool IsEnglish => _language == English;

    public static event Action? Changed;

    public static string NormalizeLanguage(string? value) =>
        string.Equals((value ?? "").Trim(), English, StringComparison.OrdinalIgnoreCase)
            ? English
            : Chinese;

    public static void SetLanguage(string? language)
    {
        var next = NormalizeLanguage(language);
        if (next == _language) return;
        _language = next;
        Changed?.Invoke();
    }

    /// <summary>按当前界面语言二选一。</summary>
    public static string T(string zh, string en) => IsEnglish ? en : zh;

    /// <summary>按当前界面语言选择格式串并格式化。</summary>
    public static string F(string zhFormat, string enFormat, params object?[] args) =>
        string.Format(IsEnglish ? enFormat : zhFormat, args);

    /// <summary>语言切换器里显示的母语名称（不随界面语言变化，方便用户找到自己的语言）。</summary>
    public static string NativeName(string? language) =>
        NormalizeLanguage(language) == English ? "English" : "简体中文";
}

/// <summary>翻译语言：源语言允许 auto，目标语言限中文 / 英文 / 韩文 / 日文。</summary>
public static class Languages
{
    public const string Auto = "auto";
    public const string Chinese = "zh";
    public const string English = "en";
    public const string Korean = "ko";
    public const string Japanese = "ja";

    /// <summary>源语言下拉顺序（自动 + 四种语言）。</summary>
    public static readonly string[] SourceCodes = { Auto, Chinese, English, Korean, Japanese };

    /// <summary>目标语言下拉顺序（中文 / 英文 / 韩文 / 日文）。</summary>
    public static readonly string[] TargetCodes = { Chinese, English, Korean, Japanese };

    private static readonly KeyValuePair<string, string[]>[] Aliases =
    {
        new(Auto, new[] { "auto", "自动", "自动识别", "auto detect", "automatic", "autodetect" }),
        new(Chinese, new[]
        {
            "zh", "zh-cn", "zh_cn", "中文", "中文（简体）", "中文(简体)", "简体中文", "简体", "chinese",
            "chinese (simplified)", "simplified chinese",
        }),
        new(English, new[] { "en", "en-us", "英文", "英语", "english" }),
        new(Korean, new[] { "ko", "ko-kr", "韩文", "韩语", "korean", "한국어" }),
        new(Japanese, new[] { "ja", "ja-jp", "日文", "日语", "japanese", "日本語" }),
    };

    /// <summary>把配置里的旧值（中文语言名 / 英文语言名）归一化为语言代码；未知值按 auto 处理。</summary>
    public static string Normalize(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return Auto;
        foreach (var pair in Aliases)
        {
            foreach (var alias in pair.Value)
            {
                if (string.Equals(alias, v, StringComparison.OrdinalIgnoreCase)) return pair.Key;
            }
        }

        return Auto;
    }

    /// <summary>源语言只允许 auto + 四种语言。</summary>
    public static string NormalizeSource(string? value) => Normalize(value);

    /// <summary>目标语言不允许 auto；旧配置里的自动值回落到中文。</summary>
    public static string NormalizeTarget(string? value)
    {
        var code = Normalize(value);
        return code == Auto ? Chinese : code;
    }

    /// <summary>界面里显示的语言名（随界面语言变化）。</summary>
    public static string Display(string? code) => Normalize(code) switch
    {
        English => Loc.T("英文", "English"),
        Korean => Loc.T("韩文", "Korean"),
        Japanese => Loc.T("日文", "Japanese"),
        Auto => Loc.T("自动", "Auto"),
        _ => Loc.T("中文", "Chinese"),
    };

    /// <summary>提示词内部使用的语言名（提示词本身为中文，语言名固定用中文表述）。</summary>
    public static string PromptName(string? code) => Normalize(code) switch
    {
        English => "English",
        Korean => "韩语",
        Japanese => "日语",
        Auto => "自动识别",
        _ => "中文（简体）",
    };

    public static string CodeFromDisplay(string? display)
    {
        foreach (var code in SourceCodes)
        {
            if (string.Equals(Display(code), (display ?? "").Trim(), StringComparison.Ordinal)) return code;
        }

        return Auto;
    }
}

/// <summary>
/// 目标 API 预设。DeepSeek 官方对国内与海外账号使用同一个 API 端点
/// （<c>https://api.deepseek.com</c>），两站的区别在开放平台入口、文档语言与账号区域；
/// 选择结果同时决定默认 Base URL 与设置页里的跳转链接。
/// </summary>
public sealed class ApiSiteOption
{
    public required string Code { get; init; }
    public required string NameZh { get; init; }
    public required string NameEn { get; init; }
    public required string BaseUrl { get; init; }
    public required string PlatformUrl { get; init; }
    public required string WebsiteUrl { get; init; }
    public required string DocsUrl { get; init; }

    /// <summary>随界面语言变化的显示名。</summary>
    public string Display => Loc.T(NameZh, NameEn);

    /// <summary>平台链接旁的短标签。</summary>
    public string PlatformHost => new Uri(PlatformUrl).Host;
}

/// <summary>DeepSeek 中国站 / 国际站预设与默认规则。</summary>
public static class ApiSites
{
    public const string China = "cn";
    public const string International = "intl";

    public static readonly ApiSiteOption ChinaSite = new()
    {
        Code = China,
        NameZh = "DeepSeek 中国站",
        NameEn = "DeepSeek China",
        BaseUrl = "https://api.deepseek.com",
        PlatformUrl = "https://platform.deepseek.com/",
        WebsiteUrl = "https://www.deepseek.com/",
        DocsUrl = "https://api-docs.deepseek.com/zh-cn/",
    };

    public static readonly ApiSiteOption InternationalSite = new()
    {
        Code = International,
        NameZh = "DeepSeek 国际站",
        NameEn = "DeepSeek International",
        BaseUrl = "https://api.deepseek.com",
        PlatformUrl = "https://platform.deepseek.com/",
        WebsiteUrl = "https://www.deepseek.com/en/",
        DocsUrl = "https://api-docs.deepseek.com/",
    };

    public static readonly ApiSiteOption[] All = { ChinaSite, InternationalSite };

    /// <summary>界面语言为中文时默认中国站，其它语言默认国际站。</summary>
    public static ApiSiteOption DefaultFor(string? uiLanguage) =>
        Loc.NormalizeLanguage(uiLanguage) == Loc.English ? InternationalSite : ChinaSite;

    public static ApiSiteOption Resolve(string? code) =>
        string.Equals((code ?? "").Trim(), International, StringComparison.OrdinalIgnoreCase)
            ? InternationalSite
            : ChinaSite;

    public static string Normalize(string? code, string? uiLanguage)
    {
        var c = (code ?? "").Trim();
        if (string.Equals(c, China, StringComparison.OrdinalIgnoreCase)) return China;
        if (string.Equals(c, International, StringComparison.OrdinalIgnoreCase)) return International;
        return DefaultFor(uiLanguage).Code;
    }

    public static string Display(string? code) => Resolve(code).Display;

    public static string CodeFromDisplay(string? display)
    {
        foreach (var site in All)
        {
            if (string.Equals(site.Display, (display ?? "").Trim(), StringComparison.Ordinal)) return site.Code;
        }

        return China;
    }
}

/// <summary>应用相关外部链接（关于页 / 文档 / 社区）。</summary>
public static class AppLinks
{
    public const string Repository = "https://github.com/ab-Royo/GameTrans";
    public const string Releases = Repository + "/releases";
    public const string Issues = Repository + "/issues";

    /// <summary>QQ 群号（QQ 没有稳定的网页群入口，界面上点击即复制群号）。</summary>
    public const string QQGroup = "419733401";

    /// <summary>作者 Bilibili 个人空间。</summary>
    public const string Bilibili = "https://m.bilibili.com/space/51952428";

    /// <summary>把 QQ 群号写入剪贴板；返回是否成功。</summary>
    public static bool CopyQQGroup() => ClipboardService.TrySetText(QQGroup);
}
