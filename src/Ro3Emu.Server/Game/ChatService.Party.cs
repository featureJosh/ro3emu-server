using Romsg;
using Tencent.IM;

namespace Ro3Emu.Server.Game;

public sealed partial class ChatService
{
    /// <summary>
    /// One party-channel line from a companion bot. The client addresses party chat as "3_&lt;teamId&gt;" (EChannelTeam = 3,
    /// Lua_ChatDefine; LC_ChatHelper builds the id from the channel number and team id), optionally with the Tencent
    /// "@TGS#_" prefix like the map channel (inferred). Sent to the owner only, on the spelling the owner joined, else the
    /// plain one; no history is kept. A failed send is dropped.
    /// </summary>
    internal async Task PublishPartyLineAsync(GameSession owner, long bot, long teamId, string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 180 || owner.Role == null || friends.Blocks(owner.Role.Id, bot)) return;
        string group = $"3_{teamId}";
        lock (_sync)
            if (!owner.ChatGroups.Contains(group) && owner.ChatGroups.Contains($"@TGS#_{group}")) group = $"@TGS#_{group}";
        var message = new TextChatMessage
        {
            MessageId = Interlocked.Increment(ref _nextMessageId).ToString(), SenderId = bot.ToString(), ReceiveId = group,
            SendTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Content = content,
        };
        try { await DownAsync(owner, Cmd.NotifyNewText, new NotifyNewTextChatMessage { Msg = message }); }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException) { }
    }
}
