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

internal sealed record ApiSettings(Uri Endpoint, string Token, string Model, string SystemPrompt, ApiProtocol Protocol,
    bool GameSearchEnabled)
{
    private const string ChatEndpoint = "/chat/completions";
    private const string ResponsesEndpoint = "/responses";
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

        if (config.Protocol is not (ApiProtocol.ChatCompletions or ApiProtocol.Responses))
        {
            error = "请选择有效的 AI 协议。";
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var suffix = config.Protocol == ApiProtocol.Responses ? ResponsesEndpoint : ChatEndpoint;
        var otherSuffix = config.Protocol == ApiProtocol.Responses ? ChatEndpoint : ResponsesEndpoint;
        if (path.EndsWith(otherSuffix, StringComparison.OrdinalIgnoreCase))
            path = path[..^otherSuffix.Length];

        var endpoint = new UriBuilder(uri)
        {
            Path = path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? path : path + suffix,
        }.Uri;
        settings = new ApiSettings(endpoint, token, model, config.SystemPrompt ?? string.Empty, config.Protocol, config.GameSearchEnabled);
        error = string.Empty;
        return true;
    }
}

internal sealed class DeepSeekClient(GarlandSearch? search = null) : IDisposable
{
    private const int MaxToolRounds = 2;
    private const int MaxLookups = 3;
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = 1024 * 1024,
    };
    private readonly GarlandSearch garland = search ?? new GarlandSearch();

    public async Task<ApiReply> GenerateReplyAsync(ApiSettings settings, string incoming, CancellationToken cancellationToken,
        IReadOnlyList<ConversationTurn>? history = null)
    {
        using var deadline = settings.GameSearchEnabled ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        deadline?.CancelAfter(TimeSpan.FromSeconds(40));
        var token = deadline?.Token ?? cancellationToken;
        var lookups = 0;
        var misses = 0;
        int? databaseStatus = null;
        var useResponses = settings.Protocol == ApiProtocol.Responses;
        var messageType = useResponses ? "message" : null;
        var prompt = settings.GameSearchEnabled ? $"{settings.SystemPrompt}\n\n{GarlandTool.Instructions}" : settings.SystemPrompt;
        var messages = new List<object>((history?.Count ?? 0) * 2 + 2);
        if (!useResponses)
            messages.Add(new ApiMessage("system", prompt));

        if (history is not null)
        {
            foreach (var turn in history)
            {
                messages.Add(new ApiMessage("user", turn.User, messageType));
                messages.Add(new ApiMessage("assistant", turn.Assistant, messageType));
            }
        }
        messages.Add(new ApiMessage("user", incoming, messageType));

        for (var round = 0; ; round++)
        {
            var canCall = settings.GameSearchEnabled && round < MaxToolRounds && lookups < MaxLookups;
            var (root, details) = await SendAsync(settings, CreateContent(settings, messages, prompt, canCall), token)
                .ConfigureAwait(false);
            var calls = settings.GameSearchEnabled ? ReadToolCalls(root, details, useResponses, messages) : [];
            if (calls.Count == 0)
            {
                var text = useResponses ? ReadResponsesReply(root, details) : ReadChatReply(root, details);
                return new ApiReply(text, details, lookups, misses, databaseStatus);
            }
            if (!canCall)
                throw new ApiResponseException(details, "模型超出了资料查询次数上限");

            foreach (var call in calls)
            {
                GarlandResult result;
                if (call.Name != GarlandTool.Name || !GarlandTool.TryGetQuery(call.Arguments, out var query))
                    result = new GarlandResult([], null, "无效的 Garland Tools 查询");
                else if (lookups >= MaxLookups)
                    result = new GarlandResult([], null, "本次回复的资料查询次数已用完");
                else
                {
                    lookups++;
                    result = await garland.SearchAsync(query, token).ConfigureAwait(false);
                    databaseStatus = result.HttpStatus;
                    if (result.Entries.Count == 0)
                        misses++;
                }

                messages.Add(useResponses
                    ? new { type = "function_call_output", call_id = call.Id, output = result.ToToolOutput() }
                    : new { role = "tool", tool_call_id = call.Id, content = result.ToToolOutput() });
            }
        }
    }

    private static JsonContent CreateContent(ApiSettings settings, List<object> messages, string prompt, bool canCall)
    {
        var isOfficialDeepSeek = settings.Endpoint.IdnHost == "api.deepseek.com";
        var useOfficialFlash = isOfficialDeepSeek && settings.Model == "deepseek-flash";
        var toolChoice = settings.GameSearchEnabled ? (canCall ? "auto" : "none") : null;
        return settings.Protocol == ApiProtocol.Responses
            ? JsonContent.Create(new
            {
                model = settings.Model,
                instructions = string.IsNullOrWhiteSpace(prompt) ? null : prompt,
                input = messages,
                max_output_tokens = 1024,
                stream = false,
                store = isOfficialDeepSeek ? (bool?)null : false,
                reasoning = useOfficialFlash ? new { effort = "none" } : null,
                tools = settings.GameSearchEnabled ? GarlandTool.ResponsesDefinition : null,
                tool_choice = toolChoice,
            }, options: RequestJsonOptions)
            : JsonContent.Create(new
            {
                model = settings.Model,
                max_tokens = 1024,
                stream = false,
                thinking = useOfficialFlash ? new { type = "disabled" } : null,
                tools = settings.GameSearchEnabled ? GarlandTool.ChatDefinition : null,
                tool_choice = toolChoice,
                messages,
            }, options: RequestJsonOptions);
    }

    private async Task<(JsonElement Root, ApiResponseDetails Details)> SendAsync(ApiSettings settings, JsonContent content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        request.Content = content;
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
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ApiResponseException(details, "响应 JSON 的根节点不是对象");

            return (root.Clone(), details);
        }
        catch (JsonException ex)
        {
            var location = ex.LineNumber is { } line && ex.BytePositionInLine is { } offset
                ? $"，第 {line + 1} 行、字节偏移 {offset}"
                : string.Empty;
            throw new ApiResponseException(details, $"响应不是有效的 JSON{location}");
        }
    }

    public void Dispose()
    {
        http.Dispose();
        garland.Dispose();
    }

    private static string ReadChatReply(JsonElement root, ApiResponseDetails details)
    {
        var message = ChatMessage(root, details);
        if (!message.TryGetProperty("content", out var content))
            throw new ApiResponseException(details, "响应缺少 choices[0].message.content");
        if (content.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new ApiResponseException(details, "choices[0].message.content 不是字符串");

        var reply = content.GetString();
        if (string.IsNullOrWhiteSpace(reply))
            throw new ApiResponseException(details, "模型返回了空回复");

        return reply;
    }

    private static JsonElement ChatMessage(JsonElement root, ApiResponseDetails details)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            var reason = root.TryGetProperty("error", out _) ? "响应包含 error 字段" : "响应缺少 choices 数组";
            throw new ApiResponseException(details, reason);
        }

        if (choices.GetArrayLength() == 0)
            throw new ApiResponseException(details, "choices 数组为空");

        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object)
            throw new ApiResponseException(details, "响应缺少 choices[0].message");
        return message;
    }

    private static void ValidateResponsesStatus(JsonElement root, ApiResponseDetails details)
    {
        var status = StringField(root, "status");
        if (status is "failed" or "cancelled" || root.TryGetProperty("error", out var error)
            && error.ValueKind != JsonValueKind.Null)
            throw new ApiResponseException(details, "Responses API 报告请求失败");

        if (status is "queued" or "in_progress")
            throw new ApiResponseException(details, "Responses API 尚未生成最终回复");

        if (status == "incomplete")
        {
            var reason = root.TryGetProperty("incomplete_details", out var incomplete) ? StringField(incomplete, "reason") : null;
            var suffix = reason is "max_output_tokens" or "content_filter" ? $"（{reason}）" : string.Empty;
            throw new ApiResponseException(details, $"Responses API 返回了未完成的回复{suffix}");
        }
    }

    private static List<ToolCall> ReadToolCalls(JsonElement root, ApiResponseDetails details, bool useResponses, List<object> messages)
    {
        var calls = new List<ToolCall>();
        if (useResponses)
        {
            ValidateResponsesStatus(root, details);
            if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                return calls;

            foreach (var item in output.EnumerateArray())
            {
                if (StringField(item, "type") == "function_call")
                    calls.Add(CreateToolCall(item, "call_id", item, details));
            }
            if (calls.Count > 0)
            {
                foreach (var item in output.EnumerateArray())
                    messages.Add(item);
            }
        }
        else
        {
            var message = ChatMessage(root, details);
            if (!message.TryGetProperty("tool_calls", out var requested) || requested.ValueKind != JsonValueKind.Array)
                return calls;

            foreach (var item in requested.EnumerateArray())
            {
                var function = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("function", out var value) ? value : default;
                calls.Add(CreateToolCall(item, "id", function, details));
            }
            if (calls.Count > 0)
                messages.Add(new { role = "assistant", content = StringField(message, "content"), tool_calls = requested,
                    reasoning_content = StringField(message, "reasoning_content") });
        }

        if (calls.Count > 8)
            throw new ApiResponseException(details, "模型一次返回了过多工具调用");
        return calls;
    }

    private static ToolCall CreateToolCall(JsonElement item, string idField, JsonElement function, ApiResponseDetails details)
    {
        var id = StringField(item, idField);
        if (string.IsNullOrEmpty(id) || id.Length > 128)
            throw new ApiResponseException(details, "模型工具调用 ID 无效");

        return new ToolCall(id, StringField(function, "name"), StringField(function, "arguments"));
    }

    private static string ReadResponsesReply(JsonElement root, ApiResponseDetails details)
    {
        ValidateResponsesStatus(root, details);

        var parts = new List<string>();
        var refused = false;
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (StringField(item, "type") != "message" || StringField(item, "role") != "assistant"
                    || !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var part in content.EnumerateArray())
                {
                    var type = StringField(part, "type");
                    var text = type == "output_text" ? StringField(part, "text") : null;
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(text);
                    if (type == "refusal")
                        refused = true;
                }
            }
        }

        if (parts.Count > 0)
            return string.Join("\n", parts);
        if (refused)
            throw new ApiResponseException(details, "模型拒绝生成回复");

        var fallback = StringField(root, "output_text");
        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback;

        throw new ApiResponseException(details, "Responses 回复中缺少可用的 output_text");
    }

    private static string? StringField(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
        [property: JsonPropertyName("content")] string Content, [property: JsonPropertyName("type")] string? Type = null);
    private sealed record ToolCall(string Id, string? Name, string? Arguments);
}

internal sealed record ApiReply(string Text, ApiResponseDetails Response, int Lookups, int LookupMisses, int? DatabaseHttpStatus);

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
