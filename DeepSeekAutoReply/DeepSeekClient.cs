using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekAutoReply;

internal sealed record ApiSettings(Uri Endpoint, string Token, string Model, string SystemPrompt)
{
    public string HistoryProfile { get; } = Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new[] { Endpoint.AbsoluteUri, Token, Model, SystemPrompt })));

    public static bool TryCreate(Configuration config, out ApiSettings settings, out string error)
    {
        settings = null!;
        var address = config.ApiAddress?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "请输入有效的 HTTP 或 HTTPS API 地址。";
            return false;
        }

        var token = config.ApiToken?.Trim() ?? string.Empty;
        if (token.Length == 0 || Array.Exists(token.ToCharArray(), c => c <= ' ' || c >= '\u007f'))
        {
            error = "请输入有效的 API Token。";
            return false;
        }

        var model = config.ModelId?.Trim() ?? string.Empty;
        if (model.Length == 0)
        {
            error = "请输入模型 ID。";
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var endpoint = new UriBuilder(uri)
        {
            Path = path.EndsWith("/chat/completions", StringComparison.Ordinal)
                ? path
                : path + "/chat/completions",
        }.Uri;
        settings = new ApiSettings(endpoint, token, model, config.SystemPrompt ?? string.Empty);
        error = string.Empty;
        return true;
    }
}

internal sealed class DeepSeekClient : IDisposable
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = 1024 * 1024,
    };

    public async Task<ApiReply> GenerateReplyAsync(ApiSettings settings, string incoming, CancellationToken cancellationToken,
        IReadOnlyList<ConversationTurn>? history = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        var useOfficialFlash = settings.Endpoint.IdnHost == "api.deepseek.com" && settings.Model == "deepseek-flash";
        var messages = new List<ApiMessage>((history?.Count ?? 0) * 2 + 2) { new("system", settings.SystemPrompt) };
        if (history is not null)
        {
            foreach (var turn in history)
            {
                messages.Add(new ApiMessage("user", turn.User));
                messages.Add(new ApiMessage("assistant", turn.Assistant));
            }
        }
        messages.Add(new ApiMessage("user", incoming));

        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            max_tokens = 1024,
            stream = false,
            thinking = useOfficialFlash ? new { type = "disabled" } : null,
            messages,
        }, options: RequestJsonOptions);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var jsonBytes = body.AsMemory(body.AsSpan().StartsWith("\uFEFF"u8) ? 3 : 0);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "未声明";
        var details = new ApiResponseDetails((int)response.StatusCode, contentType, body.Length,
            DescribeBody(jsonBytes.Span, contentType));

        if (!response.IsSuccessStatusCode)
            throw new ApiResponseException(details, "API 返回错误状态");

        try
        {
            using var json = JsonDocument.Parse(jsonBytes);
            var content = ReadReply(json.RootElement, details);
            return new ApiReply(content, details);
        }
        catch (JsonException ex)
        {
            var location = ex.LineNumber is { } line && ex.BytePositionInLine is { } offset
                ? $"，第 {line + 1} 行、字节偏移 {offset}"
                : string.Empty;
            throw new ApiResponseException(details, $"响应不是有效的 JSON{location}");
        }
    }

    public void Dispose() => http.Dispose();

    private static string ReadReply(JsonElement root, ApiResponseDetails details)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ApiResponseException(details, "响应 JSON 的根节点不是对象");

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            var reason = root.TryGetProperty("error", out _) ? "响应包含 error 字段" : "响应缺少 choices 数组";
            throw new ApiResponseException(details, reason);
        }

        if (choices.GetArrayLength() == 0)
            throw new ApiResponseException(details, "choices 数组为空");

        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content))
            throw new ApiResponseException(details, "响应缺少 choices[0].message.content");

        if (content.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new ApiResponseException(details, "choices[0].message.content 不是字符串");

        var reply = content.GetString();
        if (string.IsNullOrWhiteSpace(reply))
            throw new ApiResponseException(details, "模型返回了空回复");

        return reply;
    }

    private static string DescribeBody(ReadOnlySpan<byte> body, string contentType)
    {
        while (!body.IsEmpty && body[0] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            body = body[1..];

        if (body.IsEmpty)
            return "空";
        if (body[0] is (byte)'{' or (byte)'[' or (byte)'"')
            return "疑似 JSON";
        if (body[0] == (byte)'<')
            return "HTML/XML";
        if (contentType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) || body.StartsWith("data:"u8)
            || body.StartsWith("event:"u8) || body[0] == (byte)':')
            return "SSE 流";
        return "其他文本或二进制";
    }

    private sealed record ApiMessage([property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);
}

internal sealed record ApiReply(string Text, ApiResponseDetails Response);

internal sealed record ApiResponseDetails(int StatusCode, string ContentType, int BodyBytes, string BodyFormat)
{
    public override string ToString() =>
        $"HTTP {StatusCode}，Content-Type {ContentType}，{BodyBytes} 字节，正文类型 {BodyFormat}";
}

internal sealed class ApiResponseException(ApiResponseDetails response, string reason)
    : Exception($"{reason}（{response}）")
{
    public ApiResponseDetails Response { get; } = response;
}
