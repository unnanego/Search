using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Search.Core;

namespace Search.Engine;

// What a page says about its sign-ins that a browser has to know (Forms.swift, with Tab.swift's half of it): which box
// the caret is in and where, so the window can hang the accounts from it; when a sign-in has just been sent; and,
// once the page has moved on, whether it took, so the password can be offered a place in the vault. The page's half
// is Scripts/forms.js, in Search's isolated world.
//
// A password never passes through a message: the page script holds what a sign-in sent, and this asks for it (take)
// the moment it hears one went out, keeping it in memory here, for that tab alone, until the sign-in is judged or 45
// seconds pass. A sign-in that took is one whose next page, or whose page a moment later, has no password box left;
// one that still shows it was refused, and is never offered. Nothing is filled on its own: only Fill, on a pick.

/// The sign-in box the caret is in, in CSS px from the top-left of the page's viewport, with the page's
/// devicePixelRatio. `Passwords`: false for a box marked for passkeys alone. `NameOnly`: the name box of a sign-in
/// that asks for the password on its next page (a pick fills the name alone: FillName). `Wide`: clearly a sign-in's,
/// so the site's other hosts' accounts may come too; otherwise only this host's.
public sealed record FieldInfo(double X, double Y, double Width, double Height, double DevicePixelRatio, bool Passwords, bool NameOnly, bool Wide)
{
    /// The box in the web view's own units (DIPs): CSS px scaled by the page zoom, which is devicePixelRatio over the
    /// view's rasterization scale.
    public (double X, double Y, double Width, double Height) InDips(double rasterizationScale)
    {
        var k = DevicePixelRatio / (rasterizationScale > 0 ? rasterizationScale : 1);
        return (X * k, Y * k, Width * k, Height * k);
    }
}

/// A sign-in that worked: the site key it was sent from (lowercase, no www), the name and the password. `Clear`: the
/// page came over plain http. Its ToString leaves the password out.
public sealed record SignIn(string Host, string User, string Password, bool Clear)
{
    private bool PrintMembers(StringBuilder text) { text.Append($"Host = {Host}, User = {User}, Clear = {Clear}"); return true; }
}

public sealed class Forms
{
    private static readonly Lazy<string> _script = new(() =>
    {
        using var stream = typeof(Forms).Assembly.GetManifestResourceStream("Search.Engine.Scripts.forms.js") ?? throw new InvalidOperationException("forms.js isn't embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// The page script, for Pages.AddScript.
    public static string Script => _script.Value;

    /// How long a sign-in sent is held for a verdict.
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(45);
    /// How long the name a first step sent is kept for the password page after it.
    public static readonly TimeSpan NameHold = TimeSpan.FromMinutes(5);

    private const string TakeExpression = "globalThis.__searchForms ? __searchForms.take() : null";
    private const string HasPasswordExpression = "!!(globalThis.__searchForms && __searchForms.hasPassword())";

    private sealed class Sent
    {
        public required string Host, User, Password;
        public bool Clear;
        public DateTimeOffset At;
    }

    private sealed class State
    {
        public (string Site, string User, DateTimeOffset At)? Named;
        public Sent? Sent;
        public bool Field;
        public bool? Typing;
    }

    private readonly Func<Tab, string, Task<JsonNode?>> _evaluate;
    private readonly TimeProvider _time;
    private readonly ConditionalWeakTable<Tab, State> _states = new();

    /// The caret went into a sign-in box, or the box it is in moved (a scroll, a resize): the list hangs from it.
    public event Action<Tab, FieldInfo>? FieldEntered;
    /// The caret left the sign-in boxes: the list goes, a beat later (a click on it can take the caret first).
    public event Action<Tab>? FieldLeft;
    /// Whether the caret is somewhere on the page that takes typing, when that changes.
    public event Action<Tab, bool>? TypingChanged;
    /// The page has a sign-in on it.
    public event Action<Tab>? SignInFound;
    /// A sign-in that took: for Vault.Consider, and an offer to keep it if that says so. Never from a private tab.
    public event Action<Tab, SignIn>? SignedIn;

    /// A sign-in done in place (its boxes taken away without a new page) is judged this long after: such a form
    /// closes the instant you press the button and comes back if the server says no.
    public TimeSpan InPlaceWait { get; init; } = TimeSpan.FromSeconds(1.5);

    /// The window's: the script given to every page, and every page's messages heard.
    public Forms(Pages pages) : this(pages.Evaluate)
    {
        pages.AddScript(Script);
        pages.Message += Hear;
    }

    /// `evaluate` runs an expression in a tab's isolated world (Pages.Evaluate). For tests, the time too.
    public Forms(Func<Tab, string, Task<JsonNode?>> evaluate, TimeProvider? time = null)
    {
        _evaluate = evaluate;
        _time = time ?? TimeProvider.System;
    }

    private DateTimeOffset Now => _time.GetUtcNow();
    private State StateOf(Tab tab) => _states.GetOrCreateValue(tab);

    /// One of the page script's messages (Pages.Message). Anything not of forms.js is ignored.
    public void Hear(Tab tab, string kind, JsonElement body)
    {
        switch (kind)
        {
            case "forms.found": SignInFound?.Invoke(tab); break;
            case "forms.focus": Focus(tab, body); break;
            case "forms.sent": _ = Take(tab, body.TryGetProperty("held", out var held) && held.ValueKind == JsonValueKind.Object ? JsonNode.Parse(held.GetRawText()) as JsonObject : null); break;
            case "forms.name": Gave(tab, Text(body, "user")); break;
            case "forms.settled": _ = Settle(tab, navigated: false); break;
        }
    }

    /// A page finished loading in the tab (its main frame's NavigationCompleted): if a password went out recently and
    /// the new page has no box for one, that is a sign-in that took.
    public Task Arrived(Tab tab) => Settle(tab, navigated: true);

    /// The tab closed: whatever it held goes now rather than when it is collected.
    public void Closed(Tab tab) => _states.Remove(tab);

    /// A picked account into the sign-in on the tab's page, from the isolated world, as a person would have typed it:
    /// the name only into an empty name box, the password always. False when the sign-in boxes are gone, or the page
    /// is no longer the one in the tab's address (scheme and host, checked by the page itself as it runs).
    public async Task<bool> Fill(Tab tab, string username, string password) =>
        FillExpression(tab.Url, username, password) is { } expression && True(await _evaluate(tab, expression));

    /// A first step's name alone, picked from the accounts under its box.
    public async Task<bool> FillName(Tab tab, string username) => NameExpression(tab.Url, username) is { } expression && True(await _evaluate(tab, expression));

    /// The name the step before was sent with, if it was this site's and recent: the password page of a sign-in that
    /// asks for the name first has no box for it, and its account goes first in the list (Accounts.Offered's `given`).
    public string? GivenName(Tab tab) =>
        _states.TryGetValue(tab, out var state) && state.Named is { } named && Now - named.At < NameHold && Accounts.Host(tab.Url) is { } host && Registrable.Domain(host) == named.Site ? named.User : null;

    /// The values go in as JSON, never as source: `scheme` and `host` are the page's at the moment of the pick, which
    /// the page checks against its own as the script runs. Null for a tab with no website in it.
    public static string? FillExpression(Uri? page, string username, string password) => Call(page, "fill(a.user, a.password)", new() { ["user"] = username, ["password"] = password });
    public static string? NameExpression(Uri? page, string username) => Call(page, "name(a.user)", new() { ["user"] = username });

    private static string? Call(Uri? page, string call, Dictionary<string, string> values)
    {
        if (Accounts.Host(page) is null) return null;
        values["scheme"] = page!.Scheme + ":";
        values["host"] = page.IdnHost.ToLowerInvariant();
        return $"(() => {{ const a = {JsonSerializer.Serialize(values)}; if (location.protocol !== a.scheme || location.hostname.toLowerCase() !== a.host) return false; return !!(globalThis.__searchForms && __searchForms.{call}); }})()";
    }

    private void Focus(Tab tab, JsonElement body)
    {
        var state = StateOf(tab);
        if (body.TryGetProperty("typing", out var t) && t.ValueKind is JsonValueKind.True or JsonValueKind.False && state.Typing != t.GetBoolean())
        {
            state.Typing = t.GetBoolean();
            TypingChanged?.Invoke(tab, t.GetBoolean());
        }
        if (body.TryGetProperty("rect", out var r) && r.ValueKind == JsonValueKind.Object && Number(r, "x") is { } x && Number(r, "y") is { } y && Number(r, "w") is { } w && Number(r, "h") is { } h)
        {
            state.Field = true;
            FieldEntered?.Invoke(tab, new FieldInfo(x, y, w, h, Number(body, "dpr") ?? 1, Flag(body, "passwords", true), Flag(body, "name", false), Flag(body, "wide", true)));
        }
        else if (state.Field)
        {
            state.Field = false;
            FieldLeft?.Invoke(tab);
        }
    }

    /// A sign-in went out: what it held taken from the page now, while it is still the sign-in page, with the host as
    /// it is now (a moment later the tab may be somewhere else entirely, and that is not where the password belongs).
    /// A private tab's is never taken.
    private async Task Take(Tab tab, JsonObject? said = null)
    {
        if (tab.Private || Accounts.Host(tab.Url) is not { } host) return;
        var clear = tab.Url!.Scheme == Uri.UriSchemeHttp;
        var given = GivenName(tab);
        // Said with the message (the page may be gone by the time it could be asked), else asked for.
        var taken = said ?? await _evaluate(tab, TakeExpression) as JsonObject;
        if (said is not null) _ = _evaluate(tab, TakeExpression);
        if (taken is null || Text(taken["password"]) is not { Length: > 0 } password) return;
        // A password page with no name on it: the name is the one the step before was sent with.
        var user = Text(taken["user"]) is { Length: > 0 } typed ? typed : given ?? "";
        var state = StateOf(tab);
        var sent = state.Sent = new Sent { Host = host, User = user, Password = password, Clear = clear, At = Now };
        // Let go of once its time is up, judged or not: a password is held no longer than it can be used.
        await Task.Delay(Hold, _time);
        if (state.Sent == sent) state.Sent = null;
    }

    /// Kept in memory only, never written down, for this tab alone, a few minutes, and only for the same site.
    private void Gave(Tab tab, string? user)
    {
        if (user is not { Length: > 0 and <= 256 } || Accounts.Host(tab.Url) is not { } host) return;
        StateOf(tab).Named = (Registrable.Domain(host), user, Now);
    }

    /// A new document is judged at once; boxes the page took away by itself are given a moment first.
    private async Task Settle(Tab tab, bool navigated)
    {
        if (!_states.TryGetValue(tab, out var state) || state.Sent is not { } sent) return;
        if (Now - sent.At >= Hold) { state.Sent = null; return; }
        if (!navigated)
        {
            await Task.Delay(InPlaceWait, _time);
            // Only if nothing newer went out in the meantime.
            if (state.Sent == sent) await Settle(tab, navigated: true);
            return;
        }
        // The box still there is a refused sign-in, or the second step of one: kept a while longer, in case the page is
        // still on its way. A page that couldn't be asked isn't taken for one without the box.
        if (await _evaluate(tab, HasPasswordExpression) is not JsonValue still || !still.TryGetValue<bool>(out var has) || has) return;
        if (state.Sent is not { } now) return;
        state.Sent = null;
        state.Named = null;
        SignedIn?.Invoke(tab, new SignIn(now.Host, now.User, now.Password, now.Clear));
    }

    private static bool True(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var yes) && yes;
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string? Text(JsonElement body, string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Number(JsonElement body, string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static bool Flag(JsonElement body, string name, bool otherwise) => body.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : otherwise;
}
