using System.IO;
using System.Net.Sockets;
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

        bool headless = args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase));

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
                log.AppendLine("       " + (ex.StackTrace ?? "").Replace(Environment.NewLine, Environment.NewLine + "       "));
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
            if (s.Ui.Language != Loc.Chinese) throw new Exception("默认界面语言应为中文");
            if (s.Api.Site != ApiSites.China) throw new Exception("中文界面默认目标 API 应为中国站");
            if (s.Prompt.TargetLanguage != Languages.Chinese) throw new Exception("默认目标语言应为中文");
            if (s.Prompt.SourceLanguage != Languages.Auto) throw new Exception("默认源语言应为 auto");
            if (s.Prompt.InputTargetLanguage != Languages.English) throw new Exception("F9 默认目标语言应为英文");
            if (s.Behavior.SimulateInput) throw new Exception("模拟输入默认应关闭");
            if (!s.Behavior.CopyInputResult) throw new Exception("F9 默认应自动复制到剪贴板");

            var json = JsonSerializer.Serialize(s, AppSettings.JsonOpts);
            var back = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOpts)
                       ?? throw new Exception("反序列化失败");
            if (Math.Abs(back.Overlay.BgOpacity - s.Overlay.BgOpacity) > 1e-9) throw new Exception("往返不一致");
            if (back.Ui.Language != s.Ui.Language || back.Api.Site != s.Api.Site) throw new Exception("语言/站点未往返");
            if (back.Behavior.SimulateInput) throw new Exception("模拟输入往返异常");

            s.Behavior.SimulateInput = true;
            var simJson = JsonSerializer.Serialize(s, AppSettings.JsonOpts);
            var simBack = JsonSerializer.Deserialize<AppSettings>(simJson, AppSettings.JsonOpts)
                          ?? throw new Exception("模拟输入反序列化失败");
            if (!simBack.Behavior.SimulateInput) throw new Exception("模拟输入未往返");
            File.WriteAllText(Path.Combine(outDir, "settings.sample.json"), json);
        });

        Step("模拟输入文本规整", () =>
        {
            if (InputSimulator.Sanitize(null) != "") throw new Exception("null 规整异常");
            if (InputSimulator.Sanitize("  ") != "") throw new Exception("空白规整异常");
            if (InputSimulator.Sanitize(" hello\r\nworld ") != "hello world") throw new Exception("换行未转空格");
            if (InputSimulator.Sanitize("a\tb") != "a b") throw new Exception("制表符未转空格");
            if (InputSimulator.Sanitize("a\u0007b") != "ab") throw new Exception("控制字符未丢弃");
            if (InputSimulator.Sanitize("收到，前往 B 点支援") != "收到，前往 B 点支援") throw new Exception("中文被改动");
            if (Win32.SendUnicodeText("")) throw new Exception("空文本不应发送按键");
            if (Win32.SendUnicodeText("\r\n")) throw new Exception("仅换行不应发送按键");
            int expectedInputSize = IntPtr.Size == 8 ? 40 : 28;
            if (System.Runtime.InteropServices.Marshal.SizeOf<Win32.INPUT>() != expectedInputSize)
                throw new Exception($"INPUT 结构体大小异常: {System.Runtime.InteropServices.Marshal.SizeOf<Win32.INPUT>()} != {expectedInputSize}");
        });

        Step("界面语言与本地化", () =>
        {
            try
            {
                Loc.SetLanguage(Loc.Chinese);
                if (Loc.IsEnglish || Loc.T("保存", "Save") != "保存") throw new Exception("中文文案异常");
                if (Languages.Display(Languages.Chinese) != "中文") throw new Exception("中文语言名异常");
                if (Languages.Display(Languages.Japanese) != "日文") throw new Exception("日文语言名异常");

                Loc.SetLanguage(Loc.English);
                if (!Loc.IsEnglish || Loc.T("保存", "Save") != "Save") throw new Exception("英文文案异常");
                if (Languages.Display(Languages.Chinese) != "Chinese") throw new Exception("英文语言名异常");
                if (Languages.Display(Languages.Korean) != "Korean") throw new Exception("韩文语言名异常");
                if (Languages.Display(Languages.Auto) != "Auto") throw new Exception("自动语言名异常");
                if (!Loc.NativeName(Loc.English).StartsWith("English", StringComparison.Ordinal)) throw new Exception("语言切换项异常");

                var en = new AppSettings();
                en.Ui.Language = Loc.English;
                en.Normalize();
                if (en.Ui.Language != Loc.English) throw new Exception("英文界面语言未生效");
                if (en.Api.Site != ApiSites.International) throw new Exception("英文界面默认目标 API 应为国际站");

                File.WriteAllText(Path.Combine(outDir, "i18n.sample.txt"),
                    "zh: " + Loc.T("保存", "Save") + "\n" +
                    "en: Save\n" +
                    "language zh -> " + Loc.NativeName(Loc.Chinese) + ", en -> " + Loc.NativeName(Loc.English));
            }
            finally
            {
                Loc.SetLanguage(Loc.Chinese);
            }
        });

        Step("语言代码归一化", () =>
        {
            var s = new AppSettings();
            s.Prompt.SourceLanguage = "自动识别";
            s.Prompt.TargetLanguage = "中文（简体）";
            s.Prompt.InputSourceLanguage = "中文";
            s.Prompt.InputTargetLanguage = "English";
            s.Normalize();
            if (s.Prompt.SourceLanguage != "auto") throw new Exception("旧源语言未归一化: " + s.Prompt.SourceLanguage);
            if (s.Prompt.TargetLanguage != "zh") throw new Exception("旧目标语言未归一化: " + s.Prompt.TargetLanguage);
            if (s.Prompt.InputSourceLanguage != "zh") throw new Exception("旧输入源语言未归一化");
            if (s.Prompt.InputTargetLanguage != "en") throw new Exception("旧输入目标语言未归一化");

            var t = new AppSettings();
            t.Prompt.TargetLanguage = "auto";
            t.Prompt.SourceLanguage = "日语";
            t.Prompt.InputTargetLanguage = "韩语";
            t.Normalize();
            if (t.Prompt.TargetLanguage != "zh") throw new Exception("目标语言不允许 auto");
            if (t.Prompt.SourceLanguage != "ja") throw new Exception("日语未归一化");
            if (t.Prompt.InputTargetLanguage != "ko") throw new Exception("韩语未归一化");

            foreach (var code in Languages.TargetCodes)
            {
                if (!new[] { "zh", "en", "ko", "ja" }.Contains(code)) throw new Exception("目标语言集合异常: " + code);
            }

            if (Languages.TargetCodes.Length != 4) throw new Exception("目标语言应为 4 种");
            if (Languages.SourceCodes.Length != 5) throw new Exception("源语言应为 auto + 4 种");
        });

        Step("网络错误分类与诊断建议", () =>
        {
            var (host, port) = NetworkDiagnostics.Endpoint("https://api.deepseek.com");
            if (host != "api.deepseek.com" || port != 443)
                throw new Exception($"Base URL 解析异常: {host}:{port}");
            var (fallbackHost, fallbackPort) = NetworkDiagnostics.Endpoint("not a url");
            if (fallbackHost != "api.deepseek.com" || fallbackPort != 443)
                throw new Exception("非法 Base URL 未回落到官方端点");

            // 加速器 / 代理改写 DNS 时，.NET 抛出的正是这种「不知道这样的主机」
            var dns = new HttpRequestException("不知道这样的主机。 (api.deepseek.com:443)",
                new SocketException((int)SocketError.HostNotFound));
            if (!NetworkDiagnostics.IsNameResolutionFailure(dns))
                throw new Exception("DNS 失败未被识别");
            var dnsText = NetworkDiagnostics.ExplainRequestFailure(dns, "https://api.deepseek.com");
            if (!dnsText.Contains("DNS", StringComparison.Ordinal) || !dnsText.Contains("api.deepseek.com", StringComparison.Ordinal))
                throw new Exception("DNS 失败提示缺少主机信息: " + dnsText);
            if (!dnsText.Contains("加速器", StringComparison.Ordinal))
                throw new Exception("DNS 失败提示缺少排查建议: " + dnsText);

            var refused = new HttpRequestException("connection refused",
                new SocketException((int)SocketError.ConnectionRefused));
            if (NetworkDiagnostics.IsNameResolutionFailure(refused))
                throw new Exception("连接被拒绝被误判为 DNS 失败");
            var refusedText = NetworkDiagnostics.ExplainRequestFailure(refused, "https://api.deepseek.com");
            if (!refusedText.Contains("拒绝", StringComparison.Ordinal))
                throw new Exception("连接被拒绝提示异常: " + refusedText);

            try
            {
                Loc.SetLanguage(Loc.English);
                var en = NetworkDiagnostics.ExplainRequestFailure(dns, "https://api.deepseek.com");
                if (!en.Contains("DNS", StringComparison.Ordinal) || !en.Contains("accelerator", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("英文网络提示异常: " + en);
            }
            finally
            {
                Loc.SetLanguage(Loc.Chinese);
            }

            var probe = new NetworkProbe(NetworkStatus.DnsFailed, "summary", "advice", "api.deepseek.com", 443, 10);
            if (probe.Ok || probe.FullText != "summary\nadvice") throw new Exception("NetworkProbe 展示文本异常");
            File.WriteAllText(Path.Combine(outDir, "network.sample.txt"), dnsText + "\n\n" + refusedText);
        });

        Step("目标 API 预设与链接", () =>
        {
            var cn = ApiSites.DefaultFor(Loc.Chinese);
            var intl = ApiSites.DefaultFor(Loc.English);
            if (cn.Code != ApiSites.China) throw new Exception("中文默认站点异常");
            if (intl.Code != ApiSites.International) throw new Exception("英文默认站点异常");
            if (cn.BaseUrl != "https://api.deepseek.com") throw new Exception("中国站 Base URL 异常");
            if (intl.BaseUrl != "https://api.deepseek.com") throw new Exception("国际站 Base URL 异常");
            if (!cn.PlatformUrl.StartsWith("https://platform.deepseek.com", StringComparison.Ordinal))
                throw new Exception("开放平台链接异常");
            if (!Uri.TryCreate(cn.DocsUrl, UriKind.Absolute, out _) ||
                !Uri.TryCreate(intl.DocsUrl, UriKind.Absolute, out _))
                throw new Exception("文档链接异常");

            var s = new AppSettings { Api = { Site = "intl" } };
            s.Normalize();
            if (ApiSites.Resolve(s.Api.Site).Code != ApiSites.International) throw new Exception("站点配置未保留");

            var unknown = new AppSettings { Api = { Site = "zzz" } };
            unknown.Normalize();
            if (unknown.Api.Site != ApiSites.China) throw new Exception("未知站点未回退");

            File.WriteAllText(Path.Combine(outDir, "api-sites.sample.txt"),
                $"cn: {cn.BaseUrl} | {cn.PlatformUrl} | {cn.DocsUrl}\n" +
                $"intl: {intl.BaseUrl} | {intl.PlatformUrl} | {intl.DocsUrl}\n");
        });

        Step("设置页就地切换语言", () =>
        {
            try
            {
                var s = new AppSettings();
                s.Normalize();
                var w = new SettingsWindow(s, _ => { });
                w.SelectLanguageForTest(Loc.English);
                if (!Loc.IsEnglish) throw new Exception("设置页未切到英文");
                if (w.Title != "GameTrans Settings") throw new Exception("设置页标题未本地化: " + w.Title);
                w.SelectLanguageForTest(Loc.Chinese);
                if (Loc.IsEnglish) throw new Exception("设置页未切回中文");
                if (w.Title != "GameTrans 设置") throw new Exception("设置页标题未还原: " + w.Title);
                w.Close();
            }
            finally
            {
                Loc.SetLanguage(Loc.Chinese);
            }
        });

        var vs = ScreenCapture.VirtualScreen();
        var region = new RegionPx(vs.Left + Math.Max(0, vs.Width / 2 - 300),
            vs.Top + Math.Max(0, vs.Height / 2 - 130), 0, 0);
        region = new RegionPx(region.Left, region.Top, region.Left + 600, region.Top + 260);

        BitmapSource? shot = null;
        if (headless)
        {
            log.AppendLine("[SKIP] 截屏（headless 模式，无桌面会话）");
        }
        else
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

        if (headless)
        {
            log.AppendLine("[SKIP] 叠加层离屏渲染（headless 模式）");
        }
        else
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

            var ja = new AppSettings();
            ja.Prompt.TargetLanguage = "ja";
            ja.Prompt.InputTargetLanguage = "ko";
            ja.Normalize();
            if (!Prompts.System(ja).Contains("日语")) throw new Exception("日文目标语言未写入提示词");
            if (!Prompts.TextSystem(ja).Contains("韩语")) throw new Exception("韩文目标语言未写入提示词");
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

        Step("鼠标键热键名称与配置", () =>
        {
            if (KeyName.Format(0, Win32.VK_MBUTTON) != "鼠标中键") throw new Exception("中键名称异常");
            if (KeyName.Format(Win32.MOD_CONTROL, Win32.VK_XBUTTON1) != "Ctrl+鼠标侧键1") throw new Exception("侧键1 名称异常");
            if (KeyName.Format(Win32.MOD_SHIFT, Win32.VK_XBUTTON2) != "Shift+鼠标侧键2") throw new Exception("侧键2 名称异常");
            if (!Win32.IsMouseVk(Win32.VK_MBUTTON) || !Win32.IsMouseVk(Win32.VK_XBUTTON1)
                || !Win32.IsMouseVk(Win32.VK_XBUTTON2) || Win32.IsMouseVk(0x77))
                throw new Exception("鼠标键判定异常");

            var s = new AppSettings();
            s.Hotkeys.TranslateVk = Win32.VK_XBUTTON2;
            s.Hotkeys.TranslateMods = 0;
            s.Hotkeys.InputVk = Win32.VK_MBUTTON;
            s.Hotkeys.InputMods = Win32.MOD_SHIFT;
            s.Normalize();
            if (s.Hotkeys.TranslateText != "鼠标侧键2") throw new Exception("翻译热键往返异常: " + s.Hotkeys.TranslateText);
            if (s.Hotkeys.InputText != "Shift+鼠标中键") throw new Exception("输入热键往返异常: " + s.Hotkeys.InputText);
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
