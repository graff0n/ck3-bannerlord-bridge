using CK3xBL.Core.Localization;
using CK3xBL.Ux.Interop;
using static CK3xBL.Ux.Interop.Native;

namespace CK3xBL.Ux;

/// <summary>
/// Checks CK3's window mode before a battle. The switch to Bannerlord is seamless only when CK3 covers
/// its monitor: a windowed CK3 shows its frame and desktop around the cards. Policy by <c>mode</c>:
///  - <see cref="ModeNotice"/> (default, also for an unknown value): tell the player once per CK3 process;
///  - <see cref="ModeBorderless"/>: strip CK3's frame and fit it to its monitor (<c>makeBorderless</c>, i.e.
///    <see cref="TransitionUx.Stager"/>'s <see cref="WindowStager.EnsureBorderless"/>, restored when the UX is disposed); tell the
///    player only if that did not stick;
///  - <see cref="ModeIgnore"/>: nothing.
/// A fullscreen-like CK3 is never touched. <see cref="WindowStager.IsBorderlessAt"/> is not used to classify: its frame styles
/// include <c>WS_MINIMIZEBOX</c>, which CK3's own fullscreen window has (style <c>0x96020000</c>).
/// Thread-safe; never throws (a failing probe, restyle or notify is logged and skipped).
/// </summary>
/// <param name="probe">Style, window rect and monitor rect of a window; null when it is gone or cannot be judged (minimized).
/// <see cref="Win32Probe"/> in the bridge.</param>
/// <param name="notify">Shows the player a notice (<see cref="WindowedNotice"/>).</param>
/// <param name="makeBorderless">Makes the window borderless over the given monitor rect; true when that stuck.</param>
/// <param name="mode"><see cref="ModeNotice"/>, <see cref="ModeBorderless"/> or <see cref="ModeIgnore"/> (case-insensitive).</param>
public sealed class Ck3WindowModeGuard(
    Func<IntPtr, (long Style, ScreenRect Window, ScreenRect Monitor)?> probe,
    Action<string> notify,
    Func<IntPtr, ScreenRect, bool> makeBorderless,
    string mode)
{
    public const string ModeNotice = "notice", ModeBorderless = "borderless", ModeIgnore = "ignore";

    /// <summary>What the player is told about a windowed CK3, in English ("notice.window_mode.text" in the current language).</summary>
    public const string WindowedNotice =
        "Crusader Kings III is in windowed mode. For a seamless switch to Bannerlord, set Window Mode to Fullscreen in its video options.";

    private readonly HashSet<int> _notified = new();   // CK3 pids already told

    /// <summary>The policy in force: <paramref name="mode"/> normalised, an unknown or empty value read as <see cref="ModeNotice"/>.</summary>
    public string Mode { get; } = Normalise(mode);

    /// <summary>
    /// True when a window with this style and rect looks like a fullscreen game: no caption and no sizing frame, covering its
    /// monitor exactly. Other style bits (popup, minimize box, ...) do not matter.
    /// </summary>
    public static bool IsFullscreenLike(long style, ScreenRect window, ScreenRect monitor) =>
        (style & (WS_CAPTION | WS_THICKFRAME)) == 0 && !monitor.IsEmpty && window == monitor;

    /// <summary>Applies the policy to CK3's window <paramref name="hwnd"/> of process <paramref name="ck3Pid"/>.</summary>
    public void Check(int ck3Pid, IntPtr hwnd)
    {
        if (Mode == ModeIgnore) return;
        try
        {
            if (probe(hwnd) is not { } facts) return;
            var (style, window, monitor) = facts;
            if (IsFullscreenLike(style, window, monitor)) return;
            UxLog.Write($"CK3 window mode: windowed (pid {ck3Pid}, style 0x{style & 0xFFFFFFFF:X8}, window {window}, monitor {monitor}); policy '{Mode}'");
            if (Mode == ModeBorderless && makeBorderless(hwnd, monitor)) return;
            lock (_notified)
            {
                if (!_notified.Add(ck3Pid)) return;
            }
            notify(Loc.T("notice.window_mode.text"));   // WindowedNotice in English
        }
        catch (Exception e) { UxLog.Write($"CK3 window mode check failed (the battle goes on): {e.Message}"); }
    }

    /// <summary>
    /// Reads a window's style, window rect and the full rect of its monitor in physical pixels. Null when the window is gone, or
    /// minimized (its rect is then parked off screen and says nothing about its mode).
    /// </summary>
    public static (long Style, ScreenRect Window, ScreenRect Monitor)? Win32Probe(IntPtr hwnd)
    {
        using var _ = DpiScope.PerMonitorV2();
        if (hwnd == 0 || !IsWindow(hwnd) || IsIconic(hwnd) || !GetWindowRect(hwnd, out var rect)) return null;
        return (Style(hwnd), ScreenRect.From(rect), GameWindows.MonitorRectOf(hwnd));
    }

    private static string Normalise(string? mode)
    {
        string value = mode?.Trim().ToLowerInvariant() ?? "";
        if (value is ModeNotice or ModeBorderless or ModeIgnore) return value;
        UxLog.Write($"CK3 window mode policy '{mode}' is unknown; using '{ModeNotice}'");
        return ModeNotice;
    }
}
