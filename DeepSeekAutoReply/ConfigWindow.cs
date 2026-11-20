using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;

namespace DeepSeekAutoReply;

internal sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private string apiAddress;
    private string token;
    private ApiProtocol protocol;
    private string model;
    private string prompt;
    private readonly HashSet<XivChatType> channels;
    private int cooldown;
    private int maxCharacters;
    private int historyTurns;
    private bool loggingEnabled;
    private bool gameSearchEnabled;
    private string status = string.Empty;
    private Task<bool>? clearingHistory;

    public ConfigWindow(Plugin plugin, Configuration config) : base("DeepSeek 自动回复###DeepSeekAutoReply")
    {
        this.plugin = plugin;
        apiAddress = config.ApiAddress ?? string.Empty;
        token = config.ApiToken ?? string.Empty;
        protocol = config.Protocol == ApiProtocol.Responses ? ApiProtocol.Responses : ApiProtocol.ChatCompletions;
        model = config.ModelId ?? string.Empty;
        prompt = config.SystemPrompt ?? string.Empty;
        channels = ChatRouting.AllChannels.Where(option => ChatRouting.GetEnabledChannel(option.Type, config) is not null)
            .Select(option => option.Type).ToHashSet();
        cooldown = Math.Clamp(config.CooldownSeconds, 0, 300);
        maxCharacters = Math.Clamp(config.MaxReplyCharacters, 20, 500);
        historyTurns = Math.Clamp(config.HistoryTurns, 0, ConversationCache.MaxTurns);
        loggingEnabled = config.LoggingEnabled;
        gameSearchEnabled = config.GameSearchEnabled;
        Size = new Vector2(645, 610);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        ImGui.InputText("API 地址", ref apiAddress, 1024);
        ImGui.InputText("Token", ref token, 1024, ImGuiInputTextFlags.Password);
        ImGui.InputText("模型 ID", ref model, 256);
        if (ImGui.BeginCombo("AI 协议", protocol == ApiProtocol.Responses ? "Responses" : "Chat Completions"))
        {
            if (ImGui.Selectable("Chat Completions", protocol == ApiProtocol.ChatCompletions))
                protocol = ApiProtocol.ChatCompletions;
            if (ImGui.Selectable("Responses", protocol == ApiProtocol.Responses))
                protocol = ApiProtocol.Responses;
            ImGui.EndCombo();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("自动回复频道");
        ImGui.SameLine();
        if (ImGui.SmallButton("全选"))
            channels.UnionWith(ChatRouting.AllChannels.Select(option => option.Type));
        ImGui.SameLine();
        if (ImGui.SmallButton("全关"))
            channels.Clear();

        DrawChannelRow(ChatRouting.PrimaryChannels);
        DrawChannelRow(ChatRouting.OtherChannels);
        ImGui.TextUnformatted("通讯贝");
        DrawChannelRow(ChatRouting.LinkshellChannels);
        ImGui.TextUnformatted("跨服通讯贝");
        DrawChannelRow(ChatRouting.CrossWorldLinkshellChannels);
        ImGui.Separator();
        ImGui.InputTextMultiline("系统提示词", ref prompt, 4096, new Vector2(-115, 68));
        ImGui.SliderInt("同一频道或私聊对象冷却（秒）", ref cooldown, 0, 300);
        ImGui.SliderInt("最大回复字数", ref maxCharacters, 20, 500);
        ImGui.InputInt("缓存对话轮数（0–1000）", ref historyTurns);
        ImGui.Checkbox("启用插件日志", ref loggingEnabled);
        ImGui.Checkbox("启用 Garland Tools 查询", ref gameSearchEnabled);
        if (ImGui.SmallButton("清空对话缓存") && clearingHistory is null)
        {
            clearingHistory = plugin.ClearConversationHistory();
            status = "正在清空对话缓存";
        }
        if (clearingHistory is { IsCompletedSuccessfully: true } complete)
        {
            status = complete.Result ? "已清空对话缓存" : "清空失败，查看 /xllog";
            clearingHistory = null;
        }

        ImGui.TextWrapped("历史保存在本机 SQLite 数据库，重启后仍可使用。插件会把当前消息和该会话的历史发送给配置的 API。填 0 可关闭缓存。");

        if (ImGui.Button("保存设置"))
            Save();

        if (status.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(status);
        }
    }

    private void DrawChannelRow(IReadOnlyList<ChannelOption> options)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine();

            var option = options[i];
            var enabled = channels.Contains(option.Type);
            if (!ImGui.Checkbox($"{option.Label}##{option.Type}", ref enabled))
                continue;

            if (enabled)
                channels.Add(option.Type);
            else
                channels.Remove(option.Type);
        }
    }

    private void Save()
    {
        historyTurns = Math.Clamp(historyTurns, 0, ConversationCache.MaxTurns);
        var updated = new Configuration
        {
            ApiAddress = apiAddress.Trim(),
            ApiToken = token.Trim(),
            Protocol = protocol,
            ModelId = model.Trim(),
            SystemPrompt = prompt,
            ReplyToParty = channels.Contains(XivChatType.Party),
            ReplyToAlliance = channels.Contains(XivChatType.Alliance),
            ReplyToTell = channels.Contains(XivChatType.TellIncoming),
            AdditionalChannels = channels.Where(channel => !ChatRouting.IsPrimaryChannel(channel)).Order().ToList(),
            CooldownSeconds = cooldown,
            MaxReplyCharacters = maxCharacters,
            HistoryTurns = historyTurns,
            LoggingEnabled = loggingEnabled,
            GameSearchEnabled = gameSearchEnabled,
        };
        if (updated.ApiToken.Length > 0 && channels.Count > 0
            && !ApiSettings.TryCreate(updated, out _, out status))
            return;

        try
        {
            plugin.SaveConfiguration(updated);
            status = updated.ApiToken.Length == 0 ? "已保存，填写 Token 后开始回复" : "已保存";
        }
        catch (Exception ex)
        {
            status = $"保存失败（{ex.GetType().Name}）";
        }
    }
}
