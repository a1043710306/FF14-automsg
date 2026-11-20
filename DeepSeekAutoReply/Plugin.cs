using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using GameAction = Lumina.Excel.Sheets.Action;
using GameEventItem = Lumina.Excel.Sheets.EventItem;

namespace DeepSeekAutoReply;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/dsreply";
    private const string TestCommandName = "/dstest";
    private const string ItemTestCommandName = "/dsitem";
    private const string TestMessageTag = "DeepSeek 测试";
    private const int MaxChatBytes = 500;
    private static readonly ReplyDestination TestDestination = new("dstest", null, null, null, null);

    private readonly DeepSeekClient api = new();
    private readonly ConversationCache conversations;
    private readonly Lazy<Task<ActionNameIndex>> actionNames;
    private readonly Dictionary<string, long> lastRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<CompletedReply> completed = new();
    private readonly WindowSystem windows = new("DeepSeekAutoReply");
    private readonly ConfigWindow configWindow;
    private Configuration configuration;
    private volatile bool loggingEnabled;
    private ApiSettings? apiSettings;
    private CancellationTokenSource requests = new();
    private int revision;

    public Plugin()
    {
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Upgrade();
        ApplyConfiguration();
        var databasePath = Path.Combine(PluginInterface.ConfigDirectory.FullName, "conversation-cache.sqlite3");
        conversations = new ConversationCache(databasePath);
        actionNames = new Lazy<Task<ActionNameIndex>>(() => Task.Run(BuildActionNameIndex));
        DebugLog($"SQLite 对话缓存路径 {databasePath}");
        configWindow = new ConfigWindow(this, configuration);
        windows.AddWindow(configWindow);
        CommandManager.AddHandler(CommandName, new CommandInfo((_, _) => configWindow.Toggle())
        {
            HelpMessage = "打开 DeepSeek 自动回复设置",
        });
        CommandManager.AddHandler(TestCommandName, new CommandInfo(OnTestCommand)
        {
            HelpMessage = "测试已保存的 DeepSeek 配置，用法 /dstest [测试文本]",
        });
        CommandManager.AddHandler(ItemTestCommandName, new CommandInfo(OnItemTestCommand)
        {
            HelpMessage = "读取游戏客户端道具名称，用法 /dsitem 物品ID",
        });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        ChatGui.ChatMessage += OnChatMessage;
        ClientState.Logout += OnLogout;
        Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Framework.Update -= OnUpdate;
        ClientState.Logout -= OnLogout;
        ChatGui.ChatMessage -= OnChatMessage;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(TestCommandName);
        CommandManager.RemoveHandler(ItemTestCommandName);
        windows.RemoveAllWindows();
        requests.Cancel();
        requests.Dispose();
        _ = ObserveCacheOperationAsync(conversations.CloseAsync(), "关闭");
        api.Dispose();
    }

    internal void SaveConfiguration(Configuration updated)
    {
        PluginInterface.SavePluginConfig(updated);
        var previousApiSettings = apiSettings;
        var previousLimit = configuration.HistoryTurns;
        var wasLoggingEnabled = loggingEnabled;
        configuration = updated;
        ApplyConfiguration();
        if (!wasLoggingEnabled && loggingEnabled)
            DebugLog($"SQLite 对话缓存路径 {conversations.DatabasePath}");
        ResetRequests();
        if (previousApiSettings?.HistoryProfile != apiSettings?.HistoryProfile)
            _ = ObserveCacheOperationAsync(conversations.ClearAsync(), "清空");
        else if (previousLimit != configuration.HistoryTurns)
            _ = ObserveCacheOperationAsync(conversations.SetLimitAsync(configuration.HistoryTurns), "调整轮数");
    }

    internal Task<bool> ClearConversationHistory()
    {
        ResetRequests();
        return ObserveCacheOperationAsync(conversations.ClearAsync(), "清空");
    }

    private void ApplyConfiguration()
    {
        loggingEnabled = configuration.LoggingEnabled;
        apiSettings = ApiSettings.TryCreate(configuration, out var settings, out _) ? settings : null;
    }

    private void ResetRequests()
    {
        revision++;
        requests.Cancel();
        requests.Dispose();
        requests = new CancellationTokenSource();
        lastRequests.Clear();
        pending.Clear();
    }

    private void OnLogout(int type, int code) => ResetRequests();

    private void OnTestCommand(string command, string args)
    {
        if (!ApiSettings.TryCreate(configuration, out var settings, out var error))
        {
            ChatGui.PrintError($"请在 /dsreply 中保存有效的 API 配置。{error}", TestMessageTag);
            return;
        }

        if (!pending.Add(TestDestination.Key))
        {
            PrintTestMessage("已有一条测试请求正在处理。");
            return;
        }

        var text = string.IsNullOrWhiteSpace(args) ? "在吗？" : args.Trim();
        PrintTestMessage("正在测试 API，请稍候。");
        _ = GenerateAsync(settings, TestDestination, text, revision, requests.Token);
    }

    private void OnItemTestCommand(string command, string args)
    {
        if (!uint.TryParse(args.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var rawId) || rawId == 0)
        {
            ChatGui.PrintError("输入物品 ID，例如 /dsitem 43543", TestMessageTag);
            return;
        }

        var (id, kind) = ItemUtil.GetBaseId(rawId);
        var name = ReadLocalItemName(new ItemPayload(id, kind));
        if (string.IsNullOrWhiteSpace(name))
            ChatGui.PrintError($"客户端未返回道具名称（ID {id}，{kind}）", TestMessageTag);
        else
            PrintTestMessage($"本地道具 ID {id}（{kind}）「{name}」");
    }

    private static void PrintTestMessage(string text)
    {
        ChatGui.Print(new XivChatEntry
        {
            Type = XivChatType.Echo,
            Message = $"[{TestMessageTag}] {text}",
        });
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        var type = message.LogKind;
        if (!ClientState.IsLoggedIn)
            return;

        if (ChatRouting.GetEnabledChannel(type, configuration) is not { } channel)
        {
            if (type is XivChatType.Party or XivChatType.CrossParty)
                DebugLog($"[{type}] 小队自动回复未启用");
            return;
        }

        if (message.SourceKind == XivChatRelationKind.LocalPlayer)
        {
            DebugLog($"[{type}] 跳过自己发送的消息");
            return;
        }

        if (apiSettings is not { } settings)
        {
            DebugLog($"[{type}] 已收到消息，API 配置未就绪");
            return;
        }

        var content = message.Message;
        if (content.Payloads.Count == 0 && !message.MessageModified)
            return;

        var isTell = channel == XivChatType.TellIncoming;
        var localPlayerLoaded = PlayerState.IsLoaded;
        var senderName = isTell || localPlayerLoaded ? ChatSender.ResolveOriginalSender(message) : null;
        if (isTell && senderName is null)
        {
            DebugLog($"[{type}] 找不到可用于 /tell 的发送者");
            return;
        }

        if (localPlayerLoaded && senderName is not null
            && ChatSender.IsLocalSender(senderName, PlayerState.CharacterName, PlayerState.HomeWorld.ValueNullable?.Name.ToString()))
        {
            DebugLog($"[{type}] 发送者是本地玩家，跳过");
            return;
        }

        var key = isTell ? $"tell:{senderName}" : channel.ToString();
        if (pending.Contains(key))
        {
            DebugLog($"[{type}] 已有一条回复正在生成");
            return;
        }

        var now = Environment.TickCount64;
        var cooldown = Math.Clamp(configuration.CooldownSeconds, 0, 300) * 1000L;
        if (lastRequests.TryGetValue(key, out var previous) && now - previous < cooldown)
        {
            DebugLog($"[{type}] 自动回复冷却中");
            return;
        }

        var usesGroup = ChatRouting.UsesGroup(channel);
        var group = usesGroup ? CaptureGroup(type) : null;
        if (usesGroup && group is null)
        {
            DebugLog($"[{type}] 已收到消息，当前队伍资料尚未就绪");
            return;
        }

        var text = content.TextValue.Trim();
        var links = ReadGameLinks(message, content);
        if (text.Length == 0 && links.Count == 0)
            return;

        var area = ChatRouting.UsesLocalArea(channel) ? CaptureChatArea() : (LocalChatArea?)null;
        var destination = new ReplyDestination(key, channel, type, senderName, group, area);
        lastRequests[key] = now;
        pending.Add(key);
        _ = GenerateAsync(settings, destination, text, revision, requests.Token, links);
    }

    private IReadOnlyList<GameReference> ReadGameLinks(IHandleableChatMessage message, SeString displayed)
    {
        var original = message.OriginalMessage;
        var decoded = displayed;
        if (message.MessageModified && !original.IsEmpty)
        {
            try
            {
                decoded = SeString.Parse(original.Data.Span);
            }
            catch (Exception ex)
            {
                DebugLog($"[{message.LogKind}] 原始聊天载荷解码失败（{ex.GetType().Name}）");
            }
        }

        var references = new List<GameReference>();
        foreach (var payload in decoded.Payloads)
        {
            try
            {
                if (payload is ItemPayload item)
                {
                    references.Add(new GameReference("道具", ReadLocalItemName(item) ?? string.Empty, item.ItemId));
                }
                else if (payload is QuestPayload quest)
                {
                    var row = quest.Quest;
                    references.Add(new GameReference("任务", row.ValueNullable?.Name.ExtractText() ?? string.Empty, row.RowId));
                }
                else if (payload is StatusPayload status)
                {
                    var row = status.Status;
                    references.Add(new GameReference("状态", row.ValueNullable?.Name.ExtractText() ?? string.Empty, row.RowId));
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[{message.LogKind}] 本地游戏载荷读取失败（{payload.GetType().Name}，{ex.GetType().Name}）");
            }
        }

        var links = GameChatText.ReadLinks(original.AsSpan(), ReadLocalItemName, references);
        if (loggingEnabled && (links.Count > 0 || message.MessageModified || decoded.Payloads.Any(payload => payload is RawPayload)))
        {
            var types = string.Join("、", decoded.Payloads.Select(payload => payload.Type.ToString()).Distinct());
            var ids = string.Join("、", links.Select(link =>
                $"{link.Kind} ID {link.Id?.ToString(CultureInfo.InvariantCulture) ?? "无"}（{(link.Name.Length > 0 ? "有名称" : "无名称")}）"));
            DebugLog($"[{message.LogKind}] 游戏链接识别 {links.Count} 条，Dalamud 解码 {references.Count} 条，载荷 {types} {ids}");
        }
        return links;
    }

    private static LocalChatArea CaptureChatArea() =>
        new(ClientState.TerritoryType, ClientState.MapId, ClientState.Instance);

    private static unsafe GroupContext? CaptureGroup(XivChatType type)
    {
        var alliance = type == XivChatType.Alliance;
        var territory = ClientState.TerritoryType;
        var instance = ClientState.Instance;
        var partyId = PartyList.PartyId;
        if (type == XivChatType.Party && CaptureLocalGroup() is { } localParty)
            return localParty;

        var crossRealm = InfoProxyCrossRealm.Instance();
        if (crossRealm != null
            && (alliance ? crossRealm->IsInAllianceRaid
                : type == XivChatType.CrossParty || crossRealm->IsInCrossRealmParty || crossRealm->IsInAllianceRaid))
        {
            var memberIds = ReadCrossRealmMembers(crossRealm, alliance);
            if (GroupContext.Create(territory, instance, partyId: 0, memberIds, hasLocalParty: false) is { } crossParty)
                return crossParty;
        }

        return type == XivChatType.Party ? null : CaptureLocalGroup();

        GroupContext? CaptureLocalGroup()
        {
            if (PartyList.Length < 2 || (alliance && !PartyList.IsAlliance))
                return null;

            return GroupContext.Create(territory, instance, partyId,
                PartyList.Select(member => unchecked((ulong)member.ContentId)), hasLocalParty: true);
        }
    }

    private static unsafe List<ulong> ReadCrossRealmMembers(InfoProxyCrossRealm* crossRealm, bool alliance)
    {
        var groups = crossRealm->CrossRealmGroups;
        var count = Math.Min((int)crossRealm->GroupCount, groups.Length);
        var localGroup = crossRealm->LocalPlayerGroupIndex;
        var members = new List<ulong>();
        if (!alliance && localGroup >= count)
            return members;

        for (var i = alliance ? 0 : localGroup; i < (alliance ? count : localGroup + 1); i++)
        {
            ref var group = ref groups[i];
            var roster = group.GroupMembers;
            var memberCount = Math.Min((int)group.GroupMemberCount, roster.Length);
            for (var j = 0; j < memberCount; j++)
                members.Add(roster[j].ContentId);
        }

        return members;
    }

    private async Task GenerateAsync(ApiSettings settings, ReplyDestination destination, string incoming, int requestRevision,
        CancellationToken cancellationToken, IReadOnlyList<GameReference>? links = null)
    {
        var started = Stopwatch.GetTimestamp();
        var server = settings.Endpoint.GetLeftPart(UriPartial.Authority);
        var scope = destination.GetConversationScope(PlayerState.ContentId, settings.HistoryProfile);
        var historyTurns = configuration.HistoryTurns;
        var modelInput = incoming;
        var reply = string.Empty;
        var error = string.Empty;
        try
        {
            var matches = settings.GameSearchEnabled && incoming.Length > 0
                ? (await actionNames.Value.WaitAsync(cancellationToken).ConfigureAwait(false)).Match(incoming)
                : [];
            modelInput = GameChatText.Enrich(incoming, links ?? [], matches);
            var historyStarted = Stopwatch.GetTimestamp();
            var history = await ReadHistoryAsync(scope, historyTurns).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var historyDetails = historyTurns <= 0 ? "对话缓存已关闭"
                : scope is null ? "未识别会话"
                : $"SQLite 历史 {history.Length} 轮，读取耗时 {Stopwatch.GetElapsedTime(historyStarted).TotalMilliseconds:F0} ms";
            DebugLog($"[{destination.LogChannel}] 开始向 API 请求回复（{settings.Protocol}，{server}，{historyDetails}）");
            var result = await api.GenerateReplyAsync(settings, modelInput, cancellationToken, history).ConfigureAwait(false);
            reply = result.Text;
            if (settings.GameSearchEnabled)
            {
                DebugLog($"[{destination.LogChannel}] Garland Tools 查询 {result.Lookups} 次，未匹配 {result.LookupMisses} 次，末次 HTTP 状态 {result.DatabaseHttpStatus?.ToString() ?? "未知"}");
            }
            DebugLog($"[{destination.LogChannel}] API 响应成功（{result.Response}，{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms）");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            error = settings.GameSearchEnabled ? "AI 或资料查询超时" : "API 请求超时";
        }
        catch (ApiResponseException ex)
        {
            error = ex.Message;
        }
        catch (HttpRequestException ex)
        {
            error = ex.StatusCode is { } status
                ? $"API 请求失败（HTTP {(int)status}，{ex.HttpRequestError}）"
                : $"API 请求失败（{ex.HttpRequestError}）";
        }
        catch (Exception ex)
        {
            error = $"API 请求失败（{ex.GetType().Name}）";
        }

        if (error.Length > 0)
            error += $"，协议 {settings.Protocol}，服务 {server}，耗时 {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms";
        completed.Enqueue(new CompletedReply(requestRevision, destination, scope, modelInput, reply, error));
    }

    private string? ReadLocalItemName(ItemPayload payload)
    {
        try
        {
            var language = DataManager.Language;
            var name = ItemUtil.GetItemName(payload.RawItemId, includeIcon: false, language: language).ExtractText();
            if (string.IsNullOrWhiteSpace(name) && payload.RawItemId >= (uint)ItemKind.EventItem
                && DataManager.GetExcelSheet<GameEventItem>(language).TryGetRow(payload.RawItemId, out var eventItem))
                name = (eventItem.Name.IsEmpty ? eventItem.Singular : eventItem.Name).ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            DebugLog($"客户端没有返回道具 ID {payload.ItemId} 的名称（{payload.Kind}）");
        }
        catch (Exception ex)
        {
            WarningLog($"无法读取本地道具 ID {payload.ItemId}（{ex.GetType().Name}）");
        }
        return payload.DisplayName;
    }

    private ActionNameIndex BuildActionNameIndex()
    {
        try
        {
            var sheet = DataManager.GetExcelSheet<GameAction>();
            return new ActionNameIndex(sheet.Where(action => action.IsPlayerAction)
                .Select(action => new GameReference("技能", action.Name.ExtractText(), action.RowId)));
        }
        catch (Exception ex)
        {
            WarningLog($"无法读取游戏技能名称（{ex.GetType().Name}）");
            return new ActionNameIndex([]);
        }
    }

    private void OnUpdate(IFramework framework)
    {
        if (!completed.TryDequeue(out var result) || result.Revision != revision)
            return;

        var destination = result.Destination;
        pending.Remove(destination.Key);
        if (result.Error.Length > 0)
        {
            WarningLog($"[{destination.LogChannel}] {result.Error}");
            if (destination.Channel is null)
                ChatGui.PrintError(result.Error, TestMessageTag);
            return;
        }

        var maxCharacters = Math.Clamp(configuration.MaxReplyCharacters, 20, 500);
        if (destination.Channel is not { } channel)
        {
            var preview = FormatReply(result.Text, maxCharacters, MaxChatBytes);
            if (preview.Length > 0)
            {
                PrintTestMessage(preview);
                _ = RecordHistoryAsync(result, preview);
            }
            else
                ChatGui.PrintError("模型回复中没有可显示的文字。", TestMessageTag);
            return;
        }

        if (!ClientState.IsLoggedIn)
            return;

        if (destination.SourceType is { } source && destination.Group is { } group && !group.Matches(CaptureGroup(source)))
        {
            DebugLog($"[{destination.SourceType}] 队伍已变更或资料丢失，放弃发送");
            return;
        }

        if (destination.Area is { } area && area != CaptureChatArea())
        {
            DebugLog($"[{destination.LogChannel}] 地图或实例已变更，放弃发送");
            return;
        }

        var prefix = ChatRouting.CommandPrefix(channel, destination.Sender);
        var reply = FormatReply(result.Text, maxCharacters, MaxChatBytes - Encoding.UTF8.GetByteCount(prefix));
        if (reply.Length == 0)
            return;

        try
        {
            lastRequests[destination.Key] = Environment.TickCount64;
            SendChat(prefix + reply);
        }
        catch (Exception ex)
        {
            WarningLog($"[{destination.SourceType}] 无法提交游戏聊天回复（{ex.GetType().Name}）");
            return;
        }

        _ = RecordHistoryAsync(result, reply);
        DebugLog($"[{destination.SourceType}] 已提交回复给游戏客户端");
    }

    private async Task<ConversationTurn[]> ReadHistoryAsync(ConversationScope? scope, int maxTurns)
    {
        try
        {
            return await conversations.ReadAsync(scope, maxTurns).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCacheFailure("读取", ex);
            return [];
        }
    }

    private async Task RecordHistoryAsync(CompletedReply result, string reply)
    {
        try
        {
            var saved = await conversations.RecordAsync(result.Scope, configuration.HistoryTurns, result.Incoming, reply)
                .ConfigureAwait(false);
            if (saved is { } stats)
            {
                var replaced = stats.ReplacedContext ? "，已替换旧上下文" : string.Empty;
                DebugLog($"[{result.Destination.LogChannel}] SQLite 对话已写入，裁剪 {stats.TrimmedTurns} 轮，移除 {stats.EvictedConversations} 个旧会话{replaced}");
            }
        }
        catch (Exception ex)
        {
            LogCacheFailure("写入", ex);
        }
    }

    private async Task<bool> ObserveCacheOperationAsync(Task operation, string action)
    {
        try
        {
            await operation.ConfigureAwait(false);
            DebugLog($"SQLite 对话缓存{action}完成");
            return true;
        }
        catch (Exception ex)
        {
            LogCacheFailure(action, ex);
            return false;
        }
    }

    private void LogCacheFailure(string action, Exception exception)
    {
        var reason = exception is SQLite.SQLiteException sqlite ? sqlite.Result.ToString() : exception.GetType().Name;
        WarningLog($"SQLite 对话缓存{action}失败（{reason}）");
    }

    private void DebugLog(string message)
    {
        if (loggingEnabled)
            Log.Debug(message);
    }

    private void WarningLog(string message)
    {
        if (loggingEnabled)
            Log.Warning(message);
    }

    internal static string FormatReply(string text, int maxCharacters, int maxBytes)
    {
        var reply = new StringBuilder();
        foreach (var value in text.Trim().EnumerateRunes())
        {
            if (Rune.IsControl(value) && !Rune.IsWhiteSpace(value))
                continue;

            var rune = value.Value switch
            {
                '<' => new Rune('＜'),
                '>' => new Rune('＞'),
                _ => Rune.IsWhiteSpace(value) ? new Rune(' ') : value,
            };

            if (maxCharacters == 0 || rune.Utf8SequenceLength > maxBytes)
                break;

            reply.Append(rune.ToString());
            maxCharacters--;
            maxBytes -= rune.Utf8SequenceLength;
        }

        return reply.ToString().Trim();
    }

    private static unsafe void SendChat(string command)
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            throw new InvalidOperationException("游戏聊天接口尚未就绪");

        var text = new Utf8String(command);
        try
        {
            uiModule->ProcessChatBoxEntry(&text);
        }
        finally
        {
            text.Dtor();
        }
    }

    private sealed record ReplyDestination(string Key, XivChatType? Channel, XivChatType? SourceType, string? Sender,
        GroupContext? Group, LocalChatArea? Area = null)
    {
        public string LogChannel => SourceType?.ToString() ?? "dstest";

        public ConversationScope? GetConversationScope(ulong owner, string apiProfile)
        {
            if (Channel is null)
                return new ConversationScope(owner, Key, ApiProfile: apiProfile);

            return Sender is null || owner == 0
                ? null
                : new ConversationScope(owner, $"{Channel}:{Sender}", Group, Area, apiProfile);
        }
    }
    private sealed record CompletedReply(int Revision, ReplyDestination Destination, ConversationScope? Scope,
        string Incoming, string Text, string Error);
}
