using System.Text;

namespace GameTrans.Core;

/// <summary>
/// 模拟键入：把译文以 Unicode 字符注入当前前台窗口（<c>SendInput</c> + <c>KEYEVENTF_UNICODE</c>），
/// 不经过剪贴板。用于设置项「模拟输入」：按 F9 翻译后译文直接出现在游戏聊天输入框里。
///
/// 限制：
///   · 只发送字符，不会自动按 Enter 发送聊天（避免误发消息）；
///   · 目标窗口必须能接收字符消息（绝大多数游戏的聊天输入框可以；自行读取原始键码的极少数
///     输入框除外）；
///   · 目标窗口若以管理员权限运行，非管理员进程的 <c>SendInput</c> 会被 UIPI 拦截，返回失败，
///     调用方会退回复制到剪贴板。
/// </summary>
public static class InputSimulator
{
    /// <summary>
    /// 发送前的文本规整：换行 / 制表符转成空格（避免误触游戏聊天的发送或换行），去掉其余控制字符，
    /// 并合并连续空格。返回空串表示没有可键入内容。
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t')
            {
                AppendSpace(sb);
                continue;
            }

            if (char.IsControl(ch)) continue;
            sb.Append(ch);
        }

        return sb.ToString().Trim();
    }

    private static void AppendSpace(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
    }

    /// <summary>把文本模拟键入到当前前台窗口；发送失败返回 false。</summary>
    public static bool SendText(string text) => Win32.SendUnicodeText(text);
}
