using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Tests;

public sealed class AccountPasswordTests
{
    [Theory]
    [InlineData(true, AccountStore.LoginResult.BadPassword, false)]
    [InlineData(false, AccountStore.LoginResult.Ok, true)]
    public async Task OtherPasswordOpensTheAccountOnlyWhenPasswordsAreOff(bool require, AccountStore.LoginResult second, bool verifies)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ro3-password-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = dir, RequirePasswords = require }));
            Assert.Equal(AccountStore.LoginResult.Registered, await accounts.LoginAsync("solo", "first"));
            Assert.Equal(second, await accounts.LoginAsync("solo", ""));
            Assert.Equal(verifies, await accounts.VerifyAsync("solo", "other"));
            Assert.False(await accounts.VerifyAsync("nobody", "x")); // still no account without registering
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
