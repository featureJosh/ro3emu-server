using System.Collections.Concurrent;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The "Login as GM" tick box (client hook LoginGmBox) as the SDK login saw it, per account: the hidden password value
/// "offline+gm" / "offline+nogm". An account that never sent the marker (old hooks, tests, bots) has no entry and keeps
/// the plain GM policy. The box can only narrow that policy: <see cref="GmCommands.Allowed(GameSession)"/>.
/// The choice is recorded only after a successful login, so a wrong password cannot change another account's choice.
/// </summary>
public sealed class GmLoginChoices
{
    private const string Plain = "offline";
    private readonly ConcurrentDictionary<string, bool> _wantsGm = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Strips the marker off the credential (null choice: none sent); any other credential passes through.
    /// Nothing is recorded here: call <see cref="Record"/> once the login has succeeded.</summary>
    public static string Strip(string credential, out bool? wantsGm)
    {
        wantsGm = credential == Plain + "+gm" ? true : credential == Plain + "+nogm" ? false : null;
        return wantsGm == null ? credential : Plain;
    }

    /// <summary>Remembers the choice of an authenticated login; no marker clears the account's entry.</summary>
    public void Record(string account, bool? wantsGm)
    {
        if (wantsGm is bool wants) _wantsGm[account] = wants;
        else _wantsGm.TryRemove(account, out _);
    }

    /// <summary>False only when the client sent the marker unticked.</summary>
    public bool Declined(string? account) => account != null && _wantsGm.TryGetValue(account, out bool wants) && !wants;
}
