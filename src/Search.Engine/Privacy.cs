using Microsoft.Web.WebView2.Core;

namespace Search.Engine;

// What WebView2 does on its own that the Mac app never did, turned off for every page (Privacy.swift's counterpart).
public static class Privacy
{
    public static void Apply(CoreWebView2Settings settings)
    {
        settings.IsReputationCheckingRequired = false; // SmartScreen: every address sent to Microsoft
        settings.IsPasswordAutosaveEnabled = false;    // Edge's own password store; ours lives in the vault
        settings.IsGeneralAutofillEnabled = false;     // Edge's own form autofill; ours offers under the field
        // The browser's keys are the app's, not WebView2's: left on, Ctrl+S opened Save As, Ctrl+F its own find bar,
        // and those keys never reached the window. The window handles print, zoom and the rest itself.
        settings.AreBrowserAcceleratorKeysEnabled = Search.Core.Store.Testing && Environment.GetEnvironmentVariable("SEARCH_BROWSER_KEYS") == "1";
    }
}
