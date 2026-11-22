using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Search.Core;

namespace Search.Core.Tests;

/// Live folders' fetch (LiveFetch.cs) and keeping fresh (LiveFolders.cs), against a server of the test's own on
/// 127.0.0.1: a feed, a page pointing at one, too much, too slow, not XML, redirects, and no cookie ever sent. Nothing
/// leaves the machine.
public sealed class LiveFetchTests : IDisposable
{
    private const string Feed = """
        <rss version="2.0"><channel><title>Probe Feed</title>
          <item><title>Post 2</title><link>/post/2</link></item>
          <item><title>Post 1</title><link>/post/1</link></item>
        </channel></rss>
        """;

    private readonly HttpListener _listener = new();
    private readonly string _base;
    /// Each request as it came: its path, and the cookie and authorization it sent.
    private readonly ConcurrentQueue<(string Path, string? Cookie, string? Authorization)> _seen = new();
    private readonly CancellationTokenSource _stop = new();
    private Func<string, HttpListenerResponse, Task> _serve = (_, r) => { r.StatusCode = 404; return Task.CompletedTask; };

    public LiveFetchTests()
    {
        TempStore.Ready();
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _base = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(_base + "/");
        _listener.Start();
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(async () =>
            {
                var path = context.Request.Url!.PathAndQuery;
                _seen.Enqueue((path, context.Request.Headers["Cookie"], context.Request.Headers["Authorization"]));
                try { await _serve(path, context.Response); context.Response.Close(); }
                catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException) { }
            });
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }

    private static async Task Write(HttpListenerResponse response, string text, string type = "application/rss+xml")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentType = type;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private static Task Redirect(HttpListenerResponse response, string to)
    {
        response.StatusCode = 302;
        response.Headers["Location"] = to;
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AFeedIsFetchedAndItsLinksReadAgainstIt()
    {
        _serve = (path, r) => path == "/feed.xml" ? Write(r, Feed) : Task.CompletedTask;
        using var fetch = new LiveFetch();
        var result = await fetch.Load(_base + "/feed.xml");
        Assert.Null(result.Problem);
        Assert.Equal("Probe Feed", result.Read!.Title);
        Assert.Equal(_base + "/feed.xml", result.Read.Feed);
        Assert.Equal([_base + "/post/2", _base + "/post/1"], result.Read.Items.Select(i => i.Link));
    }

    [Fact]
    public async Task APagesFeedIsFoundAndItIsTheFolders()
    {
        _serve = (path, r) => path switch
        {
            "/blog" => Write(r, """<html><head><link rel="alternate" type="application/rss+xml" href="/feed.xml"></head></html>""", "text/html"),
            "/feed.xml" => Write(r, Feed),
            _ => Write(r, "<html></html>", "text/html"),
        };
        using var fetch = new LiveFetch();
        var result = await fetch.Load(_base + "/blog");
        Assert.Equal(_base + "/feed.xml", result.Read!.Feed);
        Assert.Equal(2, result.Read.Items.Count);
        Assert.Equal(LiveFeed.Problem.NotAFeed, (await fetch.Load(_base + "/plain")).Problem);
    }

    [Fact]
    public async Task APageThatPointsAtItselfIsNoFeed()
    {
        _serve = (_, r) => Write(r, $"""<html><link rel="alternate" type="application/rss+xml" href="{_base}/self"></html>""", "text/html");
        using var fetch = new LiveFetch();
        Assert.Equal(LiveFeed.Problem.NotAFeed, (await fetch.Load(_base + "/self")).Problem);
        Assert.Single(_seen);
    }

    [Fact]
    public async Task MoreThanTwoMegabytesIsTooBigSaidOrNot()
    {
        _serve = async (path, r) =>
        {
            var chunk = Encoding.UTF8.GetBytes(new string('x', 100_000));
            if (path == "/said") r.ContentLength64 = 3_000_000; else r.SendChunked = true;
            await r.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("<rss version=\"2.0\"><channel><title>"));
            for (var i = 0; i < 30; i++) await r.OutputStream.WriteAsync(chunk);
        };
        using var fetch = new LiveFetch();
        Assert.Equal(LiveFeed.Problem.TooBig, (await fetch.Load(_base + "/said")).Problem);
        Assert.Equal(LiveFeed.Problem.TooBig, (await fetch.Load(_base + "/streamed")).Problem);
    }

    [Fact]
    public async Task ASilentServerIsGivenUpOn()
    {
        _serve = async (path, r) =>
        {
            if (path == "/slow-start") { await Task.Delay(5_000); return; }
            r.SendChunked = true;
            await r.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("<rss version=\"2.0\"><channel><title>T</title>"));
            await r.OutputStream.FlushAsync();
            // A trickle that never ends: under the idle timeout each time, past the whole one in all.
            for (var i = 0; i < 100; i++) { await Task.Delay(100); await r.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(" ")); await r.OutputStream.FlushAsync(); }
        };
        using var fetch = new LiveFetch(idle: TimeSpan.FromMilliseconds(500), whole: TimeSpan.FromSeconds(2));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(LiveFeed.Problem.Network, (await fetch.Load(_base + "/slow-start")).Problem);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        watch.Restart();
        Assert.Equal(LiveFeed.Problem.Network, (await fetch.Load(_base + "/trickle")).Problem);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BadXmlOrAnErrorOrNothingThereIsNoFolder()
    {
        _serve = (path, r) =>
        {
            if (path == "/broken") return Write(r, "<rss><channel><title>T</title><item><title>Kept</title><link>/k</link></item><item><title>&bogus;");
            if (path == "/junk") return Write(r, "\u0000\u0001 not xml", "application/octet-stream");
            r.StatusCode = path == "/gone" ? 404 : 500;
            return Task.CompletedTask;
        };
        using var fetch = new LiveFetch();
        Assert.Equal(["Kept"], (await fetch.Load(_base + "/broken")).Read!.Items.Select(i => i.Title));
        Assert.Equal(LiveFeed.Problem.NotAFeed, (await fetch.Load(_base + "/junk")).Problem);
        Assert.Equal(LiveFeed.Problem.Network, (await fetch.Load(_base + "/gone")).Problem);
        Assert.Equal(LiveFeed.Problem.Network, (await fetch.Load(_base + "/error")).Problem);
        // Nothing listening there at all.
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        Assert.Equal(LiveFeed.Problem.Network, (await fetch.Load($"http://127.0.0.1:{port}/feed")).Problem);
        Assert.Equal(LiveFeed.Problem.NotAFeed, (await fetch.Load("file:///C:/Windows/win.ini")).Problem);
    }

    [Fact]
    public async Task RedirectsAreFollowedOnTheWebOnly()
    {
        _serve = (path, r) => path switch
        {
            "/moved" => Redirect(r, "/hop"),
            "/hop" => Redirect(r, _base + "/feed.xml"),
            "/feed.xml" => Write(r, Feed),
            "/to-file" => Redirect(r, "file:///C:/Windows/win.ini"),
            "/to-script" => Redirect(r, "javascript:alert(1)"),
            _ => Redirect(r, "/loop"),
        };
        using var fetch = new LiveFetch();
        var moved = await fetch.Load(_base + "/moved");
        // As the Mac's: the folder keeps the address asked for, and links are read against it.
        Assert.Equal(_base + "/moved", moved.Read!.Feed);
        Assert.Equal(_base + "/post/2", moved.Read.Items[0].Link);
        Assert.Equal(LiveFeed.Problem.Network, (await fetch.Load(_base + "/to-file")).Problem);
        Assert.Equal(LiveFeed.Problem.Network, (await fetch.Load(_base + "/to-script")).Problem);
        _seen.Clear();
        Assert.Equal(LiveFeed.Problem.Network, (await fetch.Load(_base + "/loop")).Problem);
        Assert.Equal(LiveFetch.MostRedirects + 1, _seen.Count);
    }

    [Fact]
    public async Task NoCookieIsKeptOrSent()
    {
        _serve = (path, r) =>
        {
            r.Headers.Add("Set-Cookie", "session=1; Path=/");
            return path == "/login" ? Redirect(r, "/feed.xml") : Write(r, Feed);
        };
        using var fetch = new LiveFetch();
        await fetch.Load(_base + "/login");
        await fetch.Load(_base + "/feed.xml");
        Assert.Equal(3, _seen.Count);
        Assert.All(_seen, s => { Assert.Null(s.Cookie); Assert.Null(s.Authorization); });
    }

    // Keeping fresh.

    [Fact]
    public async Task ARefreshKeepsIdsAFailureKeepsPostsAndTheSwitchOffFetchesNothing()
    {
        var posts = Feed;
        var up = true;
        _serve = (_, r) => { if (!up) { r.StatusCode = 503; return Task.CompletedTask; } return Write(r, posts); };
        using var fetch = new LiveFetch();
        var file = TempStore.Unique("live-folders");
        var store = new PinFolders(file);
        var folders = new LiveFolders(store, fetch);
        var space = Guid.NewGuid();
        var made = await folders.Load(_base + "/feed.xml");
        var (list, id) = LiveFeed.Made([], made.Read!);
        store.Set(space, list);
        var first = store.Folders(space)[0].Items!;
        var told = 0;
        store.Changed += _ => told++;

        posts = Feed.Replace("<item><title>Post 2</title>", "<item><title>Post 3</title><link>/post/3</link></item><item><title>Post 2</title>");
        await folders.RefreshAll(true);
        var second = store.Folders(space)[0].Items!;
        Assert.Equal(["Post 3", "Post 2", "Post 1"], second.Select(i => i.Title));
        Assert.Equal(first.Select(i => i.Id), second.Skip(1).Select(i => i.Id));
        Assert.Equal("127.0.0.1", folders.Tip(store.Folders(space)[0]));

        up = false;
        await folders.Refresh(id, space);
        Assert.Equal(second, store.Folders(space)[0].Items);
        Assert.Contains(id, folders.Failed);
        Assert.Equal(LiveFeed.CouldNotRefresh, folders.Tip(store.Folders(space)[0]));
        Assert.Equal(2, told);
        // Kept out of the file: a new reader of it knows nothing of the failure.
        Assert.DoesNotContain("refresh", File.ReadAllText(Store.File(file)), StringComparison.OrdinalIgnoreCase);

        up = true;
        _seen.Clear();
        await folders.RefreshAll(false);
        Assert.Empty(_seen);
        await folders.Refresh(id, space);
        Assert.DoesNotContain(id, folders.Failed);
        folders.Forget(id);
    }

    [Fact]
    public async Task ASecondRefreshWhileOneIsUnderWayDoesNothing()
    {
        var gate = new TaskCompletionSource();
        _serve = async (_, r) => { await gate.Task; await Write(r, Feed); };
        using var fetch = new LiveFetch();
        var store = new PinFolders(TempStore.Unique("live-folders"));
        var folders = new LiveFolders(store, fetch);
        var space = Guid.NewGuid();
        var live = new PinFolder(Guid.NewGuid(), "Feed") { Feed = _base + "/feed.xml", Items = [] };
        store.Set(space, [live]);
        var one = folders.Refresh(live.Id, space);
        Assert.True(folders.IsFetching(live.Id));
        await folders.Refresh(live.Id, space);
        gate.SetResult();
        await one;
        Assert.False(folders.IsFetching(live.Id));
        Assert.Single(_seen);
        Assert.Equal(2, store.Folders(space)[0].Items!.Count);
    }
}
