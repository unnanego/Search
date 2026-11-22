using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Search.Core;

// A coding agent on this PC (Claude Code) using the window you already have open, when you let it (AgentControl.swift):
// the rules that need no pipe and no window, so they can be tested on their own. The pipe, the session and the verbs
// are the engine's (AgentServer.cs); the question, Stop and the working mark are the window's.
//
// Each connection asks first: its first line says who it is, and nothing else it sends is looked at until you answer.
// One agent at a time; after Don't Allow, Stop, or a question left unanswered, nobody asks again for a few seconds. It
// works in tabs of its own, opened at the end of your row, never selected, never in your session. Your own tabs it can
// list and read only if you ticked "Let it see my open tabs", and never touches; private tabs it never sees. Secret
// values are taken out of every address it is given, and no script or CSS of its own runs in a page.

public enum AgentState { Connected, Asking, Allowed, Refused, Ended }

public enum AgentUse { Read, Drive }

/// What a request may do now: go, wait for your answer, or be refused with a code.
public readonly record struct AgentVerdict(bool Go, string? Refused)
{
    public static readonly AgentVerdict Proceed = new(true, null), Wait = new(false, null);
    public static AgentVerdict Refuse(string code) => new(false, code);
}

/// Where one connection stands with you.
public sealed class AgentConsent
{
    public AgentState State { get; private set; } = AgentState.Connected;
    /// "Let it see my open tabs", ticked when it was allowed.
    public bool SeesTabs { get; private set; }

    /// It said who it is: the question goes up. Once.
    public bool Hello() { if (State != AgentState.Connected) return false; State = AgentState.Asking; return true; }

    /// Your answer. Only to a question that is up.
    public void Answer(bool allow, bool seesTabs)
    {
        if (State != AgentState.Asking) return;
        State = allow ? AgentState.Allowed : AgentState.Refused;
        SeesTabs = allow && seesTabs;
    }

    public void End() { State = AgentState.Ended; SeesTabs = false; }

    public AgentVerdict Verdict(string verb) => State switch
    {
        AgentState.Connected => verb == "hello" ? AgentVerdict.Proceed : AgentVerdict.Refuse("hello_first"),
        AgentState.Asking => AgentVerdict.Wait,
        AgentState.Allowed => verb == "hello" ? AgentVerdict.Refuse("said_hello") : AgentVerdict.Proceed,
        AgentState.Refused => AgentVerdict.Refuse("not_allowed"),
        _ => AgentVerdict.Refuse("stopped")
    };

    /// A tab of its own it may read and drive; one of yours only read, and only if you said so.
    public bool May(AgentUse use, bool own) => State == AgentState.Allowed && (own || use == AgentUse.Read && SeesTabs);
}

/// A refusal: a code an agent can branch on, what happened, what to do.
public sealed class AgentRefusal(string code, string message, string hint = "") : Exception(message)
{
    public string Code { get; } = code;
    public string Hint { get; } = hint;

    public JsonObject Said
    {
        get
        {
            var error = new JsonObject { ["code"] = Code, ["message"] = Message };
            if (Hint.Length > 0) error["hint"] = Hint;
            return new JsonObject { ["error"] = error };
        }
    }

    public static AgentRefusal Stopped => new("stopped", "Stopped in Search");
}

/// A key as the DevTools protocol takes it: DOM key and code, the Windows virtual key, the text it types (empty for
/// none), and the modifiers (alt 1, ctrl 2, meta 4, shift 8).
public sealed record AgentKey(string Key, string Code, int VirtualKey, string Text, int Modifiers = 0)
{
    public const int Alt = 1, Ctrl = 2, Shift = 8;

    private static readonly Dictionary<char, (string Code, int Vk)> _punctuation = new()
    {
        ['-'] = ("Minus", 189), ['='] = ("Equal", 187), ['['] = ("BracketLeft", 219), [']'] = ("BracketRight", 221), ['\\'] = ("Backslash", 220),
        [';'] = ("Semicolon", 186), ['\''] = ("Quote", 222), [','] = ("Comma", 188), ['.'] = ("Period", 190), ['/'] = ("Slash", 191), ['`'] = ("Backquote", 192),
        ['_'] = ("Minus", 189), ['+'] = ("Equal", 187), ['{'] = ("BracketLeft", 219), ['}'] = ("BracketRight", 221), ['|'] = ("Backslash", 220),
        [':'] = ("Semicolon", 186), ['"'] = ("Quote", 222), ['<'] = ("Comma", 188), ['>'] = ("Period", 190), ['?'] = ("Slash", 191), ['~'] = ("Backquote", 192),
        ['!'] = ("Digit1", 49), ['@'] = ("Digit2", 50), ['#'] = ("Digit3", 51), ['$'] = ("Digit4", 52), ['%'] = ("Digit5", 53), ['^'] = ("Digit6", 54),
        ['&'] = ("Digit7", 55), ['*'] = ("Digit8", 56), ['('] = ("Digit9", 57), [')'] = ("Digit0", 48)
    };

    /// One character as a key of a US keyboard would type it, or null for one no key there types (it is inserted as
    /// text instead). Tab and newline are their own keys.
    public static AgentKey? Of(string character)
    {
        if (character is "\n" or "\r" or "\r\n") return Named["enter"];
        if (character == "\t") return Named["tab"];
        if (character.Length != 1) return null;
        var c = character[0];
        if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') return new(character, "Key" + char.ToUpperInvariant(c), char.ToUpperInvariant(c), character);
        if (c is >= '0' and <= '9') return new(character, "Digit" + c, c, character);
        if (c == ' ') return Named["space"];
        return _punctuation.TryGetValue(c, out var key) ? new(character, key.Code, key.Vk, character) : null;
    }

    /// The keys a name says, as press takes them.
    public static readonly IReadOnlyDictionary<string, AgentKey> Named = new Dictionary<string, AgentKey>
    {
        ["enter"] = new("Enter", "Enter", 13, "\r"), ["return"] = new("Enter", "Enter", 13, "\r"), ["tab"] = new("Tab", "Tab", 9, ""),
        ["escape"] = new("Escape", "Escape", 27, ""), ["esc"] = new("Escape", "Escape", 27, ""), ["backspace"] = new("Backspace", "Backspace", 8, ""),
        ["delete"] = new("Delete", "Delete", 46, ""), ["arrowup"] = new("ArrowUp", "ArrowUp", 38, ""), ["arrowdown"] = new("ArrowDown", "ArrowDown", 40, ""),
        ["arrowleft"] = new("ArrowLeft", "ArrowLeft", 37, ""), ["arrowright"] = new("ArrowRight", "ArrowRight", 39, ""), ["home"] = new("Home", "Home", 36, ""),
        ["end"] = new("End", "End", 35, ""), ["pageup"] = new("PageUp", "PageUp", 33, ""), ["pagedown"] = new("PageDown", "PageDown", 34, ""),
        ["space"] = new(" ", "Space", 32, " ")
    };
}

/// What wait_for waits on: text in the page, a control by role and name, the address or the title. The address and
/// title are checked here, against the address as the agent is given it, never in the page.
public sealed class AgentCondition
{
    /// For the page's check: text, role, name.
    public JsonObject Page { get; } = new();
    public string? Url { get; init; }
    public string? Title { get; init; }
    public bool Gone { get; init; }
    public bool None => Page.Count == 0 && Url is null && Title is null;

    public bool Met(bool page, string url, string title)
    {
        var all = page;
        if (Url is not null) all = all && AgentRules.Url(url).Contains(Url, StringComparison.Ordinal);
        if (Title is not null) all = all && title.Contains(Title, StringComparison.Ordinal);
        return Gone ? !all : all;
    }
}

/// How fast an agent's requests are taken: a few at once, then about five a second, a quick person's speed.
public sealed class AgentPace(double burst = 8, double perSecond = 5)
{
    private double? _tokens, _last;

    /// How long the next request waits, in seconds, asked at `now` (seconds on any steady clock).
    public double Delay(double now)
    {
        var have = _tokens ?? burst;
        if (_last is { } last) have = Math.Min(burst, have + Math.Max(0, now - last) * perSecond);
        _last = now;
        _tokens = --have;
        return have >= 0 ? 0 : -have / perSecond;
    }
}

/// One agent at a time, and none straight after a no: who is turned away at the door, and for how long.
public sealed class AgentGate
{
    /// After Don't Allow, Stop or a question walked away from, no new question for this long (shorter in a test run).
    public TimeSpan Quiet { get; set; } = TimeSpan.FromSeconds(10);
    private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;

    public AgentRefusal? Admit(bool busy, DateTimeOffset now) =>
        busy ? new("busy", "Another agent is using Search", "Wait for it to finish.")
        : now < _quietUntil ? new("not_now", "Search was just told no, or stopped", "Ask again in a few seconds, once.") : null;

    /// A session ended: a question you said no to or walked away from, or an agent you stopped, doesn't come straight back.
    public void Ended(AgentState was, bool stopped, DateTimeOffset now)
    {
        if (was is AgentState.Refused or AgentState.Asking || was == AgentState.Allowed && stopped) _quietUntil = now + Quiet;
    }
}

public static partial class AgentRules
{
    /// A connection that says nothing for this long is let go.
    public static readonly TimeSpan HelloWithin = TimeSpan.FromSeconds(5);
    /// Asked nothing for this long, an allowed agent is shown as idle and its tab's mark goes…
    public static readonly TimeSpan Idle = TimeSpan.FromSeconds(8);
    /// …and for this long, let go.
    public static readonly TimeSpan LetGo = TimeSpan.FromMinutes(5);
    /// A click on the question in its first half second is taken as one meant for whatever was there before.
    public static readonly TimeSpan Settling = TimeSpan.FromSeconds(0.5);

    /// Whether a press on the question is yours to count: a pointer's click (never a key, Accessibility or UI
    /// Automation), and not in the question's first half second, when it was meant for whatever was there before.
    public static bool FreshClick(DateTimeOffset asked, DateTimeOffset at, bool pointer) => pointer && at - asked > Settling;

    /// An address to open or go to: http or https, with a host, and no name or password in it. A bare host is https.
    public static Uri? Address(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace)) return null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url) || string.IsNullOrEmpty(url.Host))
            if (trimmed.Contains(':') || !Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out url)) return null;
        return url.Scheme is "http" or "https" && url.Host.Length > 0 && url.UserInfo.Length == 0 ? url : null;
    }

    /// Whether a page in an agent's tab may go somewhere by itself: the web, and the blank and generated pages the web
    /// makes; never Search's own pages, an extension's, a file, or a scheme some other app would be handed.
    public static bool MayLoad(Uri url) => url.Scheme.ToLowerInvariant() is "http" or "https" or "about" or "blob" or "data";

    /// A key to press: Enter, Tab, Escape, Backspace, Delete, the arrows, Home, End, PageUp, PageDown, Space, or one
    /// character, after "shift+" or "alt+"; "ctrl+" only before a named key. Never the Windows key or ctrl and a
    /// character: those are commands (copy, paste, close) and yours. Never shift+Delete, which cuts on Windows.
    public static AgentKey? Key(string? spec)
    {
        spec ??= "";
        var parts = spec.Split('+').ToList();
        // "shift++" is shift and the plus key.
        if (spec.EndsWith("++", StringComparison.Ordinal)) { parts.RemoveRange(parts.Count - 2, 2); parts.Add("+"); }
        if (parts.Count == 0 || parts[^1] is not { Length: > 0 } last) return null;
        parts.RemoveAt(parts.Count - 1);
        var flags = 0;
        foreach (var part in parts)
        {
            if (part.ToLowerInvariant() switch { "shift" => AgentKey.Shift, "alt" or "opt" or "option" => AgentKey.Alt, "ctrl" or "control" => AgentKey.Ctrl, _ => (int?)null } is not { } bit) return null;
            flags |= bit;
        }
        if (AgentKey.Named.TryGetValue(last.ToLowerInvariant(), out var named))
            return named.Key == "Delete" && (flags & AgentKey.Shift) != 0 ? null : named with { Modifiers = flags };
        if (new StringInfo(last).LengthInTextElements != 1 || last.Contains('\n') || last.Contains('\r') || (flags & AgentKey.Ctrl) != 0 || !Typeable(last)) return null;
        return (AgentKey.Of(last) ?? new AgentKey(last, "", 0, last)) with { Modifiers = flags };
    }

    /// What wait_for may wait on. Never a CSS selector: attribute selectors (escaped any way CSS allows),
    /// :placeholder-shown, :invalid or :autofill can each be made to answer what a field holds, a letter at a time. On
    /// one of your tabs, only its address and title.
    public static AgentCondition Condition(JsonObject request, bool own)
    {
        if (request.ContainsKey("selector")) throw new AgentRefusal("bad_condition", "wait_for takes no selector", "Wait for text, a role and name, the url or the title.");
        var condition = new AgentCondition { Url = Text(request, "url"), Title = Text(request, "title"), Gone = Flag(request, "gone") == true };
        foreach (var key in new[] { "text", "role", "name" })
        {
            if (Text(request, key) is not { } value) continue;
            if (!own) throw new AgentRefusal("bad_condition", "On the person's tab, only url and title", "Open the address in a tab of your own to wait on its content.");
            condition.Page[key] = value;
        }
        return condition;
    }

    [GeneratedRegex(@"pass|pwd|^pw\d*$|^p$|^pwd?\d*$|mdp|kennwort|contrase|senha|parol|wachtwoord|losen|salasana|haslo|otp|code|token|secret|auth|key|session|sig|cvc|cvv|card|pin$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s""'<>]+")]
    private static partial Regex AnAddress();

    /// An address as an agent is given it: the values of anything in its query or fragment named like a password, a
    /// code or a token taken out. A sign-in sent by GET, or a link back from one, carries them there.
    public static string Url(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Any(char.IsWhiteSpace)) return text ?? "";
        var hash = text.IndexOf('#');
        var (beforeHash, fragment) = hash >= 0 ? (text[..hash], text[(hash + 1)..]) : (text, null);
        var mark = beforeHash.IndexOf('?');
        var (head, query) = mark >= 0 ? (beforeHash[..mark], beforeHash[(mark + 1)..]) : (beforeHash, null);
        var changed = false;
        if (query is not null && Hidden(query) is { } q) { query = q; changed = true; }
        // access_token=… after the #, as OAuth hands it back.
        if (fragment is not null && fragment.Contains('=') && Hidden(fragment) is { } f) { fragment = f; changed = true; }
        if (!changed) return text;
        return head + (query is null ? "" : "?" + query) + (fragment is null ? "" : "#" + fragment);
    }

    /// The items of a query with secret-named values redacted, or null when none was.
    private static string? Hidden(string query)
    {
        var changed = false;
        var items = query.Split('&').Select(item =>
        {
            var equals = item.IndexOf('=');
            if (equals < 0 || equals == item.Length - 1) return item;
            var name = item[..equals];
            string decoded;
            try { decoded = Uri.UnescapeDataString(name); } catch (UriFormatException) { decoded = name; }
            if (!SecretName().IsMatch(decoded)) return item;
            changed = true;
            return name + "=redacted";
        }).ToList();
        return changed ? string.Join('&', items) : null;
    }

    /// Secret-looking values taken out of every address in a piece of text, as a page's failure says where it failed.
    public static string Scrubbed(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return AnAddress().Replace(text, match =>
        {
            var found = match.Value;
            // Not the sentence's own full stop or colon after it.
            var end = found.Length;
            while (end > 0 && ".,;:!?)".Contains(found[end - 1])) end--;
            return Url(found[..end]) + found[end..];
        });
    }

    /// Text an agent may type: no control or format characters but tab and newline. ctrl+k is "\u000b" by another name.
    public static bool Typeable(string text) => text.EnumerateRunes().All(r => r.Value is '\t' or '\n' or '\r' || Rune.GetUnicodeCategory(r) is not (UnicodeCategory.Control or UnicodeCategory.Format));

    /// Why an agent can't use a tab this way, or null if it can. A private tab is never there at all, and neither is a
    /// script's (the bench's): not listed, not found, not said to exist.
    public static string? Refusal(Tab tab, bool own, AgentConsent consent, AgentUse use) =>
        tab.Private || tab.Bench && !own ? "stale_page" : consent.May(use, own) ? null : "not_yours";

    /// The name a client gives itself, as it may be shown: printable, short, and never empty. It is the client's word;
    /// the question says whose program asked beside it.
    public static string ClientName(string? raw)
    {
        var kept = new string((raw ?? "").EnumerateRunes().Where(r => Rune.GetUnicodeCategory(r) is not (UnicodeCategory.Control or UnicodeCategory.Format) && r.Value is not ('“' or '”' or '"')).SelectMany(r => r.ToString()).ToArray()).Trim();
        if (kept.Length == 0) return "A program";
        var info = new StringInfo(kept);
        return info.LengthInTextElements <= 40 ? kept : info.SubstringByTextElements(0, 40);
    }

    /// An element handle, p1.s3.e12: its page, the read it came from, and its place in that read.
    public static (string Page, int Snap, int Index)? Element(string? handle)
    {
        var parts = (handle ?? "").Split('.');
        if (parts.Length != 3 || !parts[1].StartsWith('s') || !parts[2].StartsWith('e')) return null;
        return int.TryParse(parts[1][1..], NumberStyles.None, CultureInfo.InvariantCulture, out var snap) && int.TryParse(parts[2][1..], NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? (parts[0], snap, index) : null;
    }

    /// How long a request may take before it is answered no_answer: a page that never replies doesn't hold the agent.
    public static TimeSpan Patience(JsonObject request) => TimeSpan.FromSeconds(Math.Min(60, (Number(request, "timeout") ?? 20) + 15));

    /// Seconds to wait for a page to settle, never past half a minute.
    public static double Seconds(JsonObject request, double fallback) => Math.Clamp(Number(request, "timeout") ?? fallback, 0, 30);

    public static int MostNodes(JsonObject request) => (int)Math.Clamp(Number(request, "max_nodes") ?? 300, 1, 2000);
    public static int LongestText(JsonObject request) => (int)Math.Clamp(Number(request, "max_text") ?? 20000, 0, 200_000);
    /// A picture's width, in CSS pixels, if one was asked for.
    public static double? ShotWidth(JsonObject request) => Number(request, "width") is { } w ? Math.Clamp(w, 200, 2560) : null;

    /// Whether an allowed agent counts as busy, and whether it is let go, after so long with nothing asked.
    public static (bool Busy, bool LetGo) Quiet(bool working, TimeSpan quiet, TimeSpan idle, TimeSpan letGo)
    {
        if (working) quiet = TimeSpan.Zero;
        return (quiet < idle, quiet >= letGo);
    }

    public static string RefusalText(string code) => code switch
    {
        "hello_first" => "Say hello first",
        "said_hello" => "Already said",
        "not_allowed" => "You were not allowed to use Search",
        _ => "Stopped in Search"
    };

    public static string? Text(JsonObject request, string key) => request[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static double? Number(JsonObject request, string key) =>
        request[key] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    public static bool? Flag(JsonObject request, string key) => request[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
