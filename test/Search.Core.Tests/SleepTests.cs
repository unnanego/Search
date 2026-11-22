using Search.Core;

namespace Search.Core.Tests;

public class SleepTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HalfHour = TimeSpan.FromMinutes(30);

    private static IdleTab Away(double minutes) => new(Guid.NewGuid()) { Touched = Now.AddMinutes(-minutes) };

    [Fact]
    public void ThePaces()
    {
        Assert.Equal(TimeSpan.FromHours(2), Sleep.Wait(SleepPace.Later));
        Assert.Equal(HalfHour, Sleep.Wait(SleepPace.Normal));
        Assert.Equal(TimeSpan.FromMinutes(10), Sleep.Wait(SleepPace.Sooner));
        Assert.Equal(SleepPace.Later, Sleep.Pace("later"));
        Assert.Equal(SleepPace.Normal, Sleep.Pace(null));
        Assert.Equal(SleepPace.Normal, Sleep.Pace("whenever"));
        Assert.Equal("sooner", Sleep.Raw(SleepPace.Sooner));
        Assert.Equal("After half an hour away.", Sleep.Detail(SleepPace.Normal));
        Assert.Equal(TimeSpan.FromMinutes(10), Sleep.After(SleepPace.Later, savingPower: true));
        Assert.Equal(TimeSpan.FromMinutes(10), Sleep.After(SleepPace.Sooner, savingPower: true));
        Assert.Equal(HalfHour, Sleep.After(SleepPace.Normal));
        Assert.Equal(TimeSpan.FromSeconds(4), Sleep.After(SleepPace.Later, savingPower: true, seconds: 4));
        Assert.Equal(TimeSpan.FromSeconds(60), Sleep.Every(HalfHour));
        Assert.Equal(TimeSpan.FromSeconds(5), Sleep.Every(TimeSpan.FromSeconds(4)));
        Assert.Equal(TimeSpan.FromSeconds(10), Sleep.Every(TimeSpan.FromSeconds(40)));
        Assert.Equal(TimeSpan.Zero, Sleep.Pressed(critical: true));
        Assert.Equal(TimeSpan.FromMinutes(5), Sleep.Pressed(critical: false));
    }

    [Fact]
    public void WhatStaysAwake()
    {
        Assert.Null(Sleep.Awake(Away(40)));
        Assert.Equal("on screen", Sleep.Awake(Away(40) with { OnScreen = true }));
        Assert.Equal("pinned", Sleep.Awake(Away(40) with { Pinned = true }));
        Assert.Null(Sleep.Awake(Away(40) with { Pinned = true, Parked = true }));
        Assert.Equal("pinned, and its site may send notifications", Sleep.Awake(Away(40) with { Pinned = true, Parked = true, MayNotify = true }));
        Assert.Null(Sleep.Awake(Away(40) with { Pinned = true }, shut: true));
        Assert.Null(Sleep.Awake(Away(40) with { OnScreen = true }, shut: true));
        Assert.Equal("a bench tab", Sleep.Awake(Away(40) with { Bench = true }));
        Assert.Equal("blank", Sleep.Awake(Away(40) with { Blank = true }));
        Assert.Equal("already asleep", Sleep.Awake(Away(40) with { Asleep = true }));
        Assert.Equal("no page", Sleep.Awake(Away(40) with { HasPage = false }));
        Assert.Equal("still loading", Sleep.Awake(Away(40) with { Loading = true }));
        Assert.Equal("its video is out", Sleep.Awake(Away(40) with { VideoOut = true }));
        Assert.Equal("recording the screen", Sleep.Awake(Away(40) with { RecordingScreen = true }));
        Assert.Equal("the page on screen came from it", Sleep.Awake(Away(40) with { Opened = true }));
        Assert.Null(Sleep.Awake(Away(40) with { Named = true, Grouped = true, Private = true }));
    }

    [Fact]
    public void TheIdleOnesOldestFirst()
    {
        var (a, b, c, d) = (Away(45), Away(90), Away(10), Away(200) with { Noisy = true });
        var due = Sleep.Due([a, b, c, d], Now, HalfHour);
        Assert.Equal(new[] { b.Id, a.Id }, due);
        Assert.Equal(new[] { d.Id, b.Id, a.Id, c.Id }, Sleep.Due([a, b, c, d with { Noisy = false }], Now, Sleep.Pressed(critical: true)));
        Assert.True(Sleep.StillIdle(a, Now, HalfHour));
        Assert.False(Sleep.StillIdle(a with { Touched = Now }, Now, HalfHour));
    }

    [Fact]
    public void TheLastWindowClosed()
    {
        var (shown, pin, loading, playing) = (Away(0) with { OnScreen = true }, Away(0) with { Pinned = true }, Away(0) with { Loading = true }, Away(0) with { Noisy = true });
        var (sleep, again) = Sleep.Shut([shown, pin, loading, playing]);
        Assert.Equal(new[] { shown.Id, pin.Id }, sleep);
        Assert.True(again);
        Assert.False(Sleep.Shut([shown]).LookAgain);
    }

    [Fact]
    public void SitesKeptAwake()
    {
        Assert.Equal("mail.example.com", Sleep.AwakeHost(new Uri("https://Mail.Example.com/inbox")));
        Assert.Null(Sleep.AwakeHost(new Uri("file:///C:/a.pdf")));
        Assert.Null(Sleep.AwakeHost(null));
        var sites = Sleep.KeepAwake(new HashSet<string>(), new Uri("https://music.example/"), true);
        Assert.True(Sleep.KeepsAwake(new Uri("https://music.example/play"), true, sites));
        Assert.False(Sleep.KeepsAwake(new Uri("https://music.example/play"), false, sites));
        Assert.False(Sleep.KeepsAwake(new Uri("https://other.example/"), true, sites));
        Assert.Empty(Sleep.KeepAwake(sites, new Uri("https://music.example/x"), false));
        Assert.Same(sites, Sleep.KeepAwake(sites, new Uri("about:blank"), false));
    }

    [Fact]
    public void WhatASleptTabKeeps()
    {
        var slept = new Slept(new Uri("https://a.example/long"), "Long read") { ScrollX = 0, ScrollY = 1009.5, Picture = [1, 2, 3] };
        Assert.Equal("window.scrollTo(0, 1009.5)", Sleep.RestoreScroll(slept));
        Assert.Equal("[window.scrollX, window.scrollY]", Sleep.ReadScroll);
    }

    /// A page's sessionStorage kept through sleep (KeptSession): only an ordinary tab's, only up to the cap, and only
    /// JSON of the shape the page was asked for, since it goes into a script as it is.
    [Fact]
    public void ASleepingTabKeepsItsSessionStorage()
    {
        const string read = """["https://shop.example",[["draft","half a sentence"],["step","2"]]]""";
        Assert.Equal(read, Sleep.KeepSession(read, @private: false));
        Assert.Null(Sleep.KeepSession(read, @private: true));
        Assert.Null(Sleep.KeepSession(null, @private: false));
        Assert.Null(Sleep.KeepSession("", @private: false));
        var big = $$"""["https://shop.example",[["k","{{new string('x', Sleep.SessionCap)}}"]]]""";
        Assert.Null(Sleep.KeepSession(big, @private: false));
        var justUnder = $$"""["https://shop.example",[["k","{{new string('x', Sleep.SessionCap - 40)}}"]]]""";
        Assert.NotNull(Sleep.KeepSession(justUnder, @private: false));
        // Counted in UTF-8 bytes, as the Mac counts them: two bytes a letter here.
        Assert.Null(Sleep.KeepSession($$"""["https://a.example",[["k","{{new string('é', Sleep.SessionCap / 2)}}"]]]""", @private: false));
        foreach (var odd in new[] { "alert(1)", "[1,2]", """["https://a.example"]""", """["https://a.example",[["k",1]]]""", """["https://a.example",[["k"]]]""", """{"a":1}""", "[\"x\",[]]);alert(1);([" })
            Assert.Null(Sleep.KeepSession(odd, @private: false));
        var script = Sleep.RestoreSession(read);
        Assert.Contains("const [origin, items] = " + read + ";", script);
        Assert.Contains("if (window !== top) return;", script);
        Assert.Contains("location.origin !== origin || sessionStorage.length", script);
        Assert.Contains("sessionStorage.setItem(k, v)", script);
        Assert.Contains("JSON.stringify([location.origin, items])", Sleep.ReadSession);
    }
}
