using Microsoft.Web.WebView2.Core;
using Search.Engine;

namespace Search.Engine.Tests;

/// Try HTTPS first: which failures of the https:// try send the page back to the http:// address it was asked for.
public class HttpsFallbackTests
{
    [Fact]
    public void NoConnectionOrNoCertificateFallsBack()
    {
        foreach (var status in new[] { CoreWebView2WebErrorStatus.CannotConnect, CoreWebView2WebErrorStatus.ConnectionReset, CoreWebView2WebErrorStatus.ConnectionAborted, CoreWebView2WebErrorStatus.Disconnected,
                     CoreWebView2WebErrorStatus.Timeout, CoreWebView2WebErrorStatus.ServerUnreachable, CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse,
                     CoreWebView2WebErrorStatus.CertificateIsInvalid, CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect, CoreWebView2WebErrorStatus.CertificateExpired,
                     CoreWebView2WebErrorStatus.CertificateRevoked, CoreWebView2WebErrorStatus.ClientCertificateContainsErrors })
            Assert.True(Pages.FallsBackToHttp(status), status.ToString());
    }

    /// Stopped, sent elsewhere, or answered by the site: the try is the page, and nothing is loaded again.
    [Fact]
    public void AnythingElseStays()
    {
        foreach (var status in new[] { CoreWebView2WebErrorStatus.OperationCanceled, CoreWebView2WebErrorStatus.Unknown, CoreWebView2WebErrorStatus.RedirectFailed, CoreWebView2WebErrorStatus.HostNameNotResolved,
                     CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired, CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired, CoreWebView2WebErrorStatus.UnexpectedError })
            Assert.False(Pages.FallsBackToHttp(status), status.ToString());
    }
}
