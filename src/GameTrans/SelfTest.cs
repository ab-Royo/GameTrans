using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTrans.Core;
using GameTrans.UI;

namespace GameTrans;

/// <summary>无界面自检：验证截图、PNG 编码、叠加层离屏渲染与请求体结构，不弹任何窗口。</summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        var outDir = args.Length > 1 && !args[1].StartsWith('-')
            ? args[1]
            : Path.Combine(Path.GetTempPath(), "GameTrans-selftest");
        Directory.CreateDirectory(outDir);

        var log = new StringBuilder();
        int failures = 0;

        void Step(string name, Action body)
        {
            try
            {
                body();
                log.AppendLine($"[ OK ] {name}");
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Step("配置默认值与夹紧", () =>
        {
            var s = new AppSettings();
            s.Overlay.BgOpacity = 5.0;
            s.Api.MaxTokens = 999999;
            s.Overlay.BgColor = "zzz";
            s.Normalize();
            if (s.Overlay.BgOpacity > 1.0) throw new Exception("BgOpacity 未夹紧");
            if (s.Api.MaxTokens > 8192) throw new Exception("MaxTokens 未夹紧");
            if (!s.Overlay.BgColor.StartsWith('#')) throw new Exception("颜色未归一化: " + s.Overlay.BgColor);

            var json = JsonSerializer.Serialize(s, AppSettings.JsonOpts);
            var back = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOpts)
                       ?? throw new Exception("反序列化失败");
            if (Math.Abs(back.Overlay.BgOpacity - s.Overlay.BgOpacity) > 1e-9) throw new Exception("往返不一致");
            File.WriteAllText(Path.Combine(outDir, "settings.sample.json"), json);
        });

        var vs = ScreenCapture.VirtualScreen();
        var region = new RegionPx(vs.Left + Math.Max(0, vs.Width / 2 - 300),
            vs.Top + Math.Max(0, vs.Height / 2 - 130), 0, 0);
        region = new RegionPx(region.Left, region.Top, region.Left + 600, region.Top + 260);

        BitmapSource? shot = null;
        Step($"截屏 {region.Width}x{region.Height} @ ({region.Left},{region.Top})", () =>
        {
            shot = ScreenCapture.Capture(region);
            if (shot.PixelWidth != region.Width || shot.PixelHeight != region.Height)
                throw new Exception($"尺寸不符: {shot.PixelWidth}x{shot.PixelHeight}");

            var png = ScreenCapture.ToPng(shot);
            File.WriteAllBytes(Path.Combine(outDir, "capture.png"), png);

            // 解码校验
            using var ms = new MemoryStream(png);
            var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = dec.Frames[0];
            if (frame.PixelWidth != region.Width) throw new Exception("PNG 解码尺寸不符");

            // 非全黑校验
            var conv = new FormatConvertedBitmap(shot, PixelFormats.Bgra32, null, 0);
            int stride = conv.PixelWidth * 4;
            var buf = new byte[stride * conv.PixelHeight];
            conv.CopyPixels(buf, stride, 0);
            long sum = 0;
            for (int i = 0; i < buf.Length; i += 4) sum += buf[i] + buf[i + 1] + buf[i + 2];
            double avg = sum / (double)(buf.Length / 4) / 3.0;
            log.AppendLine($"       平均亮度 {avg:0.0} / PNG {png.Length / 1024} KB");
            if (avg < 1.0) throw new Exception("截图为纯黑，可能被 DWM 保护或权限不足");
        });

        Step("叠加层离屏渲染", () =>
        {
            var s = new AppSettings();
            s.Normalize();
            var content = new OverlayContent();
            content.Width = 600;
            content.Height = 260;
            content.ApplyStyle(s.Overlay, 600);
            content.SetText("示例：敌方五人推中，先别开团，等我绕后。\nOur mid lane is pushing, don't engage yet.",
                "128 ms · deepseek-flash");

            content.Measure(new Size(600, 260));
            content.Arrange(new Rect(0, 0, 600, 260));
            content.UpdateLayout();

            var rtb = new RenderTargetBitmap(600, 260, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(content);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            File.WriteAllBytes(Path.Combine(outDir, "overlay-render.png"), ms.ToArray());
        });

        Step("请求体结构（视觉翻译）", () =>
        {
            var s = new AppSettings();
            s.Normalize();

            var body = new Dictionary<string, object?>
            {
                ["model"] = s.Api.Model,
                ["messages"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "system",
                        ["content"] = Prompts.System(s),
                    },
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] = Prompts.ImageInstruction(s),
                            },
                            new Dictionary<string, object?>
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new Dictionary<string, object?>
                                {
                                    ["url"] = "data:image/png;base64,<BASE64>",
                                    ["detail"] = s.Api.Detail,
                                },
                            },
                        },
                    },
                },
                ["max_tokens"] = s.Api.MaxTokens,
                ["temperature"] = s.Api.Temperature,
                ["stream"] = false,
            };

            var json = JsonSerializer.Serialize(body, AppSettings.JsonOpts);
            File.WriteAllText(Path.Combine(outDir, "request.image.sample.json"), json);

            var node = JsonDocument.Parse(json);
            var content = node.RootElement.GetProperty("messages")[1].GetProperty("content");
            if (content.GetArrayLength() != 2) throw new Exception("content block 数不是 2");
            var type0 = content[0].GetProperty("type").GetString();
            var type1 = content[1].GetProperty("type").GetString();
            if (type0 != "text" || type1 != "image_url") throw new Exception($"block 类型异常: {type0}/{type1}");

            var model = node.RootElement.GetProperty("model").GetString();
            if (string.IsNullOrEmpty(model)) throw new Exception("model 为空");
            log.AppendLine($"       model={model} 端点 {BuildEndpoint(s.Api.BaseUrl)}");
        });

        Step("提示词生成（三种输出模式）", () =>
        {
            foreach (var mode in new[] { "translated", "bilingual", "ocr" })
            {
                var s = new AppSettings();
                s.Prompt.OutputMode = mode;
                s.Normalize();
                var sys = Prompts.System(s);
                if (!sys.Contains("硬性规则")) throw new Exception(mode + " 缺少规则段");
                File.WriteAllText(Path.Combine(outDir, $"prompt.{mode}.txt"), sys);
            }

            var textSettings = new AppSettings();
            textSettings.Normalize();
            var textSys = Prompts.TextSystem(textSettings);
            if (!textSys.Contains("只输出译文正文")) throw new Exception("F9 提示词异常");
            File.WriteAllText(Path.Combine(outDir, "prompt.text.txt"), textSys);
        });

        Step("术语表提示词注入", () =>
        {
            var s = new AppSettings();
            s.Prompt.Glossary = new List<GlossaryEntry>
            {
                new() { Term = "烤肠", Meaning = "敌方狙击手" },
                new() { Term = "B点", Meaning = "地图西侧的占领点 B" },
            };
            s.Normalize();

            var img = Prompts.System(s);
            var txt = Prompts.TextSystem(s);
            if (!img.Contains("烤肠 = 敌方狙击手")) throw new Exception("画面翻译提示词缺少术语表");
            if (!txt.Contains("B点 = 地图西侧的占领点 B")) throw new Exception("F9 提示词缺少术语表");

            File.WriteAllText(Path.Combine(outDir, "prompt.glossary.txt"),
                "== 画面翻译 ==\n" + img + "\n\n== F9 输入翻译 ==\n" + txt);
        });

        var report = string.Join(Environment.NewLine,
            $"GameTrans self-test  {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"虚拟屏幕: ({vs.Left},{vs.Top}) {vs.Width}x{vs.Height}",
            $"输出目录: {outDir}",
            "",
            log.ToString(),
            failures == 0 ? "结果: 全部通过" : $"结果: {failures} 项失败");

        File.WriteAllText(Path.Combine(outDir, "report.txt"), report, Encoding.UTF8);
        Console.WriteLine(report);
        return failures == 0 ? 0 : 1;
    }

    private static string BuildEndpoint(string baseUrl)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) b = "https://api.deepseek.com";
        return b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? b
            : b + "/chat/completions";
    }
}
