using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Bounded bot body language through the client's own messages: the emote state machine on 31048
/// (behavior_state_machine, 14 Play / 13 Idle, as EmojiService drives player emotes) and 31014 ENTITY_FACCETOALL.
/// A remote unit entering straight into 14 is the late-viewer shape EmojiService.PopulateEntity already sends, so bots
/// skip the owner-only 17 Prepare handshake (inferred). Gestures are shared world state: one packet set per bot, never
/// per viewer. Nothing starts while a bot moves, routes or fights, and any of those ends a running gesture.</summary>
internal sealed class PopulationGestures(PopulationGestureOptions options, GameData data)
{
    internal sealed record Packet(PopulationUnit Bot, uint MsgId, IMessage Body);
    private sealed class Gesture { public int EmojiId; public long EndMs; public bool Sit; }
    private readonly Dictionary<PopulationUnit, Gesture> active = new();
    private readonly Dictionary<PopulationUnit, long> restRolled = new(), faceReady = new();
    private readonly Queue<(PopulationUnit Bot, GameSession Subject, bool Wave, long At)> replies = new();
    private readonly object speakerSync = new();
    private readonly Queue<(long Role, RuntimeMapKey Map, float X, float Z, long At)> speakers = new();
    private readonly Dictionary<long, long> speakerReady = new();
    private readonly Random random = new();
    internal int Active => active.Count;

    /// <summary>From ChatService after a player's nearby map line; one turn opportunity per speaker per ten seconds.</summary>
    public void NoteSpeaker(GameSession speaker, long now)
    {
        if (!options.Enabled || !options.FaceSpeakers || speaker.Role == null || speaker.SceneLoading || speaker.InstanceId != 0) return;
        lock (speakerSync)
        {
            if (speakerReady.GetValueOrDefault(speaker.Role.Id) > now || speakers.Count >= 32) return;
            if (speakerReady.Count >= 512)
                foreach (var key in speakerReady.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) speakerReady.Remove(key);
            if (speakerReady.Count >= 512) return;
            speakerReady[speaker.Role.Id] = now + 10_000;
            speakers.Enqueue((speaker.Role.Id, speaker.RuntimeMap, speaker.Position.X, speaker.Position.Z, now));
        }
    }

    /// <summary>A published reaction line: the bot faces its subject and occasionally waves after a greeting.</summary>
    public void Spoke(PopulationChatter.Line line, long now)
    {
        if (!options.Enabled || line.Subject == null || replies.Count >= 32) return;
        bool wave = line.Context == "greeting" && random.Next(100) < Math.Clamp(options.WaveChancePercent, 0, 100);
        replies.Enqueue((line.Bot, line.Subject, wave, now));
    }

    public List<Packet> Plan(IReadOnlyCollection<PopulationUnit> bots, long now)
    {
        var packets = new List<Packet>();
        foreach (var (bot, gesture) in active.ToArray())
        {
            if (bot.Removed || bot.Dead) { active.Remove(bot); bot.Gesture = null; continue; } // the entity leaves or is rebuilt
            if (!options.Enabled || !Idle(bot) || now >= gesture.EndMs || gesture.Sit && now >= bot.ActivityUntilMs - 250)
                End(bot, gesture, now, packets);
        }
        foreach (var bot in restRolled.Keys.Concat(faceReady.Keys).Where(b => b.Removed).Distinct().ToArray()) { restRolled.Remove(bot); faceReady.Remove(bot); }
        if (!options.Enabled) { replies.Clear(); lock (speakerSync) speakers.Clear(); return packets; }
        for (int i = 0; i < 4 && replies.TryDequeue(out var reply); i++)
        {
            var (bot, subject, wave, at) = reply;
            if (now - at > 3000 || !Idle(bot) || subject.Role == null || subject.RuntimeMap != bot.RuntimeMap || !PopulationReactions.Nearby(bot, subject)) continue;
            Turn(bot, subject.Position.X, subject.Position.Z, now, packets);
            if (wave && !active.ContainsKey(bot) && active.Count < 64 && Emote(options.WaveEmojiId, bot) is { } duration)
                Start(bot, options.WaveEmojiId, now + (duration > 0 ? Math.Min(duration, 6000) : 3000), false, now, packets);
        }
        (long Role, RuntimeMapKey Map, float X, float Z, long At)[] heard;
        lock (speakerSync) { heard = speakers.Take(4).ToArray(); foreach (var _ in heard) speakers.Dequeue(); }
        foreach (var speech in heard.Where(s => now - s.At <= 3000))
            foreach (var bot in bots.Where(b => b.RuntimeMap == speech.Map && Idle(b) && !active.ContainsKey(b) && faceReady.GetValueOrDefault(b) <= now
                    && Distance2(b, speech.X, speech.Z) is > 0.01f and <= 12 * 12).OrderBy(b => Distance2(b, speech.X, speech.Z)).ThenBy(b => b.Uid).Take(2).ToArray())
                Turn(bot, speech.X, speech.Z, now, packets);
        int sits = 0;
        if (options.SitEmojiId > 0)
            foreach (var bot in bots)
            {
                if (sits >= 4 || active.Count >= 64) break;
                if (bot.IdleChatContext != "rest" || bot.ActivityUntilMs - now < 3000 || !Idle(bot) || active.ContainsKey(bot)
                    || bot.Hp * 2 < bot.MaxHp || restRolled.GetValueOrDefault(bot) == bot.ActivityUntilMs) continue;
                restRolled[bot] = bot.ActivityUntilMs; // one roll per rest stop
                if (random.Next(100) >= Math.Clamp(options.SitChancePercent, 0, 100) || Emote(options.SitEmojiId, bot) is not { } duration) continue;
                Start(bot, options.SitEmojiId, Math.Min(bot.ActivityUntilMs - 250, duration > 0 ? now + duration : long.MaxValue), true, now, packets);
                sits++;
            }
        return packets;
    }

    /// <summary>A single (sort 1) emote the bot's sex may play on this map, with its duration (0 = loop); null unless the
    /// client data proves the row, so an unknown ID is never sent.</summary>
    internal long? Emote(int emojiId, PopulationUnit bot)
    {
        if (emojiId <= 0 || data.Row("PerformanceConfig", emojiId) is not { } row || GameData.Int(row, "_iSort") != 1) return null;
        int sex = (int)GameData.Int(row, "_iSex");
        if (sex != 0 && sex != bot.Gender
            || data.Row("MapConfig", bot.SceneEntry) is { } map && GameData.Int(map, "_iPerformanceForbidden") == 1) return null;
        return Math.Max(0, GameData.Int(row, "_iDuration"));
    }

    private static bool Idle(PopulationUnit bot) => !bot.Removed && !bot.Dead && !bot.Moving && bot.Target == null
        && bot.Route.Count == 0 && !bot.JourneyActive;

    private static float Distance2(PopulationUnit bot, float x, float z) => (bot.X - x) * (bot.X - x) + (bot.Z - z) * (bot.Z - z);

    private void Turn(PopulationUnit bot, float x, float z, long now, List<Packet> packets)
    {
        if (active.TryGetValue(bot, out var gesture) && gesture.Sit) return; // a seated model keeps its pose
        faceReady[bot] = now + 8000;
        bot.NextWalkMs = Math.Max(bot.NextWalkMs, now + 3000); // a short pause rather than walking straight off
        float face = MonsterService.Angle(bot.X, bot.Z, x, z);
        if (MathF.Abs((face - bot.Face + 540) % 360 - 180) < 10) return;
        bot.Face = face;
        packets.Add(new(bot, GameAoiSink.EntityFaceToAllId, new EntityFaceToAll { EntityID = (ulong)bot.Uid, Face = face }));
    }

    private void Start(PopulationUnit bot, int emojiId, long endMs, bool sit, long now, List<Packet> packets)
    {
        var info = new AoiBehaviorStateMachineInfo { CurState = ObjectState.EObjectStateSmemojiPlay, Param = Param(emojiId), StartStateTime = now };
        active[bot] = new() { EmojiId = emojiId, EndMs = endMs, Sit = sit };
        if (!sit) bot.NextWalkMs = Math.Max(bot.NextWalkMs, endMs); // finish the wave before walking on
        bot.Gesture = info;
        packets.Add(new(bot, CombatService.AoiAttrChange, new AOIAttrChange { EntityUuid = (ulong)bot.Uid, BehaviorStateMachine = info.Clone() }));
    }

    private void End(PopulationUnit bot, Gesture gesture, long now, List<Packet> packets)
    {
        active.Remove(bot); bot.Gesture = null;
        packets.Add(new(bot, CombatService.AoiAttrChange, new AOIAttrChange
        {
            EntityUuid = (ulong)bot.Uid, BehaviorStateMachine = new AoiBehaviorStateMachineInfo
            {
                CurState = ObjectState.EObjectStateSmidle, LastState = ObjectState.EObjectStateSmemojiPlay,
                StartStateTime = now, LastParam = Param(gesture.EmojiId),
            },
        }));
    }

    private static StateParam Param(int emojiId) => new() { EmojiPlay = new StateParamEmojiPlay { EmojiId = emojiId, PlayIndex = 1, IsReady = true } };
}
