using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Search.Core;

namespace Search.App;

/// One line in a panel: a place you've been, a download, a bookmark. What a click does is the panel's business; the
/// button at the end is the line's own action (forget, cancel, show in folder).
public class PanelRow : Observable
{
    private string _title = "", _detail = "", _actionGlyph = "", _actionName = "";
    private double _progress;
    private bool _progressShown, _indeterminate;

    public string Glyph { get; init; } = "";
    public object? Item { get; init; }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public Visibility ProgressShown => _progressShown ? Visibility.Visible : Visibility.Collapsed;
    public bool Indeterminate { get => _indeterminate; set => Set(ref _indeterminate, value); }
    public string ActionGlyph { get => _actionGlyph; set { if (Set(ref _actionGlyph, value)) Changed(nameof(ActionShown)); } }
    public string ActionName { get => _actionName; set => Set(ref _actionName, value); }
    public Visibility ActionShown => _actionGlyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    protected void ShowProgress(bool on) { if (_progressShown == on) return; _progressShown = on; Changed(nameof(ProgressShown)); }
}

/// A download in this session (Fetching.swift): a line that follows its operation, from first byte to done.
public sealed class Fetch : PanelRow
{
    public CoreWebView2DownloadOperation Operation { get; }
    public string Path => Operation.ResultFilePath;
    public CoreWebView2DownloadState State => Operation.State;

    public Fetch(CoreWebView2DownloadOperation operation)
    {
        Operation = operation;
        Title = System.IO.Path.GetFileName(operation.ResultFilePath);
        operation.BytesReceivedChanged += (_, _) => Refresh();
        operation.StateChanged += (_, _) => Refresh();
        operation.EstimatedEndTimeChanged += (_, _) => Refresh();
        Refresh();
    }

    public void Refresh()
    {
        var total = Operation.TotalBytesToReceive is { } t and > 0 ? (long)t : 0;
        var received = Operation.BytesReceived;
        Title = System.IO.Path.GetFileName(Operation.ResultFilePath);
        switch (Operation.State)
        {
            case CoreWebView2DownloadState.InProgress:
                ShowProgress(true);
                Indeterminate = total == 0;
                Progress = total > 0 ? 100.0 * received / total : 0;
                Detail = total > 0 ? $"{Size(received)} of {Size(total)}" : Size(received);
                (ActionGlyph, ActionName) = ("", "Cancel");
                break;
            case CoreWebView2DownloadState.Completed:
                ShowProgress(false);
                Detail = $"{Size(received)} · {Operation.Uri}";
                (ActionGlyph, ActionName) = ("", "Show in folder");
                break;
            default:
                ShowProgress(false);
                Detail = Operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled ? "Cancelled" : "Failed · " + Operation.InterruptReason;
                (ActionGlyph, ActionName) = ("", "");
                break;
        }
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}
