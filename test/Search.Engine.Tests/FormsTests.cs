using System.Text.Json;
using System.Text.Json.Nodes;
using Search.Core;
using Search.Engine;

namespace Search.Engine.Tests;

public class FormsTests
{
    /// The isolated world, played: what each expression asked of it answers.
    private readonly List<string> _asked = [];
    private JsonNode? _taken = new JsonObject { ["user"] = "me", ["password"] = "pw" };
    private bool _hasPassword;
    private readonly Forms _forms;
    private readonly Tab _tab = new() { Url = new Uri("https://www.example.com/login") };

    public FormsTests() => _forms = new Forms(Evaluate) { InPlaceWait = TimeSpan.Zero };

    private Task<JsonNode?> Evaluate(Tab tab, string expression)
    {
        _asked.Add(expression);
        if (expression.Contains("take()")) return Task.FromResult(_taken?.DeepClone());
        if (expression.Contains("hasPassword()")) return Task.FromResult<JsonNode?>(JsonValue.Create(_hasPassword));
        return Task.FromResult<JsonNode?>(JsonValue.Create(true));
    }

    private void Hear(Tab tab, string json) { using var doc = JsonDocument.Parse(json); _forms.Hear(tab, doc.RootElement.GetProperty("kind").GetString()!, doc.RootElement.Clone()); }
    private static async Task Settle() { for (var i = 0; i < 20; i++) await Task.Yield(); await Task.Delay(30); }

    [Fact] public void ScriptIsEmbedded() => Assert.Contains("globalThis.__searchForms", Forms.Script);

    [Fact]
    public void FieldEnteredWithItsSpotThenLeftOnce()
    {
        var entered = new List<FieldInfo>();
        var left = 0;
        _forms.FieldEntered += (_, f) => entered.Add(f);
        _forms.FieldLeft += _ => left++;
        Hear(_tab, """{"kind":"forms.focus","typing":true,"rect":{"x":10,"y":20,"w":200,"h":30},"passwords":true,"name":false,"wide":false,"dpr":2}""");
        Assert.Equal(new FieldInfo(10, 20, 200, 30, 2, true, false, false), Assert.Single(entered));
        Assert.Equal((20.0, 40.0, 400.0, 60.0), entered[0].InDips(1));
        Hear(_tab, """{"kind":"forms.focus","typing":false,"rect":null}""");
        Hear(_tab, """{"kind":"forms.focus","typing":false,"rect":null}""");
        Assert.Equal(1, left);
    }

    [Fact]
    public void TypingSaidOnlyWhenItChanges()
    {
        var said = new List<bool>();
        _forms.TypingChanged += (_, t) => said.Add(t);
        foreach (var t in new[] { true, true, false }) Hear(_tab, $$"""{"kind":"forms.focus","typing":{{(t ? "true" : "false")}},"rect":null}""");
        Assert.Equal([true, false], said);
    }

    [Fact]
    public async Task ASignInThatTookIsOfferedWithTheHostItWasSentFrom()
    {
        SignIn? heard = null;
        _forms.SignedIn += (_, s) => heard = s;
        Hear(_tab, """{"kind":"forms.sent"}""");
        await Settle();
        _tab.Url = new Uri("https://dashboard.example.net/");
        await _forms.Arrived(_tab);
        Assert.Equal(new SignIn("example.com", "me", "pw", false), heard);
        Assert.DoesNotContain("pw", heard!.ToString());
    }

    [Fact]
    public async Task ARefusedSignInIsNotOffered()
    {
        var heard = 0;
        _forms.SignedIn += (_, _) => heard++;
        _hasPassword = true;
        Hear(_tab, """{"kind":"forms.sent"}""");
        await Settle();
        await _forms.Arrived(_tab);
        Assert.Equal(0, heard);
        // Still held: the page may still be on its way.
        _hasPassword = false;
        await _forms.Arrived(_tab);
        Assert.Equal(1, heard);
        await _forms.Arrived(_tab);
        Assert.Equal(1, heard);
    }

    [Fact]
    public async Task APageThatCouldntBeAskedIsNotTakenForASignIn()
    {
        var forms = new Forms((_, e) => Task.FromResult<JsonNode?>(e.Contains("take()") ? new JsonObject { ["user"] = "me", ["password"] = "pw" } : null));
        var heard = 0;
        forms.SignedIn += (_, _) => heard++;
        using var doc = JsonDocument.Parse("""{"kind":"forms.sent"}""");
        forms.Hear(_tab, "forms.sent", doc.RootElement);
        await Settle();
        await forms.Arrived(_tab);
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task APrivateTabsSignInIsNeverTaken()
    {
        var tab = new Tab { Private = true, Url = new Uri("https://example.com/") };
        Hear(tab, """{"kind":"forms.sent"}""");
        await Settle();
        Assert.DoesNotContain(_asked, e => e.Contains("take()"));
    }

    [Fact]
    public async Task TheFirstStepsNameGoesWithAPasswordPageThatHasNone()
    {
        SignIn? heard = null;
        _forms.SignedIn += (_, s) => heard = s;
        Hear(_tab, """{"kind":"forms.name","user":"me@mail.example"}""");
        Assert.Equal("me@mail.example", _forms.GivenName(_tab));
        _tab.Url = new Uri("https://example.com/password");
        _taken = new JsonObject { ["user"] = "", ["password"] = "pw" };
        Hear(_tab, """{"kind":"forms.sent"}""");
        await Settle();
        await _forms.Arrived(_tab);
        Assert.Equal("me@mail.example", heard?.User);
        Assert.Null(_forms.GivenName(_tab));
    }

    [Fact]
    public void AnotherSitesPageNeverSeesTheName()
    {
        Hear(_tab, """{"kind":"forms.name","user":"me"}""");
        _tab.Url = new Uri("https://other.com/");
        Assert.Null(_forms.GivenName(_tab));
    }

    [Fact]
    public async Task ASignInDoneInPlaceIsJudgedAfterAMoment()
    {
        SignIn? heard = null;
        _forms.SignedIn += (_, s) => heard = s;
        Hear(_tab, """{"kind":"forms.sent"}""");
        await Settle();
        Hear(_tab, """{"kind":"forms.settled"}""");
        await Settle();
        Assert.Equal("pw", heard?.Password);
    }

    [Fact]
    public async Task PlainHttpIsMarkedClear()
    {
        SignIn? heard = null;
        _forms.SignedIn += (_, s) => heard = s;
        var tab = new Tab { Url = new Uri("http://router.example.com/") };
        Hear(tab, """{"kind":"forms.sent"}""");
        await Settle();
        await _forms.Arrived(tab);
        Assert.True(heard?.Clear);
    }

    [Fact]
    public async Task FillHandsTheValuesOverAsJsonAndChecksThePage()
    {
        Assert.True(await _forms.Fill(_tab, "me", "a\"b'</script>\u2028c"));
        var expression = _asked.Single();
        Assert.Contains("location.hostname.toLowerCase() !== a.host", expression);
        Assert.Contains("\"host\":\"www.example.com\"", expression);
        Assert.Contains("\"scheme\":\"https:\"", expression);
        Assert.DoesNotContain("</script>", expression);
        Assert.DoesNotContain("\u2028", expression);
        var literal = expression[(expression.IndexOf("const a = ", StringComparison.Ordinal) + 10)..expression.IndexOf("; if (", StringComparison.Ordinal)];
        Assert.Equal("a\"b'</script>\u2028c", JsonNode.Parse(literal)!["password"]!.GetValue<string>());
    }

    [Fact]
    public async Task NothingIsFilledIntoAPageThatIsntAWebsite()
    {
        Assert.False(await _forms.Fill(new Tab { Url = new Uri("about:blank") }, "me", "pw"));
        Assert.Empty(_asked);
    }

    [Fact] public async Task FillNameAsksForTheNameAlone() { Assert.True(await _forms.FillName(_tab, "me")); Assert.Contains("__searchForms.name(a.user)", _asked.Single()); Assert.DoesNotContain("password", _asked.Single()); }
}
