using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameTrans.Core;

public sealed record TranslationResult(bool Ok, string Text, string? Error, long ElapsedMs, int StatusCode)
{
    public static TranslationResult Fail(string error, long ms = 0, int status = 0) =>
        new(false, "", error, ms, status);
}

/// <summary>
/// DeepSeek 视觉/文本翻译客户端（OpenAI 兼容 Chat Completions）。
/// 文档：POST {base}/chat/completions，content 为 block 数组，image_url 支持 data URL。
/// </summary>
public sealed class DeepSeekClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // 由每次请求的 CTS 控制
    };

    public async Task<TranslationResult> TranslateImageAsync(byte[] png, AppSettings s, string apiKey,
        CancellationToken ct)
    {
        if (png is null || png.Length == 0) return TranslationResult.Fail("截图为空");
        if (string.IsNullOrWhiteSpace(apiKey)) return TranslationResult.Fail("未配置 API Key，请到设置里填写");

        var b64 = Convert.ToBase64String(png);
        var content = new object[]
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
                    ["url"] = "data:image/png;base64," + b64,
                    ["detail"] = s.Api.Detail,
                },
            },
        };

        return await SendAsync(s, apiKey, Prompts.System(s), content, ct).ConfigureAwait(false);
    }

    public async Task<TranslationResult> TranslateTextAsync(string text, AppSettings s, string apiKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return TranslationResult.Fail("没有输入内容");
        if (string.IsNullOrWhiteSpace(apiKey)) return TranslationResult.Fail("未配置 API Key，请到设置里填写");

        // F9 输入翻译：中文 → 英文（方向由 Prompt.Input* 决定）
        var content = new object[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = Prompts.TextInstruction(s) + "\n\n" + text.Trim(),
            },
        };

        return await SendAsync(s, apiKey, Prompts.TextSystem(s), content, ct).ConfigureAwait(false);
    }

    public async Task<TranslationResult> TestAsync(AppSettings s, string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return TranslationResult.Fail("未配置 API Key");

        const string probe = "请只回复两个字：正常";
        var content = new object[]
        {
            new Dictionary<string, object?> { ["type"] = "text", ["text"] = probe },
        };

        return await SendAsync(s, apiKey, Prompts.TextSystem(s), content, ct).ConfigureAwait(false);
    }

    private async Task<TranslationResult> SendAsync(AppSettings s, string apiKey, string systemPrompt,
        object[] userContent, CancellationToken outerCt)
    {
        var sw = Stopwatch.StartNew();
        int attempt = 0;
        TranslationResult last = TranslationResult.Fail("未知错误");

        while (attempt < 2)
        {
            attempt++;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(s.Api.TimeoutSeconds));

            try
            {
                var body = new Dictionary<string, object?>
                {
                    ["model"] = s.Api.Model,
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["role"] = "system",
                            ["content"] = systemPrompt,
                        },
                        new Dictionary<string, object?>
                        {
                            ["role"] = "user",
                            ["content"] = userContent,
                        },
                    },
                    ["max_tokens"] = s.Api.MaxTokens,
                    ["temperature"] = s.Api.Temperature,
                    ["stream"] = false,
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(s.Api.BaseUrl));
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Content = new StringContent(JsonSerializer.Serialize(body, AppSettings.JsonOpts),
                    Encoding.UTF8, "application/json");

                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token).ConfigureAwait(false);

                var raw = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                var status = (int)resp.StatusCode;

                if (!resp.IsSuccessStatusCode)
                {
                    var msg = ExtractError(raw) ?? $"HTTP {status}";
                    last = TranslationResult.Fail(msg, sw.ElapsedMilliseconds, status);

                    // 429/5xx 重试一次
                    if ((status == 429 || status >= 500) && attempt < 2)
                    {
                        await Task.Delay(700, outerCt).ConfigureAwait(false);
                        continue;
                    }

                    return last;
                }

                var text = ExtractContent(raw);
                if (string.IsNullOrWhiteSpace(text))
                    return TranslationResult.Fail("模型返回了空内容", sw.ElapsedMilliseconds, status);

                return new TranslationResult(true, text.Trim(), null, sw.ElapsedMilliseconds, status);
            }
            catch (OperationCanceledException) when (outerCt.IsCancellationRequested)
            {
                return TranslationResult.Fail("已取消", sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                last = TranslationResult.Fail($"请求超时（{s.Api.TimeoutSeconds}s）", sw.ElapsedMilliseconds);
            }
            catch (HttpRequestException ex)
            {
                last = TranslationResult.Fail("网络错误：" + ex.Message, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Error("DeepSeek 请求异常: " + ex);
                return TranslationResult.Fail("请求异常：" + ex.Message, sw.ElapsedMilliseconds);
            }

            if (attempt < 2) await Task.Delay(700, outerCt).ConfigureAwait(false);
        }

        return last;
    }

    private static string BuildEndpoint(string baseUrl)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) b = "https://api.deepseek.com";
        return b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? b
            : b + "/chat/completions";
    }

    private static string? ExtractContent(string raw)
    {
        try
        {
            var node = JsonNode.Parse(raw);
            var content = node?["choices"]?[0]?["message"]?["content"];
            if (content is JsonValue v) return v.GetValue<string>();

            // 防御：内容为数组时拼接 text 片段
            if (content is JsonArray arr)
            {
                var sb = new StringBuilder();
                foreach (var item in arr)
                {
                    var t = item?["text"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(t)) sb.Append(t);
                }

                return sb.ToString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractError(string raw)
    {
        try
        {
            var node = JsonNode.Parse(raw);
            var msg = node?["error"]?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(msg)) return msg;
            var detail = node?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(detail)) return detail;
        }
        catch
        {
        }

        if (raw.Length > 300) raw = raw[..300] + "…";
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }
}
