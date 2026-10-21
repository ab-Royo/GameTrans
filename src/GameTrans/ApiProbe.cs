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

        var client = new DeepSeekClient();
        var result = client.TestAsync(settings, key, CancellationToken.None).GetAwaiter().GetResult();

        var line = result.Ok
            ? $"API 探针成功 {result.ElapsedMs}ms model={settings.Api.Model} 返回={result.Text}"
            : $"API 探针失败 {result.ElapsedMs}ms status={result.StatusCode} 错误={result.Error}";
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
            Console.WriteLine("用法: GameTrans.exe --set-key <API_KEY>");
            return 2;
        }

        var settings = SettingsStore.Load();
        SettingsStore.SaveApiKey(settings, key);
        SettingsStore.Save(settings);

        Log.Info($"API Key 已加密保存（长度 {key.Length}）");
        Console.WriteLine("API Key 已加密保存到 " + SettingsStore.SettingsPath);
        return string.IsNullOrEmpty(settings.Api.ApiKeyProtected) ? 1 : 0;
    }

    /// <summary>命令行：用真实图片跑一次视觉翻译，验证端到端。</summary>
    public static int RunVision(string[] args)
    {
        var key = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "";
        var png = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : "";
        if (!File.Exists(png))
        {
            Console.WriteLine("找不到图片: " + png);
            return 2;
        }

        var settings = SettingsStore.Load();
        settings.Normalize();

        var bytes = File.ReadAllBytes(png);
        var client = new DeepSeekClient();
        var r = client.TranslateImageAsync(bytes, settings, key, CancellationToken.None)
            .GetAwaiter().GetResult();

        var head = r.Ok
            ? $"视觉翻译成功 {r.ElapsedMs}ms model={settings.Api.Model} 图片={bytes.Length / 1024}KB"
            : $"视觉翻译失败 {r.ElapsedMs}ms status={r.StatusCode} 错误={r.Error}";

        Log.Info(head + (r.Ok ? "\n输出: " + r.Text : ""));
        Console.WriteLine(head);
        if (r.Ok)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("---------- 译文 ----------");
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
            Console.WriteLine("用法: GameTrans.exe --api-text <KEY> <要翻译的文字>");
            return 2;
        }

        var settings = SettingsStore.Load();
        settings.Normalize();

        var client = new DeepSeekClient();
        var r = client.TranslateTextAsync(text, settings, key, CancellationToken.None)
            .GetAwaiter().GetResult();

        var head = r.Ok
            ? $"文本翻译成功 {r.ElapsedMs}ms {settings.Prompt.InputSourceLanguage} -> {settings.Prompt.InputTargetLanguage}"
            : $"文本翻译失败 {r.ElapsedMs}ms status={r.StatusCode} 错误={r.Error}";

        Log.Info(head + (r.Ok ? "\n原文: " + text + "\n译文: " + r.Text : ""));
        Console.WriteLine(head);
        if (r.Ok)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("原文: " + text);
            Console.WriteLine("译文: " + r.Text);
        }

        return r.Ok ? 0 : 1;
    }
}