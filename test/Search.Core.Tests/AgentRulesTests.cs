using System.Text.Json.Nodes;
using Search.Core;

namespace Search.Core.Tests;

/// Coding agents (AgentRules): the question's rules, where an agent may go, what it may press and wait on, and what
/// is taken out of the addresses it is given (AgentControlTests.swift's, without a page).
public class AgentRulesTests
{
    [Fact]
    public void NothingBeforeHelloAndNothingWhileAsking()
    {
        var consent = new AgentConsent();
        Assert.Equal(AgentVerdict.Proceed, consent.Verdict("hello"));
        Assert.Equal(AgentVerdict.Refuse("hello_first"), consent.Verdict("open"));
        Assert.True(consent.Hello());
        Assert.False(consent.Hello());
        Assert.Equal(AgentState.Asking, consent.State);
        foreach (var verb in new[] { "open", "tabs", "read", "click", "shot" }) Assert.Equal(AgentVerdict.Wait, consent.Verdict(verb));
        Assert.False(consent.May(AgentUse.Read, own: true));
    }

    [Fact]
    public void AllowedWithoutTabs()
    {
        var consent = new AgentConsent();
        consent.Hello();
        consent.Answer(allow: true, seesTabs: false);
        Assert.Equal(AgentVerdict.Proceed, consent.Verdict("open"));
        Assert.Equal(AgentVerdict.Refuse("said_hello"), consent.Verdict("hello"));
        Assert.True(consent.May(AgentUse.Read, own: true));
        Assert.True(consent.May(AgentUse.Drive, own: true));
        Assert.False(consent.May(AgentUse.Read, own: false));
        Assert.False(consent.May(AgentUse.Drive, own: false));
    }

    [Fact]
    public void SeeingYourTabsIsNeverUsingThem()
    {
        var consent = new AgentConsent();
        consent.Hello();
        consent.Answer(allow: true, seesTabs: true);
        Assert.True(consent.May(AgentUse.Read, own: false));
        Assert.False(consent.May(AgentUse.Drive, own: false));
    }

    [Fact]
    public void RefusedAndEndedAreFinal()
    {
        var refused = new AgentConsent();
        refused.Hello();
        refused.Answer(allow: false, seesTabs: true);
        Assert.Equal(AgentState.Refused, refused.State);
        Assert.False(refused.SeesTabs);
        Assert.Equal(AgentVerdict.Refuse("not_allowed"), refused.Verdict("open"));
        refused.Answer(allow: true, seesTabs: true);
        Assert.Equal(AgentState.Refused, refused.State);

        var ended = new AgentConsent();
        ended.Hello();
        ended.Answer(allow: true, seesTabs: true);
        ended.End();
        Assert.Equal(AgentVerdict.Refuse("stopped"), ended.Verdict("tabs"));
        Assert.False(ended.May(AgentUse.Read, own: true));
        ended.Answer(allow: true, seesTabs: true);
        Assert.Equal(AgentState.Ended, ended.State);

        var early = new AgentConsent();
        early.Answer(allow: true, seesTabs: false);
        Assert.Equal(AgentState.Connected, early.State);
    }

    [Theory]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("http://127.0.0.1:8080/", "http://127.0.0.1:8080/")]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("  example.com/x  ", "https://example.com/x")]
    public void OnlyTheWeb(string text, string url) => Assert.Equal(url, AgentRules.Address(text)?.AbsoluteUri);

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("search://settings")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("about:blank")]
    [InlineData("chrome-extension://abc/x")]
    [InlineData("edge://settings")]
    [InlineData("mailto:a@b.c")]
    [InlineData("tel:123")]
    [InlineData("ftp://example.com/")]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("https://")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("two words")]
    [InlineData("FILE:///tmp")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\x")]
    [InlineData("ms-settings:privacy")]
    public void NotTheRest(string? text) => Assert.Null(AgentRules.Address(text));

    [Theory]
    [InlineData("https://a.com", true)]
    [InlineData("http://a.com", true)]
    [InlineData("about:blank", true)]
    [InlineData("blob:https://a.com/x", true)]
    [InlineData("data:text/plain,x", true)]
    [InlineData("file:///C:/x", false)]
    [InlineData("search://settings", false)]
    [InlineData("mailto:a@b.c", false)]
    [InlineData("zoommtg://x", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("chrome-extension://abc/x", false)]
    public void WhereItsPagesMayGoByThemselves(string url, bool may) => Assert.Equal(may, AgentRules.MayLoad(new Uri(url)));

    [Fact]
    public void PrivateTabsAndTheBenchsAreNeverThere()
    {
        var consent = new AgentConsent();
        consent.Hello();
        consent.Answer(allow: true, seesTabs: true);
        var shy = new Tab { Private = true };
        var yours = new Tab();
        var script = new Tab { Bench = true };
        var mine = new Tab { Bench = true, Agent = true };
        Assert.Equal("stale_page", AgentRules.Refusal(shy, own: false, consent, AgentUse.Read));
        Assert.Equal("stale_page", AgentRules.Refusal(shy, own: true, consent, AgentUse.Read));
        Assert.Equal("stale_page", AgentRules.Refusal(script, own: false, consent, AgentUse.Read));
        Assert.Null(AgentRules.Refusal(yours, own: false, consent, AgentUse.Read));
        Assert.Equal("not_yours", AgentRules.Refusal(yours, own: false, consent, AgentUse.Drive));
        Assert.Null(AgentRules.Refusal(mine, own: true, consent, AgentUse.Drive));
    }

    [Fact]
    public void KeysNeverCommands()
    {
        Assert.Equal(13, AgentRules.Key("Enter")?.VirtualKey);
        Assert.Equal("\r", AgentRules.Key("enter")?.Text);
        Assert.Equal(AgentKey.Shift, AgentRules.Key("shift+Tab")?.Modifiers);
        Assert.Equal("a", AgentRules.Key("a")?.Text);
        Assert.Equal("KeyA", AgentRules.Key("a")?.Code);
        Assert.Equal("+", AgentRules.Key("shift++")?.Key);
        Assert.Equal(AgentKey.Ctrl, AgentRules.Key("ctrl+ArrowLeft")?.Modifiers);
        Assert.Equal(AgentKey.Alt | AgentKey.Shift, AgentRules.Key("alt+shift+x")?.Modifiers);
        Assert.Equal("é", AgentRules.Key("é")?.Text);
        Assert.Equal("Delete", AgentRules.Key("Delete")?.Key);
        foreach (var refused in new[] { "cmd+v", "cmd+a", "command+w", "meta+c", "win+r", "super+x", "shift+cmd+n", "", "ab", "hyper+x", "ctrl+k", "ctrl+y", "control+k",
                     "ctrl+shift+y", "ctrl+a", "ctrl+c", "ctrl+v", "ctrl+t", "ctrl+1", "ctrl++", "+", "shift+Delete", "Insert", "shift+Insert", "F5", "\n", "\u000b", "shift+\u0019" })
            Assert.Null(AgentRules.Key(refused));
    }

    [Fact]
    public void TypedCharactersAreKeysWhereAKeyboardHasThem()
    {
        Assert.Equal(new AgentKey("Z", "KeyZ", 'Z', "Z"), AgentKey.Of("Z"));
        Assert.Equal("Digit7", AgentKey.Of("&")?.Code);
        Assert.Equal("Enter", AgentKey.Of("\n")?.Key);
        Assert.Equal("Tab", AgentKey.Of("\t")?.Key);
        Assert.Equal("", AgentKey.Of("\t")?.Text);
        Assert.Null(AgentKey.Of("ß"));
        Assert.Null(AgentKey.Of("👍"));
    }

    [Fact]
    public void NoSelectorsAtAll()
    {
        foreach (var selector in new[] { "input[value^='a']", @"[va\lue^=""a""]", @"[v\61lue^=""a""]", "input:placeholder-shown", "input:invalid", "input:autofill", "#done" })
            Assert.Equal("bad_condition", Assert.Throws<AgentRefusal>(() => AgentRules.Condition(new JsonObject { ["selector"] = selector }, own: true)).Code);
        var own = AgentRules.Condition(new JsonObject { ["text"] = "Hello", ["role"] = "button", ["name"] = "Send" }, own: true);
        Assert.Equal(3, own.Page.Count);
        foreach (var key in new[] { "text", "role", "name" })
            Assert.Throws<AgentRefusal>(() => AgentRules.Condition(new JsonObject { [key] = "x" }, own: false));
        var yours = AgentRules.Condition(new JsonObject { ["url"] = "/done", ["title"] = "done" }, own: false);
        Assert.Empty(yours.Page);
        Assert.True(yours.Met(true, "https://a.test/done?pw=hunter2", "done"));
        // The address's secret is never something to wait on.
        Assert.False(AgentRules.Condition(new JsonObject { ["url"] = "pw=h" }, own: false).Met(true, "https://a.test/done?pw=hunter2", ""));
        Assert.True(AgentRules.Condition(new JsonObject { ["title"] = "Loading", ["gone"] = true }, own: false).Met(true, "", "Done"));
        Assert.True(AgentRules.Condition(new JsonObject(), own: false).None);
    }

    [Theory]
    [InlineData("https://a.test/done?name=Ada&pw=hunter2", "https://a.test/done?name=Ada&pw=redacted")]
    [InlineData("https://a.test/?password=x&otp=123456&code=9&token=t&q=cats", "https://a.test/?password=redacted&otp=redacted&code=redacted&token=redacted&q=cats")]
    [InlineData("https://a.test/cb#access_token=abc&state=1", "https://a.test/cb#access_token=redacted&state=1")]
    [InlineData("https://a.test/?q=pass", "https://a.test/?q=pass")]
    [InlineData("https://a.test/#top", "https://a.test/#top")]
    [InlineData("https://a.test/?pw=&pin", "https://a.test/?pw=&pin")]
    [InlineData("https://a.test/?p%61ss=x", "https://a.test/?p%61ss=redacted")]
    [InlineData("", "")]
    public void SecretsTakenOutOfAddresses(string url, string given) => Assert.Equal(given, AgentRules.Url(url));

    [Theory]
    [InlineData("mdp")]
    [InlineData("kennwort")]
    [InlineData("pw1")]
    [InlineData("pwd2")]
    [InlineData("contrasena")]
    [InlineData("senha")]
    [InlineData("haslo")]
    [InlineData("passwd")]
    public void SecretNamesInOtherWords(string name) => Assert.Equal($"https://a.test/?{name}=redacted", AgentRules.Url($"https://a.test/?{name}=s3cret"));

    [Fact]
    public void SecretsTakenOutOfWhatAPageSays() =>
        Assert.Equal("Couldn't open https://a.test/x?token=redacted: refused. Also http://b.test/?q=1", AgentRules.Scrubbed("Couldn't open https://a.test/x?token=abc: refused. Also http://b.test/?q=1"));

    [Fact]
    public void NoControlCharacters()
    {
        Assert.True(AgentRules.Typeable("Ada\tLovelace\nline two"));
        Assert.False(AgentRules.Typeable("\u000b"));
        Assert.False(AgentRules.Typeable("x\u0019"));
        Assert.False(AgentRules.Typeable("\u001b"));
        Assert.False(AgentRules.Typeable("a\u202eb"), "a format character turns text around");
    }

    [Fact]
    public void ClientNames()
    {
        Assert.Equal("Claude Code", AgentRules.ClientName("Claude Code"));
        Assert.Equal("Search wantsto", AgentRules.ClientName("“Search” wants\nto"));
        Assert.Equal("A program", AgentRules.ClientName(""));
        Assert.Equal("A program", AgentRules.ClientName(null));
        Assert.Equal(40, AgentRules.ClientName(new string('x', 200)).Length);
    }

    [Fact]
    public void Pace()
    {
        var pace = new AgentPace();
        for (var n = 0; n < 8; n++) Assert.Equal(0, pace.Delay(100));
        Assert.Equal(0.2, pace.Delay(100), 3);
        // Run as told: a second later, five more are free.
        var later = new AgentPace();
        for (var n = 0; n < 9; n++) later.Delay(0);
        Assert.Equal(0, later.Delay(1.2), 3);
    }

    [Fact]
    public void OneAtATimeAndNoneStraightAfterANo()
    {
        var gate = new AgentGate();
        var now = DateTimeOffset.UtcNow;
        Assert.Null(gate.Admit(busy: false, now));
        Assert.Equal("busy", gate.Admit(busy: true, now)?.Code);
        gate.Ended(AgentState.Allowed, stopped: false, now);
        Assert.Null(gate.Admit(busy: false, now));
        foreach (var (was, stopped) in new[] { (AgentState.Refused, false), (AgentState.Asking, false), (AgentState.Allowed, true) })
        {
            var g = new AgentGate();
            g.Ended(was, stopped, now);
            Assert.Equal("not_now", g.Admit(busy: false, now.AddSeconds(9))?.Code);
            Assert.Null(g.Admit(busy: false, now.AddSeconds(10)));
        }
        var quiet = new AgentGate();
        quiet.Ended(AgentState.Connected, stopped: false, now);
        Assert.Null(quiet.Admit(busy: false, now));
    }

    [Fact]
    public void IdleThenLetGo()
    {
        Assert.Equal((true, false), AgentRules.Quiet(working: false, TimeSpan.FromSeconds(3), AgentRules.Idle, AgentRules.LetGo));
        Assert.Equal((false, false), AgentRules.Quiet(working: false, TimeSpan.FromSeconds(9), AgentRules.Idle, AgentRules.LetGo));
        Assert.Equal((false, true), AgentRules.Quiet(working: false, TimeSpan.FromMinutes(5), AgentRules.Idle, AgentRules.LetGo));
        Assert.Equal((true, false), AgentRules.Quiet(working: true, TimeSpan.FromHours(1), AgentRules.Idle, AgentRules.LetGo));
    }

    [Fact]
    public void OnlyAFreshPointerClickAnswers()
    {
        var asked = DateTimeOffset.UtcNow;
        Assert.True(AgentRules.FreshClick(asked, asked.AddSeconds(1), pointer: true));
        Assert.False(AgentRules.FreshClick(asked, asked.AddSeconds(1), pointer: false), "a key, or UI Automation's invoke");
        Assert.False(AgentRules.FreshClick(asked, asked.AddSeconds(0.3), pointer: true), "in the question's first half second");
        Assert.False(AgentRules.FreshClick(asked, asked.AddSeconds(-3), pointer: true), "an earlier click");
    }

    [Fact]
    public void Handles()
    {
        Assert.Equal(("p1", 3, 12), AgentRules.Element("p1.s3.e12"));
        foreach (var bad in new[] { "", null, "p1.3.12", "p1.s3", "p1.s-1.e2", "p1.sx.e2", "p1.s3.e12.x" }) Assert.Null(AgentRules.Element(bad));
    }

    [Fact]
    public void Limits()
    {
        Assert.Equal(TimeSpan.FromSeconds(35), AgentRules.Patience(new JsonObject()));
        Assert.Equal(TimeSpan.FromSeconds(60), AgentRules.Patience(new JsonObject { ["timeout"] = 600 }));
        Assert.Equal(30, AgentRules.Seconds(new JsonObject { ["timeout"] = 90 }, 20));
        Assert.Equal(20, AgentRules.Seconds(new JsonObject { ["timeout"] = "x" }, 20));
        Assert.Equal(300, AgentRules.MostNodes(new JsonObject()));
        Assert.Equal(2000, AgentRules.MostNodes(new JsonObject { ["max_nodes"] = 1e9 }));
        Assert.Equal(0, AgentRules.LongestText(new JsonObject { ["max_text"] = -5 }));
        Assert.Equal(200, AgentRules.ShotWidth(new JsonObject { ["width"] = 10 }));
        Assert.Null(AgentRules.ShotWidth(new JsonObject()));
    }

    [Fact]
    public void ARefusalSaysItsCode()
    {
        var said = new AgentRefusal("busy", "Another agent", "Wait").Said;
        Assert.Equal("""{"error":{"code":"busy","message":"Another agent","hint":"Wait"}}""", said.ToJsonString());
        Assert.False(new AgentRefusal("x", "y").Said["error"]!.AsObject().ContainsKey("hint"));
    }
}
