using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class ConnectionTestResult
{
    public bool Success { get; set; }
    public int? HttpStatus { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string Message { get; set; }
}

// A single bounded inference request, using the same protocol as the configured
// provider. Never retry a potentially billable request or follow redirects.
public sealed class ConnectionTestClient
{
    private readonly HttpMessageHandler handler;
    private readonly TimeSpan timeout;
    public ConnectionTestClient() : this(null, TimeSpan.FromSeconds(30)) { }
    internal ConnectionTestClient(HttpMessageHandler handler, TimeSpan timeout)
    { this.handler = handler; this.timeout = timeout; }

    internal async Task<ConnectionTestResult> TestAsync(string baseUrl, string model, string apiKey, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        int? status = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }))
            using (var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/responses"))
            {
                client.Timeout = timeout;
                client.MaxResponseContentBufferSize = 1024 * 1024;
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent(Json.Write(new {
                    model = model, input = "Reply with OK only.", stream = false, store = false, max_output_tokens = 64
                }), Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false))
                {
                    status = (int)response.StatusCode;
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    Dictionary<string, object> payload = null;
                    try { payload = Json.Read(body); }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (InvalidDataException) { }

                    object error;
                    var errorObject = payload != null && payload.TryGetValue("error", out error) ? error as Dictionary<string, object> : null;
                    string code = errorObject == null ? null : Json.Text(errorObject, "code");
                    if (!response.IsSuccessStatusCode)
                        return Result(false, status, timer, FailureMessage(status.Value, code));
                    // HTTP 200 alone is insufficient (HTML, public models, errors
                    // and unfinished responses must never be reported as success).
                    if (payload == null || Json.Text(payload, "object") != "response" ||
                        String.IsNullOrWhiteSpace(Json.Text(payload, "id")))
                        return Result(false, status, timer, "地址可达，但返回内容不是有效的 Responses 响应。请检查网关地址及协议支持。");
                    if (payload.TryGetValue("error", out error) && error != null)
                        return Result(false, status, timer, FailureMessage(status.Value, code));
                    if (Json.Text(payload, "status") != "completed")
                        return Result(false, status, timer, "地址可达，但模型请求未完成，可能达到测试的输出上限。未确认连接测试成功。");
                    if (!HasOutputText(payload))
                        return Result(false, status, timer, "地址可达，但模型未返回有效文本。请检查模型是否支持 Responses 文本请求。");
                    return Result(true, status, timer, "连接测试成功：当前密钥和模型已完成一次 Responses 文本请求。仅验证本次非流式调用，不代表剩余余额或完整 Codex 功能均已验证。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Result(false, status, timer, cancellationToken.IsCancellationRequested
                ? "已取消等待；若请求已到达网关，仍可能产生费用。"
                : "连接测试超时；请检查网络及上游响应速度。请求可能已被处理，未自动重试。");
        }
        catch (HttpRequestException)
        { return Result(false, status, timer, "连接或响应读取失败。请检查域名、端口、代理及 HTTPS 证书；响应大小不得超过 1 MiB。未自动重试。"); }
        catch (IOException)
        { return Result(false, status, timer, "连接中断，未确认模型调用完成。未自动重试。"); }
        // Do not expose exception messages or response text: a gateway may echo
        // Authorization or other private data in either of them.
        catch (Exception)
        { return Result(false, status, timer, "连接测试未完成。请检查网关地址和模型设置后重试。"); }
    }

    private static bool HasOutputText(Dictionary<string, object> payload)
    {
        object output;
        if (!payload.TryGetValue("output", out output) || !(output is object[])) return false;
        foreach (var item in ((object[])output).OfType<Dictionary<string, object>>())
        {
            object content;
            if (Json.Text(item, "type") != "message" || Json.Text(item, "role") != "assistant" ||
                !item.TryGetValue("content", out content) || !(content is object[])) continue;
            if (((object[])content).OfType<Dictionary<string, object>>().Any(part =>
                Json.Text(part, "type") == "output_text" && !String.IsNullOrWhiteSpace(Json.Text(part, "text")))) return true;
        }
        return false;
    }

    private static string FailureMessage(int status, string code)
    {
        // Only known codes are mapped; raw server messages are never displayed.
        switch (code)
        {
            case "invalid_api_key": return "密钥无效或已停用，请检查 API Key 与网关是否匹配。";
            case "api_key_expired": return "API Key 已过期，请更换密钥。";
            case "model_not_allowed": return "当前 API Key 无权使用所选模型。";
            case "ip_not_allowed": return "当前出口 IP 不在 API Key 的允许列表中。";
            case "model_not_found": return "模型不存在、未开放或尚未配置价格，请检查模型 ID。";
            case "insufficient_balance": return "可用余额不足，无法完成本次测试。";
            case "daily_budget_exceeded": return "API Key 的每日预算已达到上限。";
            case "monthly_budget_exceeded": return "API Key 的每月预算已达到上限。";
            case "rpm_limit_exceeded": return "请求次数超过限流上限，请稍后再试。";
            case "tpm_limit_exceeded": return "Token 用量超过限流上限，请稍后再试。";
            case "concurrency_limit_exceeded": return "并发请求已达上限，请等待其他请求完成。";
            case "no_available_accounts": return "网关当前没有可用的上游账号。";
            case "adapter_not_enabled": return "网关尚未启用本次请求所需的协议能力。";
        }
        if (status >= 300 && status < 400) return "网关返回重定向，已停止请求。请直接填写最终网关地址。";
        switch (status)
        {
            case 400: case 422: return "网关拒绝测试参数，请检查所选模型是否支持 Responses 及输出限制。";
            case 401: return "鉴权失败，请检查 API Key 是否有效。";
            case 402: return "余额或预算不足，无法完成本次测试。";
            case 403: return "访问被拒绝，请检查模型权限、IP 限制及网关访问策略。";
            case 404: case 405: return "未找到兼容接口或模型，请检查网关地址和模型 ID。";
            case 408: case 504: return "网关或上游请求超时，未确认调用完成。";
            case 429: return "已触发限流或配额限制，请稍后再试。";
            case 501: return "网关不支持本次请求所需的协议能力。";
            case 502: case 503: return "网关或上游服务暂不可用，请检查上游账号和服务状态。";
            default: return status >= 500 ? "网关内部错误，请检查服务端日志。" : "网关返回失败结果，未确认模型调用完成。";
        }
    }

    private static ConnectionTestResult Result(bool success, int? status, Stopwatch timer, string message)
    { return new ConnectionTestResult { Success = success, HttpStatus = status, ElapsedMilliseconds = timer.ElapsedMilliseconds, Message = message }; }
}
