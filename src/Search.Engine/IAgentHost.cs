using Search.Core;

namespace Search.Engine;

// What a coding agent's session needs of the window it works in (AgentServer), and nothing more. The window draws the
// question, Stop and the working mark (AgentBanner.swift); everything an agent may do is decided by the session.
// Every member is called on the window's own thread (the SynchronizationContext the server was given).

/// The window an agent works in.
public interface IAgentHost
{
    /// The window is closing: the session working in it ends.
    event Action? Closed;

    /// The window's row as it is now, in order. The session lists only the agent's own tabs and, if you said so, your
    /// ordinary ones; private tabs and the bench's are never there for it.
    IReadOnlyList<Tab> Tabs { get; }

    /// A tab of the agent's own (Browser.Open with agent: true): at the end of the row, never selected, never in the
    /// session or the history, its web view built (drawn but unseen, as a bench tab's) and on its way to `url`.
    Task<Tab> Open(Uri url);

    /// One of the agent's own tabs, closed.
    void Close(Tab tab);

    /// A tab's isolated world (Pages.WorldOf), and through it its CoreWebView2 (World.Core). With `build`, asked only
    /// for the agent's own tabs, its web view is built first if it has none; without, null for a tab asleep: one of
    /// yours is never woken for an agent.
    Task<World?> WorldOf(Tab tab, bool build);

    /// "<client> wants to use Search", in this window: Allow, Don't Allow, "Let it see my open tabs", and "Always allow"
    /// where it is offered (question.NoAlways null; otherwise that is why not, said small). Completes with your answer,
    /// which only a fresh click of the pointer on that control in this window gives (AgentRules.FreshClick): never a
    /// key, never Accessibility or UI Automation, never in the question's first half second. Cancelled when the
    /// connection goes first: the question goes too.
    Task<AgentAnswer> Ask(AgentQuestion question, CancellationToken gone);

    /// The working mark: on this tab and off every other, or off all of them (null).
    void Working(Tab? tab);

    /// What the window's chrome shows: Stop and who it stops (in the column's foot or the strip, never over the page),
    /// or nothing (null). Stop, pressed, calls AgentServer.Stop.
    void Show(AgentShown? shown);
}

/// The question as asked.
public sealed record AgentQuestion(string Client, string Program, AgentLauncher? Launcher, string? NoAlways, DateTimeOffset Asked);

/// Your answer to it.
public sealed record AgentAnswer(bool Allow, bool SeesTabs = false, bool Always = false);

/// An allowed agent, as the window shows it. `Auto`: let in without asking (remembered), "allowed automatically";
/// `JustAdmitted` for the first few seconds of that, when the line says so out loud (and, with the column folded or a
/// page full screen, a line at the window's foot: "<client> was let in automatically — Stop stops it"). `Busy`: asked
/// for something in the last few seconds; otherwise shown idle.
public sealed record AgentShown(string Client, string Program, bool SeesTabs, bool Auto, bool JustAdmitted, bool Busy);
