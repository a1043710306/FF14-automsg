using Dalamud.Configuration;
using Dalamud.Game.Text;
using System;
using System.Collections.Generic;

namespace DeepSeekAutoReply;

public enum ApiProtocol
{
    ChatCompletions,
    Responses,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    internal const string DefaultSystemPrompt = "你是最终幻想14玩家的自动回复助手。请用自然、简短、友好的中文回复聊天消息，只输出一条回复。不知道玩家的状态时不要猜测，也不要替玩家承诺参加活动。";
    private const string PreviousDefaultSystemPrompt = "你是最终幻想14玩家的自动回复助手。请用自然、简短、友好的中文回复私聊，只输出一条回复。不知道玩家的状态时不要猜测，也不要替玩家承诺参加活动。";

    public int Version { get; set; } = 3;
    public string ApiAddress { get; set; } = "https://api.deepseek.com";
    public string ApiToken { get; set; } = string.Empty;
    public ApiProtocol Protocol { get; set; } = ApiProtocol.ChatCompletions;
    public string ModelId { get; set; } = "deepseek-flash";
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;
    public bool ReplyToParty { get; set; } = true;
    public bool ReplyToAlliance { get; set; } = true;
    public bool ReplyToTell { get; set; } = true;
    public List<XivChatType> AdditionalChannels { get; set; } = [];
    public int CooldownSeconds { get; set; } = 8;
    public int MaxReplyCharacters { get; set; } = 120;
    public int HistoryTurns { get; set; } = ConversationCache.MaxTurns;
    public bool LoggingEnabled { get; set; } = true;
    public bool GameSearchEnabled { get; set; }

    internal void Upgrade()
    {
        if (Version >= 3)
            return;

        if (Version < 2 && SystemPrompt == PreviousDefaultSystemPrompt)
            SystemPrompt = DefaultSystemPrompt;
        if (HistoryTurns == 6)
            HistoryTurns = ConversationCache.MaxTurns;

        Version = 3;
    }
}
