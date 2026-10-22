using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>A companion's live roster projection, supplied by PopulationService.</summary>
public sealed record TeamCompanionCard(string Name, int Job, int Level, int Gender, int MapId, int LineId,
    long Hp, long MaxHp, long Sp, long MaxSp, Vec3 Pos, bool Dead, bool Present);

/// <summary>
/// Temporary population companions as ordinary roster members (42026/42048/42050/42051/42037), with no account,
/// session, saved role or teammate history. A companion stays in a party only while its owner does: the owner's
/// departure, kick or the last human leaving removes it here, and PopulationService is told to despawn it.
/// Companions never lead, are excluded from dungeon/match admission rosters and wait outside private content (inferred).
/// </summary>
public sealed partial class TeamService
{
    private sealed record CompanionMember(long Owner, TeamCompanionCard? Last);
    private readonly Dictionary<long, CompanionMember> _companions = new();
    internal Func<long, TeamCompanionCard?>? CompanionCard { get; set; }
    /// <summary>Called on the loop after the roster drops a companion; must not block (PopulationService queues it).</summary>
    internal Action<long>? CompanionDropped { get; set; }
    /// <summary>Normal-invite admission of an ambient bot (auto-accept when eligible); returns an error code or 0.</summary>
    internal Func<GameSession, long, Task<int>>? CompanionClaim { get; set; }

    internal static bool IsCompanion(long role) => PopulationService.IsUid(role);
    internal long CompanionOwner(long bot) => _companions.TryGetValue(bot, out var c) ? c.Owner : 0;
    private IEnumerable<long> Humans(Team team) => team.Members.Where(m => !IsCompanion(m));
    private long[] OwnedCompanions(Team team, long owner) =>
        team.Members.Where(m => IsCompanion(m) && CompanionOwner(m) == owner).ToArray();

    /// <summary>Join a companion to its owner's party, creating one through the normal create path when solo.</summary>
    internal Task<int> AddCompanionAsync(GameSession owner, long bot) =>
        RunTeamAsync("team/companion-join", [owner.Role!.Id, bot], () => AddCompanionCoreAsync(owner, bot));

    private async Task<int> AddCompanionCoreAsync(GameSession owner, long bot)
    {
        long self = owner.Role!.Id;
        if (!IsCompanion(bot) || _byMember.ContainsKey(bot)) return ErrorCodes.InviteeInParty;
        if (!_byMember.ContainsKey(self)) await CreateCoreAsync(owner, new TeamCreateReq { TeamType = TeamType.Five });
        if (!_byMember.TryGetValue(self, out var team)) return ErrorCodes.NotInParty;
        if (team.Members.Count >= Capacity(team)) return ErrorCodes.PartyFull;
        await AdmitCompanionAsync(team, self, bot);
        return 0;
    }

    private async Task AdmitCompanionAsync(Team team, long owner, long bot)
    {
        Join(team, bot); // no RecordTeammatesAsync: an ephemeral actor is never a durable recent teammate
        _companions[bot] = new(owner, CompanionCard?.Invoke(bot));
        _log.LogInformation("Companion {Bot} joins party {Team} for {Owner}", bot, team.Id, owner);
        await PublishAdmissionAsync(team, bot);
    }

    internal Task RemoveCompanionAsync(long bot) => RunTeamAsync("team/companion-leave", [bot], 0, async () =>
    {
        if (_byMember.TryGetValue(bot, out var team)) await RemoveAsync(team, bot, leaveType: 0);
        else _companions.Remove(bot);
    });

    /// <summary>Called on the loop after a normal invite admitted a companion; must not block (PopulationService fires its greeting).</summary>
    internal Action<GameSession, long, long>? CompanionInvited { get; set; }

    /// <summary>
    /// 42004 aimed at a population uid: the bot answers at once instead of receiving 42005. The stock client creates the
    /// party first when solo (LC_System_Team._onAddCreateAndInvite sends SendCreateTeam, then SendInviteIntoTeam on the
    /// create callback); a solo inviter is also given a party here, as AddCompanionCoreAsync does (inferred, for other callers).
    /// </summary>
    private async Task InviteCompanionCoreAsync(GameSession s, long bot)
    {
        long self = s.Role!.Id;
        _byMember.TryGetValue(self, out var team);
        int code = _byMember.ContainsKey(bot) ? ErrorCodes.InviteeInParty
            : team != null && team.Members.Count >= Capacity(team) ? ErrorCodes.PartyFull
            : CompanionClaim == null ? ErrorCodes.PlayerOffline : await CompanionClaim(s, bot);
        // The team reservation covers owner, bot and party: the claim's await cannot admit another member here.
        if (code == 0 && team == null)
        {
            await CreateCoreAsync(s, new TeamCreateReq { TeamType = TeamType.Five });
            if (!_byMember.TryGetValue(self, out team)) { code = ErrorCodes.NotInParty; ForgetCompanion(bot); }
        }
        await s.SendGameAsync(TeamInvite, new InviteIntoTeamResp { Result = code });
        if (code != 0) return;
        await AdmitCompanionAsync(team!, self, bot);
        CompanionInvited?.Invoke(s, bot, team!.Id);
    }

    private void ForgetCompanion(long role)
    {
        if (!IsCompanion(role)) return;
        _companions.Remove(role);
        CompanionDropped?.Invoke(role);
    }

    /// <summary>Companions never keep a party open without a human member.</summary>
    private void DissolveCompanions(Team team)
    {
        foreach (long bot in team.Members.ToArray())
        {
            team.Members.Remove(bot); _byMember.Remove(bot);
            ForgetCompanion(bot);
        }
    }

    private TeamMemberDetailInfo CompanionDetail(Team team, long bot)
    {
        var known = _companions.GetValueOrDefault(bot);
        var card = CompanionCard?.Invoke(bot) ?? known?.Last;
        if (known != null && card != null && !ReferenceEquals(card, known.Last)) _companions[bot] = known with { Last = card };
        return new TeamMemberDetailInfo
        {
            Guid = bot, Job = card?.Job ?? 1000, Level = card?.Level ?? 1, Name = card?.Name ?? "", Gender = card?.Gender ?? 1,
            GuildName = "", MapId = card?.MapId ?? 0, LineId = card?.LineId ?? 1,
            Title = team.ViceLeaders.Contains(bot) ? TeamTitleType.ViceLeader : TeamTitleType.None,
            TeamIndex = team.Members.IndexOf(bot), JoinTeamTime = team.Joined.GetValueOrDefault(bot),
            // A companion waiting outside private content or for its owner's reconnect shows as away (inferred).
            OnlineState = card is { Present: true } ? TeamMemberOnlineState.None : TeamMemberOnlineState.Faraway,
            ChatState = team.ChatStates.GetValueOrDefault(bot), IsForbiddenChat = team.Muted.Contains(bot),
            BattleInfo = new TeamMemberBattleInfo
            {
                MaxHpValue = Math.Max(1, card?.MaxHp ?? 1), MaxMpValue = Math.Max(1, card?.MaxSp ?? 1),
                HpPercent = (int)Math.Clamp(card?.Hp ?? 1, 0, int.MaxValue), MpPercent = (int)Math.Clamp(card?.Sp ?? 1, 0, int.MaxValue),
                Pos = card?.Pos ?? new Vec3(),
                PlayerLifeStatus = card?.Dead == true ? EPlayerLifeStatus.Dead : EPlayerLifeStatus.Alive,
            },
        };
    }
}
