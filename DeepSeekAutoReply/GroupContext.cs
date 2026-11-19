using System.Collections.Generic;
using System.Linq;

namespace DeepSeekAutoReply;

internal sealed record GroupContext(uint Territory, uint Instance, long PartyId, string? Members)
{
    public static GroupContext? Create(uint territory, uint instance, long partyId, IEnumerable<ulong> memberIds,
        bool hasLocalParty)
    {
        var ids = memberIds.Where(id => id != 0).Distinct().Order().ToArray();
        if (ids.Length > 1)
            return new GroupContext(territory, instance, partyId, string.Join(",", ids));

        return hasLocalParty && partyId != 0 ? new GroupContext(territory, instance, partyId, null) : null;
    }

    public bool Matches(GroupContext? current)
    {
        if (current is null || Territory != current.Territory || Instance != current.Instance)
            return false;

        if (PartyId != 0 && current.PartyId != 0)
            return PartyId == current.PartyId;

        return Members is not null && Members == current.Members;
    }
}
