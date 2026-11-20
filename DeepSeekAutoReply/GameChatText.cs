using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace DeepSeekAutoReply;

internal sealed record GameReference(string Kind, string Name, uint? Id = null);

internal static class GameChatText
{
    private const int MaxReferences = 12;

    public static IReadOnlyList<GameReference> ReadLinks(ReadOnlySeStringSpan message,
        Func<ItemPayload, string?>? resolveItemName = null, IReadOnlyList<GameReference>? decodedLinks = null)
    {
        var references = new List<GameReference>();
        var label = new StringBuilder();
        string? kind = null;
        string? embeddedName = null;
        uint? id = null;
        ItemKind itemKind = ItemKind.Normal;
        foreach (var payload in message)
        {
            if (payload.Type == ReadOnlySePayloadType.Macro && payload.MacroCode == MacroCode.Link)
            {
                FinishLink();
                var expressions = payload.GetEnumerator();
                if (expressions.MoveNext() && expressions.Current.TryGetUInt(out var linkType))
                {
                    kind = (LinkMacroPayloadType)linkType switch
                    {
                        LinkMacroPayloadType.Item => "道具",
                        LinkMacroPayloadType.Quest => "任务",
                        LinkMacroPayloadType.Achievement => "成就",
                        LinkMacroPayloadType.Status => "状态",
                        LinkMacroPayloadType.Emote => "情感动作",
                        _ => null,
                    };
                    if (expressions.MoveNext() && expressions.Current.TryGetUInt(out var rawId) && rawId > 0)
                    {
                        if ((LinkMacroPayloadType)linkType == LinkMacroPayloadType.Item)
                        {
                            var item = ItemUtil.GetBaseId(rawId);
                            id = item.ItemId;
                            itemKind = item.Kind;
                        }
                        else if ((LinkMacroPayloadType)linkType == LinkMacroPayloadType.Quest)
                        {
                            id = rawId < 65536 ? rawId + 65536 : rawId;
                        }
                        else if ((LinkMacroPayloadType)linkType == LinkMacroPayloadType.Status)
                        {
                            id = rawId;
                        }
                    }
                    if (kind == "道具")
                    {
                        while (expressions.MoveNext())
                        {
                            if (!expressions.Current.TryGetString(out var name))
                                continue;

                            embeddedName = name.ExtractText();
                            break;
                        }
                    }
                }
            }
            else if (kind is not null && payload.Type == ReadOnlySePayloadType.Text)
            {
                label.Append(Encoding.UTF8.GetString(payload.Body));
            }
        }
        FinishLink();
        if (decodedLinks is not null)
        {
            foreach (var decoded in decodedLinks)
                AddReference(decoded);
        }
        return references;

        void FinishLink()
        {
            if (kind is not null && references.Count < MaxReferences)
            {
                var name = CleanName(label.ToString());
                if (name.Length == 0)
                    name = CleanName(embeddedName);

                var decoded = id is not null
                    ? decodedLinks?.FirstOrDefault(reference => reference.Kind == kind && reference.Id == id)
                    : null;
                if (decoded is not null)
                {
                    var localName = CleanName(decoded.Name);
                    if (localName.Length > 0)
                        name = localName;
                }
                else if (kind == "道具" && id is { } itemId && resolveItemName is not null)
                {
                    var localName = CleanName(resolveItemName(new ItemPayload(itemId, itemKind, embeddedName)));
                    if (localName.Length > 0)
                        name = localName;
                }

                if (name.Length > 0 || id is not null)
                    AddReference(new GameReference(kind, name, id));
            }

            label.Clear();
            kind = null;
            embeddedName = null;
            id = null;
            itemKind = ItemKind.Normal;
        }

        void AddReference(GameReference reference)
        {
            if (references.Count >= MaxReferences)
                return;

            var existing = references.FindIndex(value => value.Kind == reference.Kind
                && (value.Id is not null && reference.Id is not null
                    ? value.Id == reference.Id
                    : reference.Name.Length > 0 && string.Equals(value.Name, reference.Name, StringComparison.Ordinal)));
            if (existing < 0)
                references.Add(reference);
            else if (references[existing].Name.Length == 0 && reference.Name.Length > 0)
                references[existing] = reference;
        }
    }

    private static string CleanName(string? display) =>
        string.Concat((display ?? string.Empty).Where(character => character is < '\uE000' or > '\uF8FF'))
            .Trim().Trim('[', ']', '【', '】').Trim();

    public static string Enrich(string text, IReadOnlyList<GameReference> links, IEnumerable<GameReference> actions)
    {
        var references = links.Concat(actions).Distinct().Take(MaxReferences).ToArray();
        if (references.Length == 0)
            return text;

        var hints = string.Join("、", references.Select(reference =>
        {
            var name = reference.Name.Length > 0 ? $"「{reference.Name}」" : string.Empty;
            var id = reference.Id is { } number ? $"（ID {number}）" : string.Empty;
            return $"{reference.Kind}{name}{id}";
        }));
        return $"{text}\n\n消息中识别到的游戏内容\n{hints}";
    }
}

internal sealed class ActionNameIndex(IEnumerable<GameReference> actions)
{
    private readonly Dictionary<char, GameReference[]> byFirstCharacter = actions
        .Where(action => action.Name.Length is >= 2 and <= 64)
        .GroupBy(action => action.Name, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.DistinctBy(action => action.Id).Count() == 1 ? group.First() : new GameReference("技能", group.Key))
        .GroupBy(action => char.ToUpperInvariant(action.Name[0]))
        .ToDictionary(group => group.Key, group => group.OrderByDescending(action => action.Name.Length).ToArray());

    public IReadOnlyList<GameReference> Match(string text)
    {
        var matches = new HashSet<GameReference>();
        for (var start = 0; start < text.Length && matches.Count < 8; start++)
        {
            if (!byFirstCharacter.TryGetValue(char.ToUpperInvariant(text[start]), out var candidates))
                continue;

            var found = Array.Find(candidates, action => text.AsSpan(start).StartsWith(action.Name, StringComparison.OrdinalIgnoreCase));
            if (found is null)
                continue;

            matches.Add(found);
            start += found.Name.Length - 1;
        }

        return matches.ToArray();
    }
}
