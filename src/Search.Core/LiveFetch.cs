using System.Net;

namespace Search.Core;

// A live folder's feed, fetched (LiveFolders.swift's Feed.load and its session): a plain GET on a client of its own
// that keeps nothing. No cookies sent or taken, no sign-in of any space or of Windows, no cache (HttpClient keeps none), no proxy credentials;
// https or http only, each redirect too (followed by hand, so one to anything else is refused). It gives up when 15
// seconds pass with nothing come, past 30 seconds in all, and past 2 MB, read while it streams in. A page is read for
// the feed it points to, and that is fetched in turn. Nothing here touches WebView2, so it lives in Core.
public sealed class LiveFetch : IDisposable
{
    /// The Mac's timeoutIntervalForRequest (nothing come for this long) and timeoutIntervalForResource (in all).
    public static readonly TimeSpan Idle = TimeSpan.FromSeconds(15), Whole = TimeSpan.FromSeconds(30);
    /// As many redirects as URLSession follows.
    public const int MostRedirects = 16;
    public const string Accept = "application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.9, text/html;q=0.5, */*;q=0.1";

    private readonly HttpClient _client;
    private readonly TimeSpan _idle, _whole;

    /// The timeouts are the Mac's unless a test asks for shorter ones.
    public LiveFetch(TimeSpan? idle = null, TimeSpan? whole = null)
    {
        _idle = idle ?? Idle;
        _whole = whole ?? Whole;
        var handler = new SocketsHttpHandler
        {
            UseCookies = false, AllowAutoRedirect = false, Credentials = null, PreAuthenticate = false, DefaultProxyCredentials = null,
            AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = _idle, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.Accept.ParseAdd(Accept);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Search");
    }

    public void Dispose() => _client.Dispose();

    /// The feed at `url`, or, for a page, the feed the page points to (then Read.Feed is that one's address).
    public async Task<LiveFeed.Fetched> Load(string url, CancellationToken cancel = default)
    {
        var (data, problem) = await Fetch(url, cancel).ConfigureAwait(false);
        if (data is null) return LiveFeed.Fetched.Failed(problem ?? LiveFeed.Problem.Network);
        if (LiveFeed.Parse(data, url) is { } read) return LiveFeed.Fetched.Ok(read);
        if (LiveFeed.Discover(data, url) is not { } found || found == LiveFeed.Web(url)?.AbsoluteUri) return LiveFeed.Fetched.Failed(LiveFeed.Problem.NotAFeed);
        (data, problem) = await Fetch(found, cancel).ConfigureAwait(false);
        if (data is null) return LiveFeed.Fetched.Failed(problem ?? LiveFeed.Problem.Network);
        return LiveFeed.Parse(data, found) is { } feed ? LiveFeed.Fetched.Ok(feed) : LiveFeed.Fetched.Failed(LiveFeed.Problem.NotAFeed);
    }

    /// What `url` sent, at most MaxBytes; anything but a 2xx in the end is Network.
    internal async Task<(byte[]? Data, LiveFeed.Problem? Problem)> Fetch(string url, CancellationToken cancel)
    {
        if (LiveFeed.Web(url) is not { } uri) return (null, LiveFeed.Problem.NotAFeed);
        using var whole = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        whole.CancelAfter(_whole);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(whole.Token);
        try
        {
            for (var hops = 0; ; hops++)
            {
                idle.CancelAfter(_idle);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hops >= MostRedirects || !Uri.TryCreate(uri, location, out var next) || LiveFeed.Web(next.AbsoluteUri) is not { } web) return (null, LiveFeed.Problem.Network);
                    uri = web;
                    continue;
                }
                if (!response.IsSuccessStatusCode) return (null, LiveFeed.Problem.Network);
                if (response.Content.Headers.ContentLength > LiveFeed.MaxBytes) return (null, LiveFeed.Problem.TooBig);
                await using var stream = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                using var data = new MemoryStream();
                var chunk = new byte[64 * 1024];
                while (true)
                {
                    idle.CancelAfter(_idle);
                    var read = await stream.ReadAsync(chunk, idle.Token).ConfigureAwait(false);
                    if (read == 0) return (data.ToArray(), null);
                    data.Write(chunk, 0, read);
                    if (data.Length > LiveFeed.MaxBytes) return (null, LiveFeed.Problem.TooBig);
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException or NotSupportedException or UriFormatException)
        {
            return (null, LiveFeed.Problem.Network);
        }
    }
}
