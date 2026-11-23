using System.Threading;
using System.Windows;
using GameTrans.Core;

namespace GameTrans;

public static class Program
{
    private const string MutexName = "Global\\GameTrans.SingleInstance.v1";

    /// <summary>单实例互斥体；以管理员身份重启时需要先释放，否则新进程会被旧进程挡住。</summary>
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static int Main(string[] args)
    {
        ApplyLanguageArg(args);

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

        if (args.Any(a => a.Equals("--demo-admin-prompt", StringComparison.OrdinalIgnoreCase)))
            return DemoAdminPrompt(args);

        if (args.Any(a => a.Equals("--demo-glossary", StringComparison.OrdinalIgnoreCase)))
            return DemoWindow(args, "glossary");

        if (args.Any(a => a.Equals("--demo-input", StringComparison.OrdinalIgnoreCase)))
            return DemoInput(args);

        if (args.Any(a => a.Equals("--demo-selector", StringComparison.OrdinalIgnoreCase)))
            return DemoSelector(args);

        if (args.Any(a => a.Equals("--demo-compose", StringComparison.OrdinalIgnoreCase)))
            return DemoCompose(args);

        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            // 已有实例：提示后退出（不弹窗打扰）
            UI.ToastWindow.ShowToast(Core.Loc.T("GameTrans 已在运行，请查看系统托盘", "GameTrans is already running; check the system tray"));
            Thread.Sleep(1200);
            return 0;
        }

        var settings = SettingsStore.Load();
        Loc.SetLanguage(settings.Ui.Language);
        Log.Info($"启动 v{Core.AppInfo.Version} · 配置目录 {SettingsStore.DataDir} · 区域 {settings.Region.Width}x{settings.Region.Height}");

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("未处理异常：" + e.Exception);
            UI.ToastWindow.ShowToast(Core.Loc.T("运行异常：", "Runtime error: ") + e.Exception.Message, true);
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
                MessageBox.Show(Core.Loc.T("GameTrans 启动失败：\n", "GameTrans failed to start:\n") + ex.Message, "GameTrans");
                app.Shutdown();
            }
        };

        try
        {
            return app.Run();
        }
        finally
        {
            ReleaseSingleInstanceMutex();
        }
    }

    /// <summary>
    /// 释放单实例互斥体（以管理员身份重启前调用）。正常退出时由 <see cref="Main"/> 的 finally 兜底，
    /// 重复调用安全。
    /// </summary>
    internal static void ReleaseSingleInstanceMutex()
    {
        var mutex = Interlocked.Exchange(ref _singleInstanceMutex, null);
        if (mutex == null) return;
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 当前线程不持有互斥体（例如重启已经释放过）时忽略
        }
        catch (Exception ex)
        {
            Log.Warn("释放单实例互斥体失败: " + ex.Message);
        }
        finally
        {
            try
            {
                mutex.Dispose();
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>提权重启被取消时，尝试重新拿回单实例保护；失败也不影响当前进程继续运行。</summary>
    internal static bool TryReacquireSingleInstanceMutex()
    {
        if (Interlocked.CompareExchange(ref _singleInstanceMutex, null, null) != null) return true;
        try
        {
            var mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }

            var previous = Interlocked.CompareExchange(ref _singleInstanceMutex, mutex, null);
            if (previous != null)
            {
                // 别的路径已经拿回来了
                try
                {
                    mutex.ReleaseMutex();
                }
                catch
                {
                    // 忽略
                }

                mutex.Dispose();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("重新获取单实例互斥体失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>解析 --lang zh|en（供离屏截图与自检按指定语言渲染），缺省保持当前语言。</summary>
    private static void ApplyLanguageArg(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--lang", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                Loc.SetLanguage(args[i + 1]);
                return;
            }

            if (args[i].StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
            {
                Loc.SetLanguage(args[i]["--lang=".Length..]);
                return;
            }
        }
    }

    /// <summary>调试/截图用：读取配置并把界面语言与目标 API 预设切到当前渲染语言。</summary>
    private static AppSettings LoadDemoSettings()
    {
        var s = SettingsStore.Load();
        if (!string.Equals(s.Ui.Language, Loc.Language, StringComparison.Ordinal))
        {
            s.Ui.Language = Loc.Language;
            var site = ApiSites.DefaultFor(Loc.Language);
            s.Api.Site = site.Code;
            s.Api.BaseUrl = site.BaseUrl;
        }

        s.Normalize();
        return s;
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
            var s = LoadDemoSettings();
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
            var s = LoadDemoSettings();
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
    /// <summary>调试：打开模拟输入管理员提示框，0.8 秒后离屏渲染成 PNG。</summary>
    private static int DemoAdminPrompt(string[] args)
    {
        var outPath = args.Length > 1 && !args[1].StartsWith("--")
            ? args[1]
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gametrans-admin-prompt.png");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            var w = UI.AdminPromptWindow.CreateForDemo();
            w.Show();

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800),
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
                    Core.Log.Info($"demo admin prompt content {cw}x{ch} -> {outPath}");
                }
                catch (Exception ex)
                {
                    Core.Log.Error("demo admin prompt 截图失败: " + ex);
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
            var s = LoadDemoSettings();
            Window w = kind == "startup"
                ? new UI.StartupWindow(s, () => { }, () => { }, () => { }, _ => { })
                : new UI.GlossaryWindow(s.Prompt.Glossary);
            w.Show();

            // 截图里把「网络」状态也带上：真实应用会在启动后自动探测，这里同样跑一次。
            if (kind == "startup" && w is UI.StartupWindow startupWindow)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var probe = await Core.NetworkDiagnostics.ProbeAsync(s.Api.BaseUrl);
                        _ = w.Dispatcher.BeginInvoke(new Action(() => startupWindow.SetNetwork(probe)));
                    }
                    catch (Exception ex)
                    {
                        Core.Log.Warn("demo startup 网络探测失败: " + ex.Message);
                    }
                });
            }

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(kind == "startup" ? 1600 : 900),
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
            var s = LoadDemoSettings();
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
                    int pad = 16; // root 自身 Margin，单独渲染会丢失，这里补上四周留白
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(cw + pad * 2, ch + pad * 2, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    var dv = new System.Windows.Media.DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(0x15, 0x16, 0x1A)), null,
                            new Rect(0, 0, cw + pad * 2, ch + pad * 2));
                        dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null,
                            new Rect(pad, pad, cw, ch));
                    }

                    rtb.Render(dv);
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
            var settings = LoadDemoSettings();
            var region = new Core.RegionPx(x, y, x + w, y + h);
            var overlay = new UI.OverlayWindow();
            overlay.ShowText(region, settings,
                Core.Loc.T(
                    "示例：敌方五人推中，先别开团，等我绕后。\nOur mid lane is pushing, don't engage yet.",
                    "Example: The enemy is pushing mid with five, don't engage yet, wait for me to flank."),
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
