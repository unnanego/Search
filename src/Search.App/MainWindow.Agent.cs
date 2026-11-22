using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Search.Core;
using Search.Engine;

namespace Search.App;

// What the window shows of a coding agent (AgentBanner.swift; the rules are AgentControl's, in AgentSession). When
// one connects, the question, at the foot of the page, in the window it asked in only. After that nothing over the
// page: while it is allowed, a quiet line at the foot of the column (or at the end of the tabs across the top) says
// who is using Search and where, with Stop, for as long as it's connected. Neither the question nor its buttons take
// the keys: only a click of the pointer answers, never Enter, never UI Automation, never in its first half second.
public sealed partial class MainWindow : IAgentHost
{
    private Action? _agentClosed;
    event Action? IAgentHost.Closed { add => _agentClosed += value; remove => _agentClosed -= value; }

    private Border? _agentCard;
    private TaskCompletionSource<AgentAnswer>? _agentAsked;
    private DateTimeOffset _agentAskedAt;
    private (CheckBox Tabs, CheckBox Always)? _agentBoxes;
    private readonly StackPanel _agentLine = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _agentSaid = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 };
    private Tab? _agentWorking;
    private AgentShown? _agentShown;

    private void WireAgent()
    {
        var stop = new Button { Content = "Stop", Padding = new Thickness(10, 2, 10, 3), FontSize = 12, IsTabStop = false };
        stop.Click += (_, _) => App.Agents?.Stop();
        _agentLine.Children.Add(new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"] });
        _agentLine.Children.Add(_agentSaid);
        _agentLine.Children.Add(stop);
        _pages.Opened += (tab, opener) => App.Agents?.Adopted(tab, opener);
    }

    /// The Stop line where the chrome is: the column's foot, or beside the tabs across the top.
    private void LayAgent(Panel? top)
    {
        Detach(_agentLine);
        if (_prefs.TabsOnSide && SpacesBar.Parent is Panel foot) foot.Children.Insert(foot.Children.IndexOf(SpacesBar), _agentLine);
        else top?.Children.Insert(0, _agentLine);
    }

    IReadOnlyList<Tab> IAgentHost.Tabs => _browser.Tabs.ToList();

    async Task<Tab> IAgentHost.Open(Uri url)
    {
        var tab = _browser.Open(url, select: false, agent: true);
        await _pages.Ensure(tab);
        return tab;
    }

    void IAgentHost.Close(Tab tab) { if (tab.Agent) _browser.Close(tab); }

    async Task<World?> IAgentHost.WorldOf(Tab tab, bool build)
    {
        if (_pages.WorldOf(tab) is { } world) return world;
        if (!build || !tab.Agent) return null;
        await _pages.Ensure(tab);
        return _pages.WorldOf(tab);
    }

    Task<AgentAnswer> IAgentHost.Ask(AgentQuestion question, CancellationToken gone)
    {
        DropAgentQuestion(new AgentAnswer(false));
        var asked = new TaskCompletionSource<AgentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _agentAsked = asked;
        _agentAskedAt = DateTimeOffset.UtcNow;
        var tabs = new CheckBox { Content = "Let it see my open tabs", IsTabStop = false };
        var always = new CheckBox { Content = "Always allow", IsTabStop = false, IsEnabled = question.NoAlways is null };
        _agentBoxes = (tabs, always);
        var allow = new Button { Content = "Allow", Style = (Style)Application.Current.Resources["AccentButtonStyle"], IsTabStop = false };
        var deny = new Button { Content = "Don't Allow", IsTabStop = false };
        // Tapped comes only from a pointer: a key, or UI Automation's Invoke, makes a Click and no Tapped.
        allow.Tapped += (_, e) => OnAgentPress(e, allow: true);
        deny.Tapped += (_, e) => OnAgentPress(e, allow: false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { allow, deny, tabs, always } };
        var panel = new StackPanel { Spacing = 8, Children =
        {
            new TextBlock { Text = $"{question.Client} wants to use Search", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
            new TextBlock { Text = $"{question.Program}. It would work in tabs of its own, and never see your passwords, private tabs or other windows.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 },
            buttons
        } };
        if (question.NoAlways is { } why) panel.Children.Add(new TextBlock { Text = why, TextWrapping = TextWrapping.Wrap, Opacity = 0.6, FontSize = 11 });
        _agentCard = new Border
        {
            Child = panel, Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 20), MaxWidth = 620, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Background = (Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
        };
        Grid.SetRow(_agentCard, 1);
        Grid.SetColumn(_agentCard, 1);
        Canvas.SetZIndex(_agentCard, 20);
        Root.Children.Add(_agentCard);
        gone.Register(() => DispatcherQueue.TryEnqueue(() => { if (_agentAsked == asked) DropAgentQuestion(null); }));
        return asked.Task;
    }

    private void OnAgentPress(TappedRoutedEventArgs e, bool allow)
    {
        e.Handled = true;
        if (_agentAsked is null || !AgentRules.FreshClick(_agentAskedAt, DateTimeOffset.UtcNow, e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse || e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen || e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)) return;
        AnswerAgent(allow);
    }

    /// The question answered: Allow with what was ticked, or Don't Allow.
    private void AnswerAgent(bool allow)
    {
        var (tabs, always) = _agentBoxes ?? (new CheckBox(), new CheckBox());
        DropAgentQuestion(allow ? new AgentAnswer(true, tabs.IsChecked == true, always.IsEnabled && always.IsChecked == true) : new AgentAnswer(false));
    }

    private void DropAgentQuestion(AgentAnswer? answer)
    {
        if (_agentCard is not null) Root.Children.Remove(_agentCard);
        _agentCard = null;
        _agentBoxes = null;
        var asked = _agentAsked;
        _agentAsked = null;
        if (asked is null) return;
        if (answer is null) asked.TrySetCanceled(); else asked.TrySetResult(answer);
    }

    void IAgentHost.Working(Tab? tab) { _agentWorking = tab; SayAgent(); }

    void IAgentHost.Show(AgentShown? shown)
    {
        _agentShown = shown;
        SayAgent();
        // Let in by itself while there is no column to see it in: a notice says so once.
        if (shown is { Auto: true, JustAdmitted: true } && (!_prefs.TabsOnSide || _prefs.Folded || _fullScreen)) Notify($"{shown.Client} was let in automatically. Stop stops it.");
    }

    private void SayAgent()
    {
        if (_agentShown is not { } shown) { _agentLine.Visibility = Visibility.Collapsed; return; }
        var where = _agentWorking is { } tab ? " · " + (tab.Shown.Length > 0 ? tab.Shown : "a tab") : "";
        _agentSaid.Text = $"{shown.Client} is using Search{(shown.Auto ? ", allowed automatically" : "")}{(shown.Busy ? where : " · idle")}";
        ToolTipService.SetToolTip(_agentLine, $"{shown.Program}{(shown.SeesTabs ? ". It can see your open tabs" : "")}.");
        _agentLine.Opacity = shown.Busy ? 1 : 0.65;
        _agentLine.Visibility = Visibility.Visible;
    }

    /// The window goes: an agent working in it is done.
    private void AgentGone() { DropAgentQuestion(null); _agentClosed?.Invoke(); }
}
