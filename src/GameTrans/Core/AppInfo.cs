namespace GameTrans.Core;

/// <summary>版本与产品标识（关于页、日志、启动页共用）。</summary>
public static class AppInfo
{
    public const string Name = "GameTrans";
    public const string Version = "1.4.0";

    /// <summary>一句话说明，随界面语言切换。</summary>
    public static string Tagline => Loc.T(
        "游戏聊天翻译 · 区域截图识别 + DeepSeek 视觉翻译",
        "Game chat translator · Region capture + DeepSeek vision translation");
}
