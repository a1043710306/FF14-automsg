using System;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace DeepSeekAutoReply;

internal static class ChatSender
{
    private static readonly Regex CharacterName = new(@"\A[\p{L}\p{M}\p{Nd}'’._·・-]+(?: [\p{L}\p{M}\p{Nd}'’._·・-]+)*\z");
    private static readonly Regex WorldName = new(@"\A[\p{L}\p{M}\p{Nd}'’-]+\z");

    public static string? ResolveOriginalSender(IChatMessage message)
    {
        var displaySender = message.Sender;
        if (displaySender.Payloads.Count == 0)
            return null;

        var original = message.OriginalSender;
        return ResolveSender(original.IsEmpty ? displaySender : SeString.Parse(original.Data.Span));
    }

    internal static string? ResolveSender(SeString sender)
    {
        var player = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (player is not null)
        {
            var name = player.PlayerName.Trim();
            var world = player.World.ValueNullable?.Name.ToString().Trim();
            if (CharacterName.IsMatch(name) && world is not null && WorldName.IsMatch(world))
                return $"{name}@{world}";
        }

        var display = sender.TextValue.Replace((char)SeIconChar.CrossWorld, '@');
        display = string.Concat(display.Where(character => character is < '\uE000' or > '\uF8FF')).Trim();
        var separator = display.IndexOf('@');
        if (separator < 0)
            return CharacterName.IsMatch(display) ? display : null;

        var displayName = display[..separator].Trim();
        var displayWorld = display[(separator + 1)..].Trim();
        return CharacterName.IsMatch(displayName) && WorldName.IsMatch(displayWorld) ? $"{displayName}@{displayWorld}" : null;
    }

    public static bool IsLocalSender(string sender, string localName, string? localWorld)
    {
        var separator = sender.IndexOf('@');
        var senderName = separator < 0 ? sender.AsSpan() : sender.AsSpan(0, separator);
        return senderName.Equals(localName.AsSpan(), StringComparison.OrdinalIgnoreCase)
            && (separator < 0 || string.IsNullOrEmpty(localWorld)
                || sender.AsSpan(separator + 1).Equals(localWorld.AsSpan(), StringComparison.OrdinalIgnoreCase));
    }
}
