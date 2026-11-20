using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace Harness;

public sealed record Options(string Scenario, string Out, string Data, int Count, string Mode, int Timeout, string[] Raw)
{
    public static Options Parse(string[] args)
    {
        string Get(string name, string fallback) { var i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var scenario = Get("scenario", "startup");
        return new Options(scenario, Get("out", Path.Combine(Path.GetTempPath(), $"spike-{scenario}.json")),
            Get("data", Path.Combine(Path.GetTempPath(), "search-spike", scenario)), int.Parse(Get("count", "10")), Get("mode", "none"), int.Parse(Get("timeout", "120")), args.Skip(1).ToArray());
    }
}

public sealed record Context(Options Opts, Window Window, LocalServer Server, Dictionary<string, object?> Result);

/// Milliseconds since the process started, so "launched" includes the runtime's own start-up.
public static class Clock
{
    private static readonly DateTime _start = Process.GetCurrentProcess().StartTime;
    public static Dictionary<string, double> Marks { get; } = new();
    public static double Ms => Math.Round((DateTime.Now - _start).TotalMilliseconds, 1);
    public static double Mark(string name) => Marks[name] = Ms;
}

public static class Memory
{
    public static object Measure(CoreWebView2Environment env)
    {
        long priv = 0, ws = 0;
        var kinds = new Dictionary<string, int>();
        var infos = env.GetProcessInfos();
        foreach (var info in infos)
        {
            kinds[info.Kind.ToString()] = kinds.GetValueOrDefault(info.Kind.ToString()) + 1;
            try { using var p = Process.GetProcessById(info.ProcessId); priv += p.PrivateMemorySize64; ws += p.WorkingSet64; } catch (ArgumentException) { }
        }
        using var own = Process.GetCurrentProcess();
        return new { appPrivateMB = own.PrivateMemorySize64 >> 20, appWorkingSetMB = own.WorkingSet64 >> 20, webViewPrivateMB = priv >> 20, webViewWorkingSetMB = ws >> 20, processes = infos.Count, kinds };
    }
}

/// A tiny HTTP/1.1 server on 127.0.0.1. Every *.test host is mapped to it with --host-resolver-rules, so pages
/// get real hosts (ads.test, app.test, probe.test) and real network requests, as blocking and extensions see them.
public sealed class LocalServer : IDisposable
{
    private static readonly byte[] _gif = Convert.FromBase64String("R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==");
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    public Dictionary<string, string> Pages { get; } = new();
    public int Requests;
    public System.Collections.Concurrent.ConcurrentDictionary<string, int> Hosts { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string HostRules => "MAP *.test 127.0.0.1";

    public LocalServer() { _listener.Start(); _ = Accept(); }

    public string Url(string host, string path) => $"http://{host}:{Port}/{path.TrimStart('/')}";

    private async Task Accept()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var head = new StringBuilder();
            while (!head.ToString().Contains("\r\n\r\n")) { var n = await stream.ReadAsync(buffer); if (n == 0) return; head.Append(Encoding.ASCII.GetString(buffer, 0, n)); }
            Interlocked.Increment(ref Requests);
            var text = head.ToString();
            var host = text.Split("\r\n").FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim().Split(':')[0] ?? "?";
            Hosts.AddOrUpdate(host, 1, (_, n) => n + 1);
            var path = text.Split(' ')[1].Split('?')[0];
            var (status, type, body) = path.StartsWith("/img") ? ("200 OK", "image/gif", _gif)
                : Pages.TryGetValue(path, out var html) ? ("200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html))
                : ("404 Not Found", "text/plain", "not found"u8.ToArray());
            var header = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
            await stream.WriteAsync(body);
        }
        catch (IOException) { }
    }

    public void Dispose() { _stop.Cancel(); _listener.Stop(); }
}

public static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool ClientToScreen(nint hwnd, ref Point p);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] public static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    public delegate bool EnumProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, nint lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint hwnd, StringBuilder text, int max);

    public static string Colour(uint bgr) => $"#{bgr & 0xFF:X2}{(bgr >> 8) & 0xFF:X2}{(bgr >> 16) & 0xFF:X2}";

    public static List<object> TopWindows(HashSet<uint> pids)
    {
        var found = new List<object>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pids.Contains(pid) && IsWindowVisible(h))
            {
                var title = new StringBuilder(256); var cls = new StringBuilder(256);
                GetWindowText(h, title, 256); GetClassName(h, cls, 256);
                found.Add(new { pid, title = title.ToString(), cls = cls.ToString() });
            }
            return true;
        }, 0);
        return found;
    }
}
