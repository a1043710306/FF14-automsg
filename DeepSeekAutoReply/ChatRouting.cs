using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text;

namespace DeepSeekAutoReply;

internal sealed record ChannelOption(XivChatType Type, string Label, string Command);

internal static class ChatRouting
{
    public static readonly IReadOnlyList<ChannelOption> PrimaryChannels =
    [
        new(XivChatType.Party, "小队（含跨服）", "/p"),
        new(XivChatType.Alliance, "团队", "/a"),
        new(XivChatType.TellIncoming, "私聊", "/tell"),
    ];

    public static readonly IReadOnlyList<ChannelOption> OtherChannels =
    [
        new(XivChatType.Say, "说话", "/say"),
        new(XivChatType.Yell, "呼喊", "/yell"),
        new(XivChatType.Shout, "喊话", "/shout"),
        new(XivChatType.FreeCompany, "部队", "/fc"),
        new(XivChatType.NoviceNetwork, "新人频道", "/beginner"),
        new(XivChatType.PvPTeam, "PvP 战队", "/pvpteam"),
    ];

    public static readonly IReadOnlyList<ChannelOption> LinkshellChannels = NumberedChannels("/l",
        XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4,
        XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8);

    public static readonly IReadOnlyList<ChannelOption> CrossWorldLinkshellChannels = NumberedChannels("/cwl",
        XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3, XivChatType.CrossLinkShell4,
        XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6, XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8);

    public static readonly IReadOnlyList<ChannelOption> AllChannels =
        [.. PrimaryChannels, .. OtherChannels, .. LinkshellChannels, .. CrossWorldLinkshellChannels];

    private static readonly Dictionary<XivChatType, ChannelOption> ChannelsByType = AllChannels.ToDictionary(channel => channel.Type);

    public static XivChatType? GetEnabledChannel(XivChatType source, Configuration config)
    {
        var channel = source == XivChatType.CrossParty ? XivChatType.Party : source;
        var enabled = channel switch
        {
            XivChatType.Party => config.ReplyToParty,
            XivChatType.Alliance => config.ReplyToAlliance,
            XivChatType.TellIncoming => config.ReplyToTell,
            _ => ChannelsByType.ContainsKey(channel) && config.AdditionalChannels?.Contains(channel) == true,
        };
        return enabled ? channel : null;
    }

    public static bool IsPrimaryChannel(XivChatType channel) =>
        channel is XivChatType.Party or XivChatType.Alliance or XivChatType.TellIncoming;

    public static bool UsesGroup(XivChatType channel) => channel is XivChatType.Party or XivChatType.Alliance;

    public static bool UsesLocalArea(XivChatType channel) => channel is XivChatType.Say or XivChatType.Yell or XivChatType.Shout;

    public static string CommandPrefix(XivChatType channel, string? sender)
    {
        if (channel == XivChatType.TellIncoming && string.IsNullOrEmpty(sender))
            throw new ArgumentException("A tell reply needs a recipient.", nameof(sender));

        var command = ChannelsByType[channel].Command;
        return channel == XivChatType.TellIncoming ? $"{command} {sender} " : $"{command} ";
    }

    private static ChannelOption[] NumberedChannels(string command, params XivChatType[] types) =>
        types.Select((type, index) => new ChannelOption(type, (index + 1).ToString(), $"{command}{index + 1}")).ToArray();
}
