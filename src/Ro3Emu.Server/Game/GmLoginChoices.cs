using System.Collections.Concurrent;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The "Login as GM" tick box (client hook LoginGmBox) as the SDK login saw it, per account: the hidden password value
/// "offline+gm" / "offline+nogm". An account that never sent the marker (old hooks, tests, bots) has no entry and keeps
/// the plain GM policy. The box can only narrow that policy: <see cref="GmCommands.Allowed(GameSession)"/>.
/// </summary>
public sealed class GmLoginChoices
{
    private const string Plain = "offline";
    private readonly ConcurrentDictionary<string, bool> _wantsGm = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Strips the marker off the credential and records the choice; any other credential passes through.</summary>
    public string Take(string account, string credential)
    {
        if (credential == Plain + "+gm") { _wantsGm[account] = true; return Plain; }
        if (credential == Plain + "+nogm") { _wantsGm[account] = false; return Plain; }
        _wantsGm.TryRemove(account, out _);
        return credential;
    }

    /// <summary>False only when the client sent the marker unticked.</summary>
    public bool Declined(string? account) => account != null && _wantsGm.TryGetValue(account, out bool wants) && !wants;
}
