using System.ComponentModel;
using Search.Core;
using Search.Engine;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Search.App;

// Capture (Snap.swift; Settings › Capture pages, off until turned on): a picture of part of the page, as Arc's Capture
// takes one. Ctrl+Shift+X raises a light layer over the page (Snap.cs): drag across an area, click an element, or
// press Whole Page; Esc or the key again takes nothing. The picture goes on the clipboard, kept out of the cloud
// clipboard (and, from a private tab, out of the clipboard's history and away from clipboard managers), and into the
// space's downloads folder as a PNG. Nothing is sent anywhere.
public sealed partial class MainWindow
{
    private Snap? _snapper;
    private Snap Snapper => _snapper ??= new Snap(_pages);
    /// The last picture taken, for the bench: where it went and how big it is.
    internal (string? Path, int Width, int Height, bool Cropped, string? Notice)? LastCapture { get; private set; }

    private void WireCapture()
    {
        _browser.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Browser.Selected)) _snapper?.Stop(); };
        Hook<PropertyChangedEventHandler>(h => _prefs.PropertyChanged += h, h => _prefs.PropertyChanged -= h, (_, e) => { if (e.PropertyName == nameof(Prefs.Capture) && !_prefs.Capture) _snapper?.Stop(); });
    }

    /// Ctrl+Shift+X: the layer up over the tab in front, or, up already, put away.
    private async Task ToggleCapture()
    {
        if (!_prefs.Capture) return;
        if (Snapper.Stop()) return;
        if (_browser.Selected is not { Url: { Scheme: "http" or "https" or "file" } } tab) return;
        StopPicking();
        await _pages.Ensure(tab);
        var result = await Snapper.Take(tab);
        switch (result.End)
        {
            case SnapEnd.Taken when result.Png is { } png: KeepCapture(tab, png, result); break;
            case SnapEnd.NothingThere or SnapEnd.Failed: LastCapture = (null, 0, 0, false, result.Notice); Notice(result.Notice!); break;
        }
    }

    private void KeepCapture(Tab tab, byte[] png, SnapResult result)
    {
        var folder = Store.Testing ? Store.File("Downloads") : _spaces.DownloadsFolder(_spaces.All.First(s => s.Id == _space), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        string? saved = null;
        try
        {
            Directory.CreateDirectory(folder);
            var keep = SnapMath.Keep(DateTime.Now, folder, tab.Private);
            File.WriteAllBytes(keep.Path, png);
            saved = keep.Path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        // A test run stays off the real clipboard: it is the person's, not the test's.
        if (!Store.Testing) Copy(png, tab.Private);
        var notice = saved is null ? "Copied, but couldn't save it" : SnapMath.Notice(result.Cropped, Path.GetFileName(folder));
        LastCapture = (saved, result.Width, result.Height, result.Cropped, notice);
        Notice(notice);
    }

    private static void Copy(byte[] png, bool @private)
    {
        var package = new DataPackage();
        package.SetData("PNG", Stream(png));
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(Stream(png)));
        // Marks Windows' clipboard history and clipboard managers read; the cloud clipboard is left out always.
        foreach (var (format, data) in SnapMath.Marks(@private)) package.SetData(format, Stream(data));
        try { Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsRoamable = false, IsAllowedInHistory = !@private }); Clipboard.Flush(); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
    }

    private static InMemoryRandomAccessStream Stream(byte[] data)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(data); writer.StoreAsync().AsTask().Wait(); writer.DetachStream(); }
        stream.Seek(0);
        return stream;
    }
}
