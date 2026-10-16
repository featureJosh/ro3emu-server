using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Real-event observation and body language. Callbacks only record bounded IDs; the population pass and the
/// shared chat planner decide whether one bot reacts once.</summary>
public sealed partial class PopulationService
{
    private PopulationGestures? gestures;
    private readonly Dictionary<int, bool> bossTemplates = new();
    private long nextBossScanMs;
    private PopulationReactions Reactions => chatter.Reactions;
    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void InitReactions()
    {
        gestures = new(options.Gestures, data);
        if (combat != null)
        {
            combat.Dealt.Add((player, monster, damage) => { if (damage > 0) Reactions.RecordPlayerHit(player, monster, NowMs); });
            combat.PopulationHit = (bot, monster) => Reactions.RecordBotHit(bot, monster, NowMs);
        }
        chat?.LocalChatSent.Add(speaker => gestures.NoteSpeaker(speaker, NowMs));
    }

    /// <summary>For the owner of a friendly effect: a real player's heal/buff (revive=false) or resurrection landed on a bot.
    /// Nothing calls this yet. CombatService.ValidateTarget and ApplyEffectsAsync resolve friendly and Resurrection targets only
    /// among GameSessions, so a bot is not a valid heal, buff or revive target today; bot-aware targeting should call this
    /// once per landed effect.</summary>
    internal void NotePlayerSupport(PopulationUnit bot, GameSession player, bool revive)
    {
        if (bot.Uid >= UidBase && !bot.Removed) Reactions.RecordSupport(bot, player, revive, NowMs);
    }

    private async Task ReactionsPassAsync(long now)
    {
        if (chat != null && options.Chat.BossReactionsEnabled && now >= nextBossScanMs)
        {
            nextBossScanMs = now + 5000; // bounded: one scan of occupied maps every five seconds, at most 16 bosses each
            foreach (long scene in bots.Select(b => b.WorldScene).Distinct().ToArray())
                foreach (var boss in world.Map(scene).Units.OfType<MonsterUnit>()
                    .Where(m => !m.Dead && m.Hp > 0 && m.InstanceId == 0 && IsBoss(m.Template.NpcId)).Take(16).ToArray())
                    Reactions.ObserveBoss(boss, now);
        }
        if (gestures == null) return;
        foreach (var packet in gestures.Plan(bots, now)) await world.ToUnitViewersAsync(packet.Bot, packet.MsgId, packet.Body);
    }

    internal bool IsBoss(int npcId)
    {
        if (!bossTemplates.TryGetValue(npcId, out bool boss))
        {
            // NPCConfig._iNPCSubType 3/4 are the field boss/MVP rows WildAfkService.MvpAggro and WorldEffectsService.ShoutAsync
            // treat as bosses; MvpService spawns subtype 4 (which value is "field boss" versus "MVP" is inferred).
            boss = data.Row("NPCConfig", npcId) is { } npc && GameData.Int(npc, "_iNPCSubType") is 3 or 4;
            if (bossTemplates.Count < 4096) bossTemplates[npcId] = boss;
        }
        return boss;
    }

    private void NoteSpoken(PopulationChatter.Line line, long now) => gestures?.Spoke(line, now);
}
