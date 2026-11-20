using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekAutoReply;

internal sealed record GarlandQuery(string Name, string Kind, uint? Id);

internal sealed record GarlandEntry(string Kind, uint Id, string Name, string Url, string? Description = null,
    IReadOnlyDictionary<string, string>? Details = null, IReadOnlyList<string>? Related = null);

internal sealed record GarlandResult(IReadOnlyList<GarlandEntry> Entries, int? HttpStatus, string? Error)
{
    public string ToToolOutput() => JsonSerializer.Serialize(new
    {
        source = "Garland Tools 中文数据库",
        entries = Entries,
        status = HttpStatus,
        error = Error,
    }, JsonSerializerOptions.Web);
}

internal sealed partial class GarlandSearch : IDisposable
{
    private const string Origin = "https://garlandtools.cn";
    private const int MaxMatches = 4;
    private const int MaxDetails = 2;
    private static readonly IReadOnlyDictionary<string, int> DetailVersions = new Dictionary<string, int>
    {
        ["item"] = 3,
        ["quest"] = 2,
        ["action"] = 2,
        ["achievement"] = 2,
    };
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
        { "item", "quest", "action", "achievement", "status", "instance", "npc", "mob", "fate", "leve", "node", "fishing" };

    private readonly HttpClient http;

    public GarlandSearch(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(7),
            MaxResponseContentBufferSize = 2 * 1024 * 1024,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepSeekAutoReply/1.7 (Garland Tools lookup)");
    }

    public static bool IsKnownKind(string kind) => kind == "any" || Kinds.Contains(kind);

    public async Task<GarlandResult> SearchAsync(GarlandQuery query, CancellationToken cancellationToken)
    {
        int? status = null;
        try
        {
            if (query.Id is { } id && DetailVersions.ContainsKey(query.Kind))
            {
                var exact = await EnrichAsync(CreateEntry(query.Kind, id, query.Name), cancellationToken).ConfigureAwait(false);
                if (exact.Entry.Description is not null || exact.Entry.Details is not null)
                    return new GarlandResult([exact.Entry], exact.Status, null);
                status = exact.Status;
            }

            if (query.Name.Length == 0)
                return new GarlandResult([], status, status is null ? "缺少可搜索的名称" : $"数据库详情返回 HTTP {status}");

            var url = $"{Origin}/api/search.php?lang=chs&text={Uri.EscapeDataString(query.Name)}";
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return new GarlandResult([], status, $"数据库搜索返回 HTTP {status}");

            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new GarlandResult([], status, "数据库搜索结果不是列表");

            var results = document.RootElement.EnumerateArray().Select(ReadSearchEntry).OfType<GarlandEntry>()
                .Where(entry => query.Kind == "any" || entry.Kind == query.Kind)
                .OrderByDescending(entry => string.Equals(entry.Name, query.Name, StringComparison.OrdinalIgnoreCase))
                .ThenBy(entry => entry.Id)
                .DistinctBy(entry => (entry.Kind, entry.Id))
                .Take(MaxMatches)
                .ToArray();
            if (results.Length == 0)
                return new GarlandResult([], status, "没有找到匹配的数据库条目");

            var detailed = await Task.WhenAll(results.Take(MaxDetails).Select(entry => EnrichAsync(entry, cancellationToken)))
                .ConfigureAwait(false);
            for (var i = 0; i < detailed.Length; i++)
                results[i] = detailed[i].Entry;
            return new GarlandResult(results, status, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GarlandResult([], status, "数据库请求超时");
        }
        catch (JsonException)
        {
            return new GarlandResult([], status, "数据库响应不是有效的 JSON");
        }
        catch (HttpRequestException)
        {
            return new GarlandResult([], status, "数据库网络请求失败");
        }
    }

    public void Dispose() => http.Dispose();

    private async Task<(GarlandEntry Entry, int? Status)> EnrichAsync(GarlandEntry entry, CancellationToken cancellationToken)
    {
        if (!DetailVersions.TryGetValue(entry.Kind, out var version))
            return (entry, null);

        try
        {
            var url = $"{Origin}/db/doc/{entry.Kind}/chs/{version}/{entry.Id}.json";
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return (entry, status);

            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!Property(root, entry.Kind, out var detail) || detail.ValueKind != JsonValueKind.Object
                || !ReadId(detail, "id", out var id) || id != entry.Id)
                return (entry, status);

            var fields = new Dictionary<string, string>();
            foreach (var (key, label) in new[]
            {
                ("ilvl", "品级"), ("lvl", "等级"), ("cast", "咏唱毫秒"), ("recast", "复唱毫秒"),
                ("range", "距离"), ("patch", "版本"), ("location", "地点"),
            })
            {
                if (Property(detail, key, out var value) && value.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                    fields[label] = value.ToString();
            }

            var related = new List<string>();
            if (Property(root, "partials", out var partials) && partials.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in partials.EnumerateArray())
                {
                    var match = ReadSearchEntry(part);
                    if (match is not null && match.Kind is "npc" or "instance" or "quest" or "mob")
                        related.Add($"{match.Kind} {match.Name}");
                    if (related.Count >= 8)
                        break;
                }
            }

            if (entry.Kind == "quest" && ReadId(detail, "target", out var target)
                && Property(root, "partials", out var questPartials) && questPartials.ValueKind == JsonValueKind.Array)
            {
                var npc = questPartials.EnumerateArray().Select(ReadSearchEntry)
                    .FirstOrDefault(match => match?.Kind == "npc" && match.Id == target);
                if (npc is not null)
                    fields["任务目标 NPC"] = npc.Name;
            }

            return (entry with
            {
                Name = StringField(detail, "name") ?? entry.Name,
                Description = CleanText(StringField(detail, "description")),
                Details = fields,
                Related = related,
            }, status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (entry, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return (entry, null);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static GarlandEntry? ReadSearchEntry(JsonElement item)
    {
        var kind = StringField(item, "type");
        if (kind is null || !Kinds.Contains(kind) || !ReadId(item, "id", out var id)
            || !Property(item, "obj", out var entry))
            return null;

        var name = StringField(entry, "n");
        return string.IsNullOrWhiteSpace(name) ? null : CreateEntry(kind, id, name);
    }

    private static GarlandEntry CreateEntry(string kind, uint id, string name) =>
        new(kind, id, name, $"{Origin}/db/#{kind}/{id}");

    private static bool ReadId(JsonElement value, string key, out uint id)
    {
        id = 0;
        return Property(value, key, out var element)
            && uint.TryParse(element.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    private static bool Property(JsonElement value, string key, out JsonElement property)
    {
        property = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out property);
    }

    private static string? StringField(JsonElement value, string key) =>
        Property(value, key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string? CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = Whitespace().Replace(Tags().Replace(WebUtility.HtmlDecode(value), " "), " ").Trim();
        return text.Length <= 650 ? text : text[..650];
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
