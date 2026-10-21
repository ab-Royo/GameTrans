using System.IO;
using System.Text;
using System.Text.Json;

namespace GameTrans.Core;

public static class SettingsStore
{
    public static string AppDir { get; } = AppContext.BaseDirectory;

    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("GAMETRANS_DATA") is { Length: > 0 } env
            ? env
            : File.Exists(Path.Combine(AppDir, "portable.txt"))
                ? AppDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameTrans");

    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string LogPath => Path.Combine(DataDir, "GameTrans.log");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                var s = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOpts);
                if (s != null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取配置失败，使用默认值: " + ex.Message);
        }

        var def = new AppSettings();
        def.Normalize();
        return def;
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(s, AppSettings.JsonOpts);
            File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Error("保存配置失败: " + ex);
        }
    }

    public static void SaveApiKey(AppSettings s, string plain)
    {
        if (string.IsNullOrWhiteSpace(plain))
        {
            s.Api.ApiKeyProtected = "";
            return;
        }

        var cipher = Win32.Protect(Encoding.UTF8.GetBytes(plain.Trim()));
        s.Api.ApiKeyProtected = cipher == null ? "" : Convert.ToBase64String(cipher);
    }

    public static string ReadApiKey(AppSettings s)
    {
        if (string.IsNullOrEmpty(s.Api.ApiKeyProtected)) return "";
        try
        {
            var cipher = Convert.FromBase64String(s.Api.ApiKeyProtected);
            var plain = Win32.Unprotect(cipher);
            return plain == null ? "" : Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return "";
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.DataDir);
                var fi = new FileInfo(SettingsStore.LogPath);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    var bak = SettingsStore.LogPath + ".1";
                    File.Delete(bak);
                    File.Move(SettingsStore.LogPath, bak);
                }

                File.AppendAllText(SettingsStore.LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
