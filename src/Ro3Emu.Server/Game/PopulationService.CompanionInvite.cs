using Google.Protobuf;
using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class PopulationService
{
    private static readonly string[] PartyGreetings = ["Hi! Happy to team up.", "Thanks for the invite!", "Ready when you are.", "Let's go!"];

    /// <summary>
    /// The "Team" slice of a bot's role info (romsg.GlobalCacheTeam, LC_DAL_RolePublic.lua:983-1005). The player card
    /// offers Team Up to a solo inviter only when the invitee's teamstatus equals TeamType.None
    /// (LC_PlayerOperationHelper.lua:896-910), so the zeros are written explicitly: an absent field decodes as nil.
    /// </summary>
    private static byte[] BotTeamInfo()
    {
        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        foreach (int field in new[] { 1, 2, 3 })
        {
            output.WriteTag(field, WireFormat.WireType.Varint);
            output.WriteInt64(0);
        }
        output.Flush();
        return stream.ToArray();
    }

    /// <summary>A normal invite admitted a bot: it says a short hello in party chat. Runs off the team scope; failures are swallowed.</summary>
    private void GreetParty(GameSession owner, long bot, long teamId)
    {
        if (chat == null) return;
        string text = PartyGreetings[random.Next(PartyGreetings.Length)];
        _ = Task.Run(async () =>
        {
            try { await chat.PublishPartyLineAsync(owner, bot, teamId, text); } catch (Exception) { /* a greeting is optional */ }
        });
    }
}
