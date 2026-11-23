using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace GameTrans.Core;

/// <summary>网络探测结果分类。用于把「不知道这样的主机」这类系统错误翻译成可执行的排查建议。</summary>
public enum NetworkStatus
{
    Ok,
    DnsFailed,
    Unreachable,
    Timeout,
    TlsFailed,
    Unknown,
}

/// <summary>一次网络探测的结果：<see cref="Summary"/> 面向用户，<see cref="Advice"/> 给出下一步动作。</summary>
public sealed record NetworkProbe(
    NetworkStatus Status,
    string Summary,
    string Advice,
    string Host,
    int Port,
    long ElapsedMs)
{
    public bool Ok => Status == NetworkStatus.Ok;

    /// <summary>展示/日志用的完整文本（摘要 + 建议）。</summary>
    public string FullText => string.IsNullOrWhiteSpace(Advice) ? Summary : Summary + "\n" + Advice;
}

/// <summary>
/// 网络连通性诊断（DNS → TCP），不依赖 <see cref="HttpClient"/>，因此能区分
/// 「域名解析失败」「目标端口不可达」「连接超时」——这些在用户开着网络加速器 /
/// VPN / 代理时非常常见，而 .NET 默认只会抛出「不知道这样的主机」这类难以理解的系统错误。
/// </summary>
public static class NetworkDiagnostics
{
    public const int DefaultTimeoutMs = 5000;
    public const string DefaultBaseUrl = "https://api.deepseek.com";

    /// <summary>从 Base URL 取主机与端口；解析失败时回落到 DeepSeek 官方端点。</summary>
    public static (string Host, int Port) Endpoint(string? baseUrl)
    {
        try
        {
            var b = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
            if (!b.Contains("://", StringComparison.Ordinal)) b = "https://" + b;
            var uri = new Uri(b);
            return (uri.Host, uri.Port);
        }
        catch
        {
            return ("api.deepseek.com", 443);
        }
    }

    /// <summary>做一次 DNS 解析 + TCP 连接探测（默认 5 秒超时），不发送任何 HTTP 数据。</summary>
    public static async Task<NetworkProbe> ProbeAsync(string? baseUrl, CancellationToken ct = default,
        int timeoutMs = DefaultTimeoutMs)
    {
        var (host, port) = Endpoint(baseUrl);
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Math.Clamp(timeoutMs, 1000, 30000));

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            if (addresses.Length == 0)
                return DnsFailedProbe(host, port, sw, Loc.T("DNS 返回空地址列表", "DNS returned no addresses"));

            // 逐个地址尝试：IPv6 不可用时仍可回落到 IPv4。
            Exception? lastError = null;
            foreach (var address in addresses)
            {
                try
                {
                    using var tcp = new TcpClient(address.AddressFamily);
                    await tcp.ConnectAsync(address, port, cts.Token).ConfigureAwait(false);
                    return new NetworkProbe(NetworkStatus.Ok,
                        Loc.F("网络正常：{0}:{1} 可连接（{2} ms）", "Network OK: {0}:{1} reachable ({2} ms)",
                            host, port, sw.ElapsedMilliseconds),
                        "", host, port, sw.ElapsedMilliseconds);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            return lastError is SocketException se
                ? UnreachableProbe(host, port, sw, se)
                : UnknownProbe(host, port, sw, lastError);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new NetworkProbe(NetworkStatus.Timeout,
                Loc.F("连接 {0}:{1} 超时（{2} ms）", "Connecting to {0}:{1} timed out after {2} ms",
                    host, port, Math.Clamp(timeoutMs, 1000, 30000)),
                TimeoutAdvice(host, port), host, port, sw.ElapsedMilliseconds);
        }
        catch (SocketException ex)
        {
            return IsDnsError(ex.SocketErrorCode)
                ? DnsFailedProbe(host, port, sw, ex.Message)
                : UnreachableProbe(host, port, sw, ex);
        }
        catch (Exception ex)
        {
            return UnknownProbe(host, port, sw, ex);
        }
    }

    /// <summary>判断异常链里是否包含「域名解析失败」。</summary>
    public static bool IsNameResolutionFailure(Exception ex)
    {
        if (ex is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }) return true;
        var socket = FindSocketException(ex);
        return socket != null && IsDnsError(socket.SocketErrorCode);
    }

    /// <summary>
    /// 把请求阶段的异常映射成面向用户的说明 + 排查建议。
    /// 重点是 DNS 失败（加速器 / VPN / 代理改写了 DNS 时最常见），
    /// 避免只显示「不知道这样的主机 (api.deepseek.com:443)」。
    /// </summary>
    public static string ExplainRequestFailure(Exception ex, string? baseUrl)
    {
        var (host, port) = Endpoint(baseUrl);
        if (IsNameResolutionFailure(ex))
            return DnsSummary(host) + "\n" + DnsAdvice(host);

        var socket = FindSocketException(ex);
        if (socket != null)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                    Loc.F("网络不可达：无法连接 {0}:{1}。请检查网络连接，或关闭 / 切换加速器、VPN、代理后重试。",
                        "Network unreachable: cannot reach {0}:{1}. Check your connection, or disable / switch the accelerator, VPN or proxy and retry.",
                        host, port) + "\n" + TimeoutAdvice(host, port),
                SocketError.ConnectionRefused =>
                    Loc.F("连接被拒绝：{0}:{1} 未开放。请检查 Base URL / 端口，或加速器是否拦截了该连接。",
                        "Connection refused: {0}:{1} is not accepting connections. Check the Base URL / port, or whether the accelerator is blocking it.",
                        host, port),
                SocketError.TimedOut =>
                    Loc.F("连接 {0}:{1} 超时。请检查网络，或关闭 / 切换加速器、VPN、代理后重试。",
                        "Connecting to {0}:{1} timed out. Check your connection, or disable / switch the accelerator, VPN or proxy and retry.",
                        host, port),
                _ => Loc.T("网络错误：", "Network error: ") + ex.Message,
            };
        }

        return Loc.T("网络错误：", "Network error: ") + ex.Message;
    }

    private static NetworkProbe DnsFailedProbe(string host, int port, Stopwatch sw, string detail)
    {
        var summary = DnsSummary(host);
        if (!string.IsNullOrWhiteSpace(detail))
            summary += Loc.T("（", " (") + detail + Loc.T("）", ")");
        return new NetworkProbe(NetworkStatus.DnsFailed, summary, DnsAdvice(host), host, port,
            sw.ElapsedMilliseconds);
    }

    private static NetworkProbe UnreachableProbe(string host, int port, Stopwatch sw, SocketException se)
    {
        string summary = se.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => Loc.F("无法连接 {0}:{1}：连接被拒绝", "Cannot connect to {0}:{1}: connection refused", host, port),
            SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                Loc.F("无法连接 {0}:{1}：网络不可达", "Cannot connect to {0}:{1}: network unreachable", host, port),
            _ => Loc.F("无法连接 {0}:{1}（{2}）", "Cannot connect to {0}:{1} ({2})", host, port, se.SocketErrorCode),
        };
        return new NetworkProbe(NetworkStatus.Unreachable, summary, TimeoutAdvice(host, port), host, port,
            sw.ElapsedMilliseconds);
    }

    private static NetworkProbe UnknownProbe(string host, int port, Stopwatch sw, Exception? ex) =>
        new(NetworkStatus.Unknown,
            Loc.F("网络诊断失败：{0}", "Network diagnostics failed: {0}", ex?.Message ?? Loc.T("未知错误", "unknown error")),
            Loc.T("请检查 Base URL 是否填写正确，并确认网络可用。",
                "Check that the Base URL is correct and that the network is available."),
            host, port, sw.ElapsedMilliseconds);

    private static string DnsSummary(string host) => Loc.F(
        "DNS 解析失败：无法解析 {0}。", "DNS lookup failed: cannot resolve {0}.", host);

    private static string DnsAdvice(string host) => Loc.T(
        "常见原因是网络加速器 / VPN / 代理改写了 DNS，或网络尚未连接。可尝试：" +
        "① 关闭加速器，或切换加速器模式后重试；" +
        "② 把系统 DNS 改为 223.5.5.5 / 1.1.1.1；" +
        "③ 确认浏览器能打开 https://" + host + " 后再按热键重试。",
        "This usually means an accelerator / VPN / proxy is rewriting DNS, or the network is not ready. Try: " +
        "(1) disable the accelerator or switch its mode and retry; " +
        "(2) set the system DNS to 223.5.5.5 / 1.1.1.1; " +
        "(3) verify a browser can open https://" + host + " and press the hotkey again.");

    private static string TimeoutAdvice(string host, int port) => Loc.F(
        "请检查网络连接，或关闭 / 切换加速器、VPN、代理后重试；也可以确认 https://{0} 是否可达（端口 {1}）。",
        "Check the network connection, or disable / switch the accelerator, VPN or proxy and retry; also verify https://{0} is reachable (port {1}).",
        host, port);

    private static bool IsDnsError(SocketError error) =>
        error is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
            or SocketError.NoRecovery;

    private static SocketException? FindSocketException(Exception? ex)
    {
        for (int depth = 0; ex != null && depth < 8; depth++)
        {
            if (ex is SocketException socket) return socket;
            ex = ex.InnerException;
        }

        return null;
    }
}
