using System.Threading;
using System.Windows;
using GameTrans.Core;

namespace GameTrans;

public static class Program
{
    private const string MutexName = "Global\\GameTrans.SingleInstance.v1";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.Run(args);

        if (args.Any(a => a.Equals("--demo-overlay", StringComparison.OrdinalIgnoreCase)))
            return DemoOverlay(args);

        if (args.Any(a => a.Equals("--demo-settings", StringComparison.OrdinalIgnoreCase)))
            return DemoSettings(args);

        if (args.Any(a => a.Equals("--api-test", StringComparison.OrdinalIgnoreCase)))
            return ApiProbe.Run(args);

        if (args.Any(a => a.Equals("--set-key", StringComparison.OrdinalIgnoreCase)))
            return ApiProbe.SetKey(args);

        if (args.Any(a => a.Equals("--api-vision", StringComparison.OrdinalIgnoreCase)))
            return ApiProbe.RunVision(args);

        if (args.Any(a => a.Equals("--api-text", StringComparison.OrdinalIgnoreCase)))
            return ApiProbe.RunText(args);

        if (args.Any(a => a.Equals("--demo-startup", StringComparison.OrdinalIgnoreCase)))
            return DemoWindow(args, "startup");

        if (args.Any(a => a.Equals("--demo-glossary", StringComparison.OrdinalIgnoreCase)))
            return DemoWindow(args, "glossary");

        if (args.Any(a => a.Equals("--demo-input", StringComparison.OrdinalIgnoreCase)))
            return DemoInput(args);

        if (args.Any(a => a.Equals("--demo-selector", StringComparison.OrdinalIgnoreCase)))
            return DemoSelector(args);

        if (args.Any(a => a.Equals("--demo-compose", StringComparison.OrdinalIgnoreCase)))
            return DemoCompose(args);

        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已有实例：提示后退出（不弹窗打扰）
            UI.ToastWindow.ShowToast("GameTrans 已在运行，请查看系统托盘");
            Thread.Sleep(1200);
            return 0;
        }

        var settings = SettingsStore.Load();
        Log.Info($"启动 v{Core.AppInfo.Version} · 配置目录 {SettingsStore.DataDir} · 区域 {settings.Region.Width}x{settings.Region.Height}");

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("未处理异常：" + e.Exception);
            UI.ToastWindow.ShowToast("运行异常：" + e.Exception.Message, true);
            e.Handled = true;
        };

        app.Startup += (_, _) =>
        {
            try
            {
                _ = new TrayApp(settings);
            }
            catch (Exception ex)
            {
                Log.Error("启动失败：" + ex);
                MessageBox.Show("GameTrans 启动失败：\n" + ex.Message, "GameTrans");
                app.Shutdown();
            }
        };

        return app.Run();
    }

    /// <summary>调试：显示 F9 输入窗并离屏渲染，检查布局。</summary>
    private static int DemoInput(string[] args)
    {
        var outPath = args.Length > 1 && !args[1].StartsWith("--")
            ? args[1]
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gametrans-input.png");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            var s = Core.SettingsStore.Load();
            var vs = Core.ScreenCapture.VirtualScreen();
            var region = new Core.RegionPx(vs.Left + vs.Width / 2 - 400, vs.Top + vs.Height / 2,
                vs.Left + vs.Width / 2 + 400, vs.Top + vs.Height / 2 + 300);

            var w = new UI.InputWindow(s, new Core.DeepSeekClient(), "sk-demo", region);
            w.Show();

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    var root = (System.Windows.FrameworkElement)w.Content;
                    root.UpdateLayout();
                    int cw = (int)Math.Ceiling(root.ActualWidth);
                    int ch = (int)Math.Ceiling(root.ActualHeight);
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(cw, ch, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(root);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (var fs = System.IO.File.Create(outPath)) enc.Save(fs);
                    Core.Log.Info($"demo input {cw}x{ch} at ({w.Left:F0},{w.Top:F0}) winW={w.Width:F0} actualW={w.ActualWidth:F0} " +
                                  $"region={region.Width}x{region.Height} -> {outPath}");
                }
                catch (Exception ex)
                {
                    Core.Log.Error("demo input 失败: " + ex);
                }

                w.Close();
                app.Shutdown();
            };
            timer.Start();
        };

        return app.Run();
    }
    private const string LongSample =
        "[本地] Jehst: 谢谢\n" +
        "[队伍] Jehst: 南区前哨站需要医疗支援\n" +
        "[队伍] Jehst: 请求在 9 号点治疗，坐标 X811 Y641\n" +
        "[全部] NUTZ: 我是后来才加进来的，前哨站被围了\n" +
        "[队伍] [KUN] abRoy: 南区前哨站需要补给，尽快\n" +
        "[队伍] Jehst: 收到，我这就过去";

    /// <summary>调试：给定选区尺寸，按真实排版算一次并离屏渲染叠加层内容。</summary>
    private static int DemoCompose(string[] args)
    {
        var outPath = args.Length > 1 && !args[1].StartsWith("--")
            ? args[1]
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gametrans-compose.png");
        var nums = args.Skip(1).Where(a => int.TryParse(a, out _)).Select(int.Parse).ToArray();
        int w = nums.Length >= 2 ? nums[0] : 370;
        int h = nums.Length >= 2 ? nums[1] : 130;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            var s = Core.SettingsStore.Load();
            s.Normalize();
            var vs = Core.ScreenCapture.VirtualScreen();
            var region = new Core.RegionPx(vs.Left + 60, vs.Top + 40, vs.Left + 60 + w, vs.Top + 40 + h);

            var ov = new UI.OverlayWindow();
            ov.ShowText(region, s, LongSample, "1180 ms · deepseek-flash");

            try
            {
                var root = (System.Windows.FrameworkElement)ov.Content;
                int cw = (int)Math.Ceiling(ov.Width);
                int ch = (int)Math.Ceiling(ov.Height);
                root.Measure(new Size(cw, ch));
                root.Arrange(new Rect(0, 0, cw, ch));
                root.UpdateLayout();

                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(cw, ch, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);

                // 叠加层本身是透明窗口，预览图铺一层深色底便于查看效果
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x16, 0x1B)), null,
                        new Rect(0, 0, cw, ch));
                    dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(0, 0, cw, ch));
                }
                rtb.Render(dv);

                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var fs = System.IO.File.Create(outPath)) enc.Save(fs);

                Core.Log.Info($"demo compose: region={w}x{h} window={cw}x{ch} " +
                              $"font={s.Overlay.FontSize} mode={s.Overlay.OverflowMode} -> {outPath}");
            }
            catch (Exception ex)
            {
                Core.Log.Error("demo compose 失败: " + ex);
            }

            ov.Close();
            app.Shutdown();
        };

        return app.Run();
    }
    /// <summary>调试：短暂打开全屏选区（约 1.2 秒），验证冻结截图与遮罩渲染后自动关闭。</summary>
    private static int DemoSelector(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            var w = new UI.RegionSelectorWindow();
            w.PrepareForDemo();
            w.Show();

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    var root = (System.Windows.FrameworkElement)w.Content;
                    root.UpdateLayout();
                    int cw = (int)Math.Ceiling(root.ActualWidth);
                    int ch = (int)Math.Ceiling(root.ActualHeight);
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, cw),
                        Math.Max(1, ch), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(root);

                    // 采样平均亮度，验证 #80000000 遮罩确实生效
                    int stride = cw * 4;
                    var buf = new byte[stride * ch];
                    rtb.CopyPixels(buf, stride, 0);
                    long sum = 0;
                    for (int i = 0; i < buf.Length; i += 4) sum += buf[i] + buf[i + 1] + buf[i + 2];
                    double avg = sum / (double)(buf.Length / 4) / 3.0;
                    Core.Log.Info($"demo selector content {cw}x{ch} avgLuma={avg:0.0} (遮罩后应明显变暗)");
                }
                catch (Exception ex)
                {
                    Core.Log.Error("demo selector 失败: " + ex);
                }

                w.Close();
                app.Shutdown();
            };
            timer.Start();
        };

        return app.Run();
    }
    /// <summary>调试：打开启动页 / 黑话表，0.9 秒后离屏渲染成 PNG。</summary>
    private static int DemoWindow(string[] args, string kind)
    {
        var outPath = args.Length > 1 && !args[1].StartsWith("--")
            ? args[1]
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gametrans-{kind}.png");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            var s = Core.SettingsStore.Load();
            Window w = kind == "startup"
                ? new UI.StartupWindow(s, () => { }, () => { }, () => { }, _ => { })
                : new UI.GlossaryWindow(s.Prompt.Glossary);
            w.Show();

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(900),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    var root = (System.Windows.FrameworkElement)w.Content;
                    root.UpdateLayout();
                    int cw = (int)Math.Ceiling(root.ActualWidth);
                    int ch = (int)Math.Ceiling(root.ActualHeight);
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, cw),
                        Math.Max(1, ch), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(root);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (var fs = System.IO.File.Create(outPath)) enc.Save(fs);
                    Core.Log.Info($"demo {kind} content {cw}x{ch} -> {outPath}");
                }
                catch (Exception ex)
                {
                    Core.Log.Error($"demo {kind} 失败: " + ex);
                }

                w.Close();
                app.Shutdown();
            };
            timer.Start();
        };

        return app.Run();
    }
    private static T? FindChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (c is T t) return t;
            var d = FindChild<T>(c);
            if (d != null) return d;
        }
        return null;
    }

    /// <summary>调试：打开设置窗，0.9 秒后截图保存，用于检查布局。</summary>
    private static int DemoSettings(string[] args)
    {
        var outPath = args.Length > 1 && !args[1].StartsWith("--")
            ? args[1]
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gametrans-settings.png");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            var s = Core.SettingsStore.Load();
            var w = new UI.SettingsWindow(s, _ => { });
            w.Show();

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(900),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    var win = (System.Windows.Window)w;
                    var sv = FindChild<System.Windows.Controls.ScrollViewer>(win) ??
                             throw new InvalidOperationException("找不到 ScrollViewer");
                    var root = (System.Windows.FrameworkElement)sv.Content;
                    root.UpdateLayout();
                    root.Measure(new Size(sv.ViewportWidth > 0 ? sv.ViewportWidth : 740, double.PositiveInfinity));
                    root.Arrange(new Rect(0, 0, sv.ViewportWidth > 0 ? sv.ViewportWidth : 740,
                        Math.Max(200, root.DesiredSize.Height)));
                    root.UpdateLayout();
                    int cw = (int)Math.Ceiling(root.ActualWidth);
                    int ch = (int)Math.Ceiling(root.ActualHeight);
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(cw, ch, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(root);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (var fs = System.IO.File.Create(outPath)) enc.Save(fs);
                    Core.Log.Info($"demo settings content {cw}x{ch} -> {outPath}");
                }
                catch (Exception ex)
                {
                    Core.Log.Error("demo settings 截图失败: " + ex);
                }

                w.Close();
                app.Shutdown();
            };
            timer.Start();
        };

        return app.Run();
    }

    /// <summary>调试：把叠加层显示 2.5 秒（默认屏幕中央 640×240），用于验证样式与原生样式。</summary>
    private static int DemoOverlay(string[] args)
    {
        var nums = args.Skip(1).Where(a => int.TryParse(a, out _)).Select(int.Parse).ToArray();
        int x = nums.Length >= 4 ? nums[0] : 0;
        int y = nums.Length >= 4 ? nums[1] : 0;
        int w = nums.Length >= 4 ? nums[2] : 640;
        int h = nums.Length >= 4 ? nums[3] : 240;

        var vs = Core.ScreenCapture.VirtualScreen();
        if (nums.Length < 4)
        {
            x = vs.Left + (vs.Width - w) / 2;
            y = vs.Top + (vs.Height - h) / 2;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            var settings = Core.SettingsStore.Load();
            var region = new Core.RegionPx(x, y, x + w, y + h);
            var overlay = new UI.OverlayWindow();
            overlay.ShowText(region, settings,
                "示例：敌方五人推中，先别开团，等我绕后。\nOur mid lane is pushing, don't engage yet.",
                "128 ms · deepseek-flash");

            var hwnd = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            Core.Win32.GetWindowRect(hwnd, out var rc);
            long ex = (long)Core.Win32.GetWindowLongPtr(hwnd, Core.Win32.GWL_EXSTYLE);
            Core.Win32.GetWindowDisplayAffinity(hwnd, out uint affinity);
            Core.Log.Info($"demo overlay: visible={Core.Win32.IsWindowVisible(hwnd)} " +
                          $"rect=({rc.Left},{rc.Top},{rc.Right},{rc.Bottom}) exStyle=0x{ex:X} " +
                          $"displayAffinity=0x{affinity:X} (0x11=从截图排除)");

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                overlay.Close();
                app.Shutdown();
            };
            timer.Start();
        };

        return app.Run();
    }
}
