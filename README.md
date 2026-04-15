# WebView2 .NET SDK: Misleading `NotImplementedException` for STA Threading Violation

## The Bug

When you access WebView2 COM objects (e.g., cookie properties) from a **non-UI thread** for the **first time**, the .NET SDK throws:

```
System.NotImplementedException:
  Unable to cast to ICoreWebView2Cookie.
  This may happen if you are using an interface not supported by the
  version of the WebView2 Runtime you are using...
```

This message is **completely wrong**. The real cause is an **STA threading violation**.

Interestingly, if the same property was previously accessed on the UI thread (caching the native COM pointer), a subsequent background-thread access **correctly** throws `InvalidOperationException` with "can only be accessed from the UI thread."

Same root cause, two different exceptions — depending on whether the native pointer was cached.

## Root Cause

The generated wrapper code (from `type.stg` template) has a lazy-init pattern:

```csharp
internal ICoreWebView2Cookie _nativeICoreWebView2Cookie
{
    get
    {
        if (_nativeICoreWebView2CookieValue == null)
        {
            try
            {
                // COM QueryInterface — fails with E_NOINTERFACE on wrong thread
                _nativeICoreWebView2CookieValue = (ICoreWebView2Cookie)_rawNative;
            }
            catch (Exception exception)  // ← catches EVERYTHING as "version mismatch"
            {
                throw new NotImplementedException("Unable to cast... version mismatch...");
            }
        }
        return _nativeICoreWebView2CookieValue;
    }
}
```

When `_nativeValue` is null (first access) and the call comes from a background thread:
1. COM `QueryInterface` fails → `E_NOINTERFACE` (can't marshal across STA apartments)
2. CLR translates to `InvalidCastException`
3. SDK catches **all** exceptions → wraps as `NotImplementedException` + "version mismatch"

When `_nativeValue` is already cached (prior UI-thread access):
- SDK's `VerifyAccess()` correctly detects the wrong thread → `InvalidOperationException`

## Reproduce

```
dotnet run
```

Output:

```
Test A: First access of cookie from background thread
  (native COM pointer NOT yet cached)
  Exception: NotImplementedException
  Message:   Unable to cast to Microsoft.Web.WebView2.Core.Raw.ICoreWebView2Cookie...

Test B: Access cookie from background thread
  (native COM pointer already cached from UI-thread access)
  Exception: InvalidOperationException
  Message:   CoreWebView2Cookie members can only be accessed from the UI thread.

=== Result ===
BUG CONFIRMED:
  Test A: NotImplementedException (WRONG - misleading 'version mismatch')
  Test B: InvalidOperationException (CORRECT - 'UI thread' message)

  Same operation, same root cause (STA violation), different exceptions.
  The only difference is whether the native COM pointer was cached.
```

## Fix

In `type.stg` (`NativeProperty` template), add a specific catch **before** the generic one:

```csharp
catch (InvalidCastException exception) when (exception.HResult == -2147467262 /* E_NOINTERFACE */
    && System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
{
    throw new InvalidOperationException(
      "Unable to cast to " + typeof(<interface>).Name + ". " +
      "This is likely a threading issue: WebView2 COM objects must be " +
      "accessed from the UI thread (STA).", exception);
}
```

## Related Bugs

- **AB#58195804** — Error message/code not straightforward (P2, Approved)
- **AB#37255788** — Cookie cast failure (P3, open 4.5 years)

## Environment

- WebView2 SDK: `1.0.3912.50`
- .NET 10
- Windows 11
