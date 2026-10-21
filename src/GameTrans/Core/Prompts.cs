using System.Text;

namespace GameTrans.Core;

/// <summary>提示词模板：画面翻译出中文，F9 输入翻译中→英；两者都注入玩家自定义的黑话对照。</summary>
public static class Prompts
{
    /// <summary>画面翻译（截图 → 中文）。</summary>
    public static string System(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.Prompt.SystemPrompt))
            return s.Prompt.SystemPrompt.Trim();

        var game = GameLabel(s);
        var target = s.Prompt.TargetLanguage;
        var src = IsAuto(s.Prompt.SourceLanguage) ? "自动识别" : s.Prompt.SourceLanguage;

        var sb = new StringBuilder();
        sb.AppendLine($"你是游戏《{game}》的实时聊天/字幕翻译引擎。");
        sb.AppendLine($"源语言：{src}；目标语言：{target}。");
        sb.AppendLine();

        if (s.Prompt.OutputMode == "ocr")
        {
            sb.AppendLine("任务：只识别并转写图片中所有可见文字，不做翻译。");
        }
        else
        {
            sb.AppendLine($"任务：识别图片中所有可见文字，并翻译成{target}。");
        }

        sb.AppendLine();
        sb.AppendLine("硬性规则：");
        sb.AppendLine("1. 只输出结果正文；不要输出解释、说明、标题、Markdown 标记、代码块或多余引号。");
        sb.AppendLine("2. 严格保持原文的分行结构与先后顺序，不要把多行合并成一段，也不要自行加空行。");
        sb.AppendLine("3. 不要输出「原文：」「译文：」「Translation:」这类前缀。");

        if (s.Prompt.OutputMode == "bilingual")
        {
            sb.AppendLine("4. 每一行按「原文 ⇢ 译文」的格式输出，一行一条。");
        }

        if (s.Prompt.OutputMode != "ocr")
        {
            sb.AppendLine($"4. 译文必须是{target}，不要中英混排；玩家 ID、频道标签（如 [Team]/[All]）、数字和技能按键可以保留原样。");
            sb.AppendLine($"5. 人名、装备、技能、地图点位等术语使用《{game}》官方或社区通行译法，不要生造或音译。");
            sb.AppendLine("6. 忽略 UI 图标、按钮边框、血条数字等与对话无关的元素。");
            sb.AppendLine("7. 口语、缩写、脏话按玩家实际语气翻，不要过度书面化；gg/wp/afk 这类通用电竞缩写可保留。");
            sb.AppendLine("8. 若画面中确实没有可读文字，只输出：(无文字)");
            sb.AppendLine("9. 不要编造原文中不存在的内容；识别不确定的词按最接近的写法保留。");
        }
        else
        {
            sb.AppendLine("4. 忽略 UI 图标、按钮边框、血条数字等与文字无关的元素。");
            sb.AppendLine("5. 若画面中确实没有可读文字，只输出：(无文字)");
        }

        AppendExtra(sb, s);
        AppendGlossary(sb, s);
        return sb.ToString().TrimEnd();
    }

    /// <summary>F9 输入翻译（中文 → 英文）。</summary>
    public static string TextSystem(AppSettings s)
    {
        var game = GameLabel(s);
        var from = s.Prompt.InputSourceLanguage;
        var to = s.Prompt.InputTargetLanguage;

        var sb = new StringBuilder();
        sb.AppendLine($"你是游戏《{game}》的聊天翻译助手。");
        sb.AppendLine($"任务：把玩家输入的文字从{from}翻译成{to}，用于粘贴到游戏聊天框。");
        sb.AppendLine();
        sb.AppendLine("硬性规则：");
        sb.AppendLine("1. 只输出译文正文，不要任何解释、标注、引号或 Markdown。");
        sb.AppendLine("2. 保持一行输入对应一行译文；不要合并或拆散句子。");
        sb.AppendLine($"3. 输出必须是{to}，不要混入原文语言；玩家 ID、道具名可保留原样。");
        sb.AppendLine("4. 语气按玩家原意：骂人/玩笑就照原语气，不要书面化或加礼貌用语。");
        sb.AppendLine($"5. 游戏术语、缩写、黑话用《{game}》社区通行说法。");
        sb.AppendLine("6. 原文很短（如 ok、lol）时也只给最简洁的对等说法，不要扩写。");

        AppendExtra(sb, s);
        AppendGlossary(sb, s);
        return sb.ToString().TrimEnd();
    }

    public static string ImageInstruction(AppSettings s) => s.Prompt.OutputMode switch
    {
        "ocr" => "识别这张游戏截图中的文字并按原样输出。",
        "bilingual" => "识别并翻译这张游戏截图中的文字，按「原文 ⇢ 译文」逐行输出。",
        _ => "识别并翻译这张游戏截图中的文字。",
    };

    public static string TextInstruction(AppSettings s) =>
        s.Prompt.OutputMode == "ocr"
            ? "原样输出下面这段文字，不要翻译："
            : $"把下面这段文字翻译成{s.Prompt.InputTargetLanguage}，只输出译文：";

    private static string GameLabel(AppSettings s) =>
        string.IsNullOrWhiteSpace(s.Prompt.GameName) ? "游戏" : s.Prompt.GameName.Trim();

    private static void AppendExtra(StringBuilder sb, AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.Prompt.ExtraInstruction)) return;
        sb.AppendLine();
        sb.AppendLine("额外要求（优先遵守）：");
        sb.AppendLine(s.Prompt.ExtraInstruction.Trim());
    }

    /// <summary>把玩家自定义的黑话对照表拼进提示词；超过 60 条只取前 60 条以控制 token。</summary>
    private static void AppendGlossary(StringBuilder sb, AppSettings s)
    {
        var list = s.Prompt.Glossary?
            .Where(g => !string.IsNullOrWhiteSpace(g.Term))
            .Take(60)
            .ToList();
        if (list == null || list.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("术语 / 黑话对照（玩家自定义，必须优先按此含义理解与翻译）：");
        foreach (var g in list)
        {
            var meaning = string.IsNullOrWhiteSpace(g.Meaning) ? "（按上下文判断）" : g.Meaning.Trim();
            sb.AppendLine($"- {g.Term.Trim()} = {meaning}");
        }

        sb.AppendLine("遇到上面的词时，按给定含义翻译，不要按字面或普通词典义；表中没有的词按上下文判断。");
    }

    private static bool IsAuto(string? v) =>
        string.IsNullOrWhiteSpace(v) || v.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("自动", StringComparison.Ordinal) || v.Equals("自动识别", StringComparison.Ordinal);
}
