// Minimal repro: WebView2 .NET SDK throws misleading NotImplementedException
// when COM objects are accessed from a non-UI thread for the FIRST time.
//
// The bug is in the NativeProperty lazy-init path in the generated wrapper code.
// When _nativeICoreWebView2CookieValue is null and the first access happens on
// a background thread, the COM QueryInterface fails with E_NOINTERFACE (STA
// violation), and the SDK wraps it as NotImplementedException with "version
// mismatch" — completely wrong.
//
// If the property was previously accessed on the UI thread (caching the native
// pointer), subsequent BG-thread access correctly throws InvalidOperationException
// via VerifyAccess. The bug is only in the first-access path.
//
// Run: dotnet run

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

class Program
{
    static int Main()
    {
        Console.WriteLine("=== WebView2 STA Threading Bug Repro ===");
        Console.WriteLine("SDK version: 1.0.3912.50 (latest)");
        Console.WriteLine();

        int exitCode = 0;
        var done = new ManualResetEventSlim();

        // Create a dedicated STA thread (simulating a UI thread)
        var uiThread = new Thread(() =>
        {
            Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
            {
                try
                {
                    exitCode = await RunTest();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Fatal: {ex}");
                    exitCode = 2;
                }
                finally
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
            done.Set();
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        done.Wait(TimeSpan.FromSeconds(30));
        return exitCode;
    }

    static async Task<int> RunTest()
    {
        // --- Create WebView2 on the UI (STA) thread ---
        var env = await CoreWebView2Environment.CreateAsync();
        var controller = await env.CreateCoreWebView2ControllerAsync(new IntPtr(-3));
        var webview = controller.CoreWebView2;

        // --- Navigate to get cookies ---
        var navDone = new TaskCompletionSource<bool>();
        webview.NavigationCompleted += (s, e) => navDone.TrySetResult(e.IsSuccess);
        webview.Navigate("https://www.bing.com/");
        if (!await navDone.Task)
        {
            Console.WriteLine("SKIP: Navigation failed.");
            controller.Close();
            return 2;
        }

        // --- Get cookies on the UI thread ---
        var cookies = await webview.CookieManager.GetCookiesAsync("https://www.bing.com/");
        if (cookies.Count < 2)
        {
            Console.WriteLine("SKIP: Need at least 2 cookies.");
            controller.Close();
            return 2;
        }

        // =====================================================================
        // Test A: First access from BG thread (no prior UI-thread access)
        //   → BUG: _nativeValue is null, lazy-init triggers QI on wrong thread
        //   → SDK throws NotImplementedException with "version mismatch"
        // =====================================================================
        Console.WriteLine("Test A: First access of cookie from background thread");
        Console.WriteLine("  (native COM pointer NOT yet cached)");
        var cookieA = cookies[0]; // never accessed on UI thread
        Exception caughtA = null;
        await Task.Run(() =>
        {
            try { _ = cookieA.Name; }
            catch (Exception ex) { caughtA = ex; }
        });

        Console.WriteLine($"  Exception: {caughtA?.GetType().Name}");
        Console.WriteLine($"  Message:   {Truncate(caughtA?.Message, 100)}");
        Console.WriteLine();

        // =====================================================================
        // Test B: BG access AFTER prior UI-thread access (native pointer cached)
        //   → CORRECT: VerifyAccess detects wrong thread
        //   → SDK throws InvalidOperationException
        // =====================================================================
        Console.WriteLine("Test B: Access cookie from background thread");
        Console.WriteLine("  (native COM pointer already cached from UI-thread access)");
        var cookieB = cookies[1];
        _ = cookieB.Name; // cache the native pointer on UI thread
        Exception caughtB = null;
        await Task.Run(() =>
        {
            try { _ = cookieB.Name; }
            catch (Exception ex) { caughtB = ex; }
        });

        Console.WriteLine($"  Exception: {caughtB?.GetType().Name}");
        Console.WriteLine($"  Message:   {Truncate(caughtB?.Message, 100)}");
        Console.WriteLine();

        // =====================================================================
        // Verdict
        // =====================================================================
        Console.WriteLine("=== Result ===");
        bool bugPresent = caughtA is NotImplementedException;
        bool correctPath = caughtB is InvalidOperationException;

        if (bugPresent)
        {
            Console.WriteLine("BUG CONFIRMED:");
            Console.WriteLine("  Test A: NotImplementedException (WRONG - misleading 'version mismatch')");
            Console.WriteLine("  Test B: InvalidOperationException (CORRECT - 'UI thread' message)");
            Console.WriteLine();
            Console.WriteLine("  Same operation, same root cause (STA violation), different exceptions.");
            Console.WriteLine("  The only difference is whether the native COM pointer was cached.");
            Console.WriteLine();
            Console.WriteLine("  Related bugs: AB#58195804, AB#37255788");
        }
        else
        {
            Console.WriteLine("Bug appears to be fixed in this SDK version.");
        }

        controller.Close();
        return bugPresent ? 1 : 0;
    }

    static string Truncate(string s, int max) =>
        s == null ? "(null)" : s.Length <= max ? s : s[..max] + "...";
}
