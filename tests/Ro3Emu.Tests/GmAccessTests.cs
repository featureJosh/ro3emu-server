using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Who has GM (docs/guide/gm-access.md): an empty ServerOptions.GmAccounts means everyone for tests and local play, nobody
/// in game on a host started as Production on purpose; the loopback HTTP /ro3emu/gm keeps "empty = everyone".
/// </summary>
public sealed class GmAccessTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void AnExplicitProductionEnvironmentClosesAnEmptyList()
    {
        Assert.False(ServerHost.ResolveOptions(["--environment", "Production"]).GmOpenWhenEmpty);
        Assert.True(ServerHost.ResolveOptions(["--environment", "Development"]).GmOpenWhenEmpty);
        // An explicit option wins over the environment.
        Assert.True(ServerHost.ResolveOptions(["--environment", "Production", "--Ro3Emu:GmOpenWhenEmpty=true"]).GmOpenWhenEmpty);
        // No environment given (tests, tools/run-server.ps1): the host's implicit "Production" does not count.
        if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") is null && Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") is null)
            Assert.True(ServerHost.ResolveOptions([]).GmOpenWhenEmpty);
    }

    [Theory]
    // list, open-when-empty, account, loopback tool, expected
    [InlineData("", null, "anyone", false, true)]    // unset: everyone (tests, local play)
    [InlineData("", true, "anyone", false, true)]
    [InlineData("", false, "anyone", false, false)]  // Production: nobody in game
    [InlineData("", false, "anyone", true, true)]    // ...but the loopback tool is unchanged
    [InlineData("admin", false, "admin", false, true)]
    [InlineData("admin", false, "Admin", false, false)] // the stored spelling only: a case variant is not the GM
    [InlineData("admin", null, "tester", false, false)]
    [InlineData("admin", false, "tester", true, false)] // a list binds the tool as before
    public void TheAllowlistMatrix(string list, bool? openWhenEmpty, string account, bool loopbackTool, bool expected)
    {
        var options = new ServerOptions
        {
            GmAccounts = list.Length == 0 ? [] : list.Split(','),
            GmOpenWhenEmpty = openWhenEmpty,
        };
        Assert.Equal(expected, GmCommands.Allowed(options, account, loopbackTool));
    }

    [Theory]
    [InlineData("!bots stay", true)]
    [InlineData("!BOTS Follow", true)]
    [InlineData("!bots dismiss all", true)]
    [InlineData("!bots recruit", true)]
    [InlineData("!bots off", false)]
    [InlineData("!bots 50", false)]
    [InlineData("!bots stay now", false)]
    [InlineData("!bots", false)]
    [InlineData("!where", false)]
    public void OnlyPersonalCompanionCommandsSkipTheGmCheck(string line, bool expected) =>
        Assert.Equal(expected, GmCommands.IsPlayerCommand(line));

    [Fact]
    public async Task InProductionAnEmptyListRefusesChatGmButTheLoopbackToolStillRuns()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-gmprod-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create(["--environment", "Production"], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await app.StartAsync();
        try
        {
            string http = $"http://127.0.0.1:{httpPort}";
            await using var alice = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
            await alice.LoginAsync("alice", "pw");
            await alice.EnterGateAsync();
            await alice.EnterWorldAsync((await alice.CreateRoleAsync("Alice")).RoleBaseInfo.Id);
            await alice.JoinChatAsync("4_1");

            await alice.SendChatAsync("4_1", 2, "!where", channelIndex: 4);
            Assert.Contains("not enabled", await Marquee(alice, "not enabled"));
            // The client's GM panel asks whether to show its button: no.
            await alice.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { ParamString = "ro3-gm-panel-capability-v1:1" });
            Assert.EndsWith(":0", GeneralGMProcRsp.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.GeneralProcGm), Wait)).Body).ErrInfo);

            using var client = new HttpClient();
            var response = await client.PostAsync($"{http}/ro3emu/gm?account=alice", new StringContent("!where"));
            response.EnsureSuccessStatusCode();
            Assert.Contains("Scene", await Marquee(alice, "Scene"));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheLoginMarkerIsStrippedAndRecordedOnlyWhenTold()
    {
        var choices = new GmLoginChoices();
        Assert.Equal("offline", GmLoginChoices.Strip("offline+gm", out bool? gm));
        Assert.Equal(true, gm);
        Assert.False(choices.Declined("a")); // stripping alone records nothing
        Assert.Equal("offline", GmLoginChoices.Strip("offline+nogm", out gm));
        Assert.Equal(false, gm);
        choices.Record("a", gm);
        Assert.True(choices.Declined("A"));
        // No marker (old hooks, tests, bots): back to the plain policy, and the credential is untouched.
        Assert.Equal("pw", GmLoginChoices.Strip("pw", out gm));
        Assert.Null(gm);
        choices.Record("a", gm);
        Assert.False(choices.Declined("a"));
        Assert.False(choices.Declined(null));
    }

    [Fact]
    public async Task AWrongPasswordCannotChangeAnotherAccountsGmChoice()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-gmchoice-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.RequirePasswords = true;
        });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}") };
            async Task<string> Login(string credential) => await (await http.PostAsync("/sdk/login", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["identity"] = "admin", ["credential"] = credential }))).Content.ReadAsStringAsync();
            var choices = app.Services.GetRequiredService<GmLoginChoices>();

            Assert.Contains("\"code\":0", await Login("secret")); // registers the account
            Assert.False(choices.Declined("admin"));
            Assert.Contains("\"code\":401", await Login("offline+nogm")); // wrong password + marker: no choice recorded
            Assert.False(choices.Declined("admin"));
            choices.Record("admin", false); // the owner declined GM
            Assert.Contains("\"code\":401", await Login("offline+gm")); // wrong password + marker: nothing changes
            Assert.True(choices.Declined("admin"));
            Assert.Contains("\"code\":401", await Login("wrong"));
            Assert.True(choices.Declined("admin")); // a bad login with no marker does not clear it either
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    [Theory]
    // GmAccounts, marker, expected GM in game for the account "pilot"
    [InlineData("", "offline+gm", true)]          // open policy: ticked
    [InlineData("", "offline+nogm", false)]       // open policy: the box means something locally
    [InlineData("", "offline", true)]             // no marker: today's behaviour
    [InlineData("pilot", "offline+gm", true)]     // listed: ticked
    [InlineData("pilot", "offline+nogm", false)]
    [InlineData("pilot", "offline", true)]
    [InlineData("other", "offline+gm", false)]    // never widens a closed policy
    [InlineData("other", "offline+nogm", false)]
    [InlineData("other", "offline", false)]
    public async Task TheLoginAsGmBoxOnlyNarrowsThePolicy(string list, string marker, bool expected)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-gmbox-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.GmAccounts = list.Length == 0 ? [] : [list];
        });
        await app.StartAsync();
        try
        {
            await using var bot = new BotClient($"http://127.0.0.1:{httpPort}", File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
            await bot.LoginAsync("pilot", marker);
            await bot.EnterGateAsync();
            await bot.EnterWorldAsync((await bot.CreateRoleAsync("Pilot")).RoleBaseInfo.Id);
            await bot.JoinChatAsync("4_1");
            await bot.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { ParamString = "ro3-gm-panel-capability-v1:1" });
            string info = GeneralGMProcRsp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(GmCommands.GeneralProcGm), Wait)).Body).ErrInfo;
            Assert.EndsWith(expected ? ":1" : ":0", info);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    private static async Task<string> Marquee(BotClient bot, string? contains = null) =>
        BroadCastNotify.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify)
            && (contains == null || BroadCastNotify.Parser.ParseFrom(e.Body).Content.Contains(contains)), Wait)).Body).Content;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
