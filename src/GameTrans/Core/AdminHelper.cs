using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace GameTrans.Core;

/// <summary>
/// 管理员权限检测与提权重启。
/// 程序本身仍以 <c>asInvoker</c> 启动（普通权限足够截图、热键、剪贴板）；
/// 只有「模拟输入」可能受 UIPI 限制：游戏 / 加速器以管理员权限运行时，普通权限进程的
/// <c>SendInput</c> 会被系统静默拦截。因此按需提示用户以管理员身份重启，而不是强制整个程序提权。
/// </summary>
public static class AdminHelper
{
    private static readonly Lazy<bool> Elevated = new(DetectElevated, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>当前进程是否以管理员权限运行。</summary>
    public static bool IsElevated => Elevated.Value;

    private static bool DetectElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Log.Warn("检测管理员权限失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>设置页 / 启动页的醒目短提示（开启模拟输入但未提权时）。</summary>
    public static string SimulateInputShortWarning => Loc.T(
        "模拟输入已开启，但当前不是管理员权限：若游戏或加速器以管理员运行，键入会被系统拦截。" +
        "请以管理员身份重启 GameTrans，或关闭模拟输入。",
        "Simulated input is on but the app is not elevated: typing will be blocked if the game or accelerator runs as administrator. " +
        "Restart GameTrans as administrator, or turn simulated input off.");

    /// <summary>保存设置后的完整说明（模拟输入需要管理员权限）。</summary>
    public static string SimulateInputFullWarning => Loc.T(
        "模拟输入需要管理员权限：游戏或加速器以管理员权限运行时，普通权限进程的 SendInput 会被系统（UIPI）拦截，" +
        "译文无法键入游戏，程序会退回复制到剪贴板。\n" +
        "建议以管理员身份重启 GameTrans；若不想提升权限，可关闭「模拟输入」，继续使用「复制到剪贴板 + 手动粘贴」。",
        "Simulated input needs administrator rights: when the game or accelerator runs elevated, Windows (UIPI) blocks SendInput " +
        "from a non-elevated process, so the translation cannot be typed into the game and the app falls back to the clipboard.\n" +
        "Restart GameTrans as administrator, or turn simulated input off and keep using clipboard + manual paste.");

    /// <summary>
    /// 以管理员身份重启当前程序。成功返回 <c>null</c>（新进程已拉起，调用方应退出当前实例）；
    /// 失败（例如用户取消 UAC）返回可展示的错误说明，当前实例继续运行。
    /// </summary>
    public static string? RestartElevated()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            return Loc.T("无法确定程序路径，请手动以管理员身份运行 GameTrans。",
                "Cannot determine the executable path; start GameTrans as administrator manually.");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };

            // 新进程需要单实例互斥体；先释放，避免它一启动就判定「已在运行」而退出。
            // 提权失败 / 用户取消时再拿回来，当前实例继续提供单实例保护。
            Program.ReleaseSingleInstanceMutex();
            try
            {
                Process.Start(psi);
            }
            catch
            {
                Program.TryReacquireSingleInstanceMutex();
                throw;
            }

            Log.Info("已请求以管理员身份重启: " + exe);
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("用户取消了管理员授权");
            return Loc.T("已取消管理员授权，程序仍以普通权限运行。",
                "Administrator elevation was cancelled; the app is still running unelevated.");
        }
        catch (Exception ex)
        {
            Log.Error("以管理员身份重启失败: " + ex);
            return Loc.T("以管理员身份重启失败：", "Failed to restart as administrator: ") + ex.Message;
        }
    }
}
