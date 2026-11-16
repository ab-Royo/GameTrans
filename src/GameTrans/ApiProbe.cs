using System.Text;
using GameTrans.Core;

namespace GameTrans;

public static class ApiProbe
{
    /// <summary>命令行自检：用给定 Key 调一次文本探针，把结果写日志并返回退出码。</summary>
    public static int Run(string[] args)
    {
        var key = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "";
        var settings = SettingsStore.Load();
        settings.Normalize();
        Loc.SetLanguage(settings.Ui.Language);

        var client = new DeepSeekClient();
        var result = client.TestAsync(settings, key, CancellationToken.None).GetAwaiter().GetResult();

        var line = result.Ok
            ? Loc.F("API 探针成功 {0}ms model={1} 返回={2}", "API probe OK {0}ms model={1} reply={2}",
                result.ElapsedMs, settings.Api.Model, result.Text)
            : Loc.F("API 探针失败 {0}ms status={1} 错误={2}", "API probe failed {0}ms status={1} error={2}",
                result.ElapsedMs, result.StatusCode, result.Error);
        Log.Info(line);
        Console.WriteLine(line);
        return result.Ok ? 0 : 1;
    }

    /// <summary>命令行：设置并加密保存 API Key。</summary>
    public static int SetKey(string[] args)
    {
        var key = args.Length > 1 && !args[1].StartsWith("--") ? args[1].Trim() : "";
        if (key.Length == 0)
        {
            Console.WriteLine(Loc.T("用法: GameTrans.exe --set-key <API_KEY>", "Usage: GameTrans.exe --set-key <API_KEY>"));
            return 2;
        }

        var settings = SettingsStore.Load();
        Loc.SetLanguage(settings.Ui.Language);
        SettingsStore.SaveApiKey(settings, key);
        SettingsStore.Save(settings);

        Log.Info($"API Key 已加密保存（长度 {key.Length}）");
        Console.WriteLine(Loc.T("API Key 已加密保存到 ", "API key encrypted and saved to ") + SettingsStore.SettingsPath);
        return string.IsNullOrEmpty(settings.Api.ApiKeyProtected) ? 1 : 0;
    }

    /// <summary>命令行：用真实图片跑一次视觉翻译，验证端到端。</summary>
    public static int RunVision(string[] args)
    {
        var key = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "";
        var png = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : "";
        if (!File.Exists(png))
        {
            Console.WriteLine(Loc.T("找不到图片: ", "Image not found: ") + png);
            return 2;
        }

        var settings = SettingsStore.Load();
        settings.Normalize();
        Loc.SetLanguage(settings.Ui.Language);

        var bytes = File.ReadAllBytes(png);
        var client = new DeepSeekClient();
        var r = client.TranslateImageAsync(bytes, settings, key, CancellationToken.None)
            .GetAwaiter().GetResult();

        var head = r.Ok
            ? Loc.F("视觉翻译成功 {0}ms model={1} 图片={2}KB", "Vision translation OK {0}ms model={1} image={2}KB",
                r.ElapsedMs, settings.Api.Model, bytes.Length / 1024)
            : Loc.F("视觉翻译失败 {0}ms status={1} 错误={2}", "Vision translation failed {0}ms status={1} error={2}",
                r.ElapsedMs, r.StatusCode, r.Error);

        Log.Info(head + (r.Ok ? "\n输出: " + r.Text : ""));
        Console.WriteLine(head);
        if (r.Ok)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine(Loc.T("---------- 译文 ----------", "---------- Translation ----------"));
            Console.WriteLine(r.Text);
        }

        return r.Ok ? 0 : 1;
    }

    /// <summary>命令行：把一段文字按 F9 的方向翻译（默认 中文 → 英文）。</summary>
    public static int RunText(string[] args)
    {
        var key = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "";
        var text = string.Join(" ", args.Skip(2).Where(a => !a.StartsWith("--")));
        if (string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine(Loc.T("用法: GameTrans.exe --api-text <KEY> <要翻译的文字>", "Usage: GameTrans.exe --api-text <KEY> <text>"));
            return 2;
        }

        var settings = SettingsStore.Load();
        settings.Normalize();
        Loc.SetLanguage(settings.Ui.Language);

        var client = new DeepSeekClient();
        var r = client.TranslateTextAsync(text, settings, key, CancellationToken.None)
            .GetAwaiter().GetResult();

        var head = r.Ok
            ? Loc.F("文本翻译成功 {0}ms {1} -> {2}", "Text translation OK {0}ms {1} -> {2}",
                r.ElapsedMs, Languages.Display(settings.Prompt.InputSourceLanguage),
                Languages.Display(settings.Prompt.InputTargetLanguage))
            : Loc.F("文本翻译失败 {0}ms status={1} 错误={2}", "Text translation failed {0}ms status={1} error={2}",
                r.ElapsedMs, r.StatusCode, r.Error);

        Log.Info(head + (r.Ok ? "\n原文: " + text + "\n译文: " + r.Text : ""));
        Console.WriteLine(head);
        if (r.Ok)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine(Loc.T("原文: ", "Source: ") + text);
            Console.WriteLine(Loc.T("译文: ", "Translation: ") + r.Text);
        }

        return r.Ok ? 0 : 1;
    }
}