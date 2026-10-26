using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using CK3xBL.Core.Abstractions;
using CK3xBL.Ux.Audio;
using CK3xBL.Ux.Interop;
using CK3xBL.Ux.Overlay;
using CK3xBL.Ux.Rendering;

namespace CK3xBL.Ux;

/// <summary>
/// Everything the player sees and hears between the two games (docs/HOW_IT_WORKS.md, "The switch
/// between the games"):
///  - title card: freeze-frame CK3 (an invisible cut), darken into a CK3-styled card ("The Battle of X", CoAs), hold, then
///    dissolve it straight into the battle Bannerlord reveals (<see cref="RevealAsync"/>, PROTOCOL §13.6);
///  - result card: "Victory at X" / "Defeat at X" with the summary, while focus moves back to CK3;
///  - the overlay is topmost, layered, click-through and never activates; it is hidden (not just transparent) afterwards so
///    the game regains independent flip, and it removes itself if the player switches to another application;
///  - foreground ladder, per-process audio ramps with the user's level remembered (and repaired after a crash),
///    borderless staging (<see cref="Stager"/>).
/// Methods are safe to call from any thread. UX failures are logged and degrade gracefully: a missing card never stops a battle.
/// </summary>
public sealed class TransitionUx : ITransitionUx
{
    /// <summary>Executable names (without .exe) that are always treated as the games.</summary>
    private static readonly string[] GameProcessNames = ["ck3", "Bannerlord", "Bannerlord.Native"];

    /// <summary>Fade-out of <see cref="HideAsync"/> (milliseconds).</summary>
    private const double HideFadeMs = 500;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, long> _registered = new();   // registered game pid -> its start time (pids get reused)
    private UxOptions _options = new();
    private OverlayThread? _ui;
    private OverlayController? _overlay;
    private ProcessVolumeController? _audio;
    private Lazy<CardFonts>? _fonts;
    private long _requestSeq;
    private nint _stageWindow;          // last game window seen: decides the monitor when a card names none
    private bool _initialized, _disposed;
    private volatile bool _titleCardPresented;
    private EventHandler? _onProcessExit;
    private UnhandledExceptionEventHandler? _onUnhandled;

    private readonly string _audioStorePath;
    private readonly Func<nint> _foregroundWindow;

    public TransitionUx() : this(ProcessVolumeController.DefaultStorePath, Native.GetForegroundWindow) { }

    /// <summary>
    /// Self-test constructor: keeps audio crash-recovery state in <paramref name="audioStorePath"/> and lets the overlay see
    /// <paramref name="foregroundWindow"/> as the foreground window (it never presents a card over a foreign application).
    /// </summary>
    internal TransitionUx(string audioStorePath, Func<nint> foregroundWindow)
    {
        _audioStorePath = audioStorePath;
        _foregroundWindow = foregroundWindow;
    }

    /// <summary>Borderless staging of the game windows; restored on <see cref="Dispose"/> and at process exit.</summary>
    public WindowStager Stager { get; } = new();

    /// <summary>Longest a fully shown card may stay up before it removes itself (safety valve). Default 2 minutes.</summary>
    public TimeSpan MaxCardHold
    {
        get => _overlay?.MaxHold ?? TimeSpan.FromMinutes(2);
        set { EnsureInitialized(); _overlay!.MaxHold = value; }
    }

    /// <summary>
    /// Raised when the overlay removed a card by itself or declined to show one (the player switched to, or already was in,
    /// another application; or a card was held longer than <see cref="MaxCardHold"/>). The card's show task still completes
    /// normally. Raised on the overlay thread; handlers must be quick.
    /// </summary>
    public event Action<string>? OverlayAutoHidden;

    /// <summary>The card typeface (loaded on first use; never fails, it falls back to a system serif). Valid until <see cref="Dispose"/>.</summary>
    public CardFonts Fonts { get { EnsureInitialized(); return _fonts!.Value; } }

    public void Initialize(UxOptions options)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized) { UxLog.Write("Initialize called twice; keeping the first configuration"); return; }
            _options = options ?? new UxOptions();
            // CardFonts.Load never throws, so the Lazy can never cache a failure that would disable every card.
            _fonts = new Lazy<CardFonts>(() =>
            {
                var fonts = CardFonts.Load(string.IsNullOrWhiteSpace(_options.FontFamily) ? "Fondamento" : _options.FontFamily, _options.CK3GameDir);
                UxLog.Write($"card font: {fonts.Family.Name} (italic {fonts.HasItalic}) from {fonts.Source}");
                return fonts;
            }, LazyThreadSafetyMode.ExecutionAndPublication);

            _audio = new ProcessVolumeController(_audioStorePath);
            _audio.RecoverFromPreviousRun();

            _ui = new OverlayThread();
            _overlay = _ui.InvokeAsync(() => new OverlayController(_ui, () => Interlocked.Read(ref _requestSeq), IsGameProcess, _foregroundWindow))
                .GetAwaiter().GetResult();
            _overlay.AutoHidden += reason => OverlayAutoHidden?.Invoke(reason);

            _onProcessExit = (_, _) => EmergencyRestore("process exit");
            _onUnhandled = (_, _) => EmergencyRestore("unhandled exception");
            AppDomain.CurrentDomain.ProcessExit += _onProcessExit;
            AppDomain.CurrentDomain.UnhandledException += _onUnhandled;
            _initialized = true;
        }
        // Warm-ups off the caller's thread: font loading, and one PrintWindow of CK3 (the first capture is ~10x slower).
        _ = Task.Run(() =>
        {
            try
            {
                _ = _fonts.Value;
                nint ck3 = GameWindows.FindByProcessName("ck3", GameWindows.Ck3WindowClass);
                if (ck3 != 0)
                {
                    RegisterWindow(ck3);
                    using var frame = FrameGrab.Capture(ck3);
                    UxLog.Write(frame != null ? $"freeze-frame warm-up: {frame.ScreenRect} in {frame.ElapsedMs} ms" : "freeze-frame warm-up: no frame");
                }
            }
            catch (Exception e) { UxLog.Write($"warm-up failed: {e.Message}"); }
        });
    }

    /// <summary>Number of overlay windows currently alive (diagnostics and tests).</summary>
    internal Task<int> OverlayWindowCountAsync() =>
        TryEnsureInitialized() ? _ui!.InvokeAsync(() => _overlay!.WindowCount) : Task.FromResult(0);

    /// <summary>Alpha of every overlay window alive (diagnostics and tests).</summary>
    internal Task<IReadOnlyList<byte>> OverlayWindowAlphasAsync() =>
        TryEnsureInitialized() ? _ui!.InvokeAsync(() => _overlay!.WindowAlphas) : Task.FromResult<IReadOnlyList<byte>>([]);

    /// <summary>
    /// Marks a process as one of the games (its windows may hold the foreground during transitions). The mark is tied to the
    /// process's start time, so a later process that reuses the pid is not trusted. A newly seen game also triggers a check of
    /// pending audio levels (a restarted game inherits the level Windows remembered for it).
    /// </summary>
    public void RegisterGameProcess(int processId)
    {
        if (processId <= 0 || processId == Environment.ProcessId) return;
        long start = StartTicksOf(processId);
        if (start == 0) return;   // gone already, or cannot be inspected: its name still decides (IsGameProcess)
        if (_registered.TryGetValue(processId, out long known) && known == start) return;
        _registered[processId] = start;
        _audio?.CheckSoon();
    }

    // ===================================================================================================== cards

    public async Task ShowTitleCardAsync(TitleCard card, CancellationToken ct)
    {
        _titleCardPresented = false;
        _titleCardPresented = await ShowCardAsync(card, CardKind.Title, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the last <see cref="ShowTitleCardAsync"/> put its card fully on screen. False before the first one, and when
    /// that card was declined (the player was in another application), superseded or dismissed before it was opaque, failed to
    /// render or upload, or was cancelled. The orchestrator then asks Bannerlord for its own title card (PROTOCOL §13.6
    /// <c>title_in_engine</c>), so a battle never opens without a title.
    /// </summary>
    public bool TitleCardPresented => _titleCardPresented;

    public Task ShowResultCardAsync(TitleCard card, CancellationToken ct) => ShowCardAsync(card, ResultKind(card), ct);

    /// <summary>The style of a result card: from its outcome (<see cref="TitleCard.Defeat"/>), else read from its English title.</summary>
    internal static CardKind ResultKind(TitleCard? card) =>
        card?.Defeat is { } defeat ? (defeat ? CardKind.Defeat : CardKind.Victory) : TitleCardRenderer.KindForResult(card?.Title);

    public async Task HideAsync(CancellationToken ct)
    {
        if (!TryEnsureInitialized()) return;
        await FadeOutAsync(Interlocked.Increment(ref _requestSeq), HideFadeMs, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The single dissolving title card (PROTOCOL §13.6): keeps the card fully up for <paramref name="hold"/>, then dissolves it
    /// over <paramref name="dissolve"/> into the battle Bannerlord is revealing beneath it.
    /// </summary>
    /// <remarks>
    /// Cancelling during the hold throws and leaves the card up (the caller decides what happens to it); cancelling during the
    /// dissolve throws and removes the card at once, as <see cref="HideAsync"/> does. With no hold this is
    /// <see cref="HideAsync"/> with the given fade. A card requested during the hold supersedes the reveal and is left alone.
    /// </remarks>
    public async Task RevealAsync(TimeSpan hold, TimeSpan dissolve, CancellationToken ct)
    {
        if (!TryEnsureInitialized()) return;
        long before = Interlocked.Read(ref _requestSeq);
        if (hold > TimeSpan.Zero) await Task.Delay(hold, ct).ConfigureAwait(false);
        long seq = before + 1;
        if (Interlocked.CompareExchange(ref _requestSeq, seq, before) != before)
        {
            UxLog.Write("reveal: a newer card request arrived during the hold; leaving the overlay to it");
            return;
        }
        await FadeOutAsync(seq, Math.Max(0, dissolve.TotalMilliseconds), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fades every card out over <paramref name="durationMs"/> as request <paramref name="seq"/>. Cancelling removes the cards at
    /// once (unless something newer has replaced them) and throws.
    /// </summary>
    private async Task FadeOutAsync(long seq, double durationMs, CancellationToken ct)
    {
        var hidden = await _ui!.InvokeAsync(() => _overlay!.HideAll(durationMs)).ConfigureAwait(false);
        try
        {
            await WaitOrCancelAsync(hidden, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled mid-fade: remove the card at once (unless something newer has replaced it).
            _ui.Post(() => { if (Interlocked.Read(ref _requestSeq) == seq) _overlay!.DestroyAll(); });
            throw;
        }
    }

    /// <summary>
    /// Shows a one-line hint under the current card (e.g. "Click to continue to the battle" when activation failed; clicks
    /// pass through the overlay to the game). Null or empty removes it.
    /// </summary>
    /// <remarks>
    /// Failure-safe like the cards: a hint that cannot be drawn is logged and skipped. Only cancellation of
    /// <paramref name="ct"/> surfaces (as <see cref="OperationCanceledException"/>).
    /// </remarks>
    public async Task ShowHintAsync(string? text, CancellationToken ct)
    {
        if (!TryEnsureInitialized()) return;
        DibSurface? strip = null;
        bool handedOver = false;
        try
        {
            if (string.IsNullOrWhiteSpace(text)) { await _ui!.InvokeAsync(() => _overlay!.HideHint()).ConfigureAwait(false); return; }
            var bounds = await _ui!.InvokeAsync(() => _overlay!.CurrentBounds).ConfigureAwait(false);
            if (bounds is not { } monitor) return;
            float scale = TitleCardRenderer.ScaleOf(monitor.Width, monitor.Height);
            int height = Math.Max(24, (int)(110 * scale));
            var stripRect = new ScreenRect(monitor.Left, monitor.Top + (int)(monitor.Height * 0.86f) - height / 2, monitor.Width, height);
            strip = await Task.Run(() => RenderHintStrip(text, stripRect, scale), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            // ShowHint owns the strip once it runs; if the overlay thread is gone it never runs and the strip is freed below.
            await _ui.InvokeAsync(() => { handedOver = true; _overlay!.ShowHint(strip, stripRect); }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { UxLog.Write($"hint '{text}' failed (the battle goes on without it): {e.Message}"); }
        finally
        {
            if (!handedOver) strip?.Dispose();
        }
    }

    private DibSurface RenderHintStrip(string text, ScreenRect rect, float scale)
    {
        var surface = DibSurface.Create(rect.Width, rect.Height);
        try
        {
            using (var view = surface.CreateView(PixelFormat.Format32bppPArgb))
                TitleCardRenderer.RenderHint(view, text, Fonts, scale);
            return surface;
        }
        catch
        {
            surface.Dispose();
            throw;
        }
    }

    /// <summary>Renders and presents a card. True when it ended up fully on screen.</summary>
    private async Task<bool> ShowCardAsync(TitleCard card, CardKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(card);
        if (!TryEnsureInitialized()) return false;
        long seq = Interlocked.Increment(ref _requestSeq);
        nint source = card.FreezeFrameFrom;
        if (source != 0 && GameWindows.IsAlive(source)) RegisterWindow(source);
        try
        {
            var (surfaces, fromFreezeFrame) = await PrepareCardAsync(seq, card, kind, source).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                surfaces.Dispose();                        // cancelled while rendering: never present it
                ct.ThrowIfCancellationRequested();
            }
            var timing = fromFreezeFrame ? CardTiming.FromFreezeFrame : CardTiming.FadeIn;
            // Darken into it; completes once the card is opaque (or superseded, dismissed or declined).
            var shown = await _ui!.InvokeAsync(() => _overlay!.Present(seq, surfaces, timing)).ConfigureAwait(false);
            await WaitOrCancelAsync(shown, ct).ConfigureAwait(false);
            bool presented = await shown.ConfigureAwait(false);
            if (!presented) UxLog.Write($"card ({kind}) '{card.Title}' was not shown fully (superseded, dismissed or declined)");
            return presented;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _ui?.Post(() => _overlay!.CancelShow(seq));   // the half-shown card leaves quickly
            throw;
        }
        catch (Exception e)
        {
            // Never let a cosmetic failure stop the battle flow, and never leave a half-built card (a lone freeze frame) up.
            UxLog.Write($"card ({kind}) failed: {e}");
            _ui?.Post(() => _overlay!.CancelShow(seq));
            return false;
        }
    }

    /// <summary>
    /// Captures and shows the freeze frame at once (an invisible cut), then renders both card layers for the stage monitor.
    /// The text layer does not depend on the frame, so it renders while the frame is being captured.
    /// </summary>
    private async Task<(CardSurfaces surfaces, bool fromFreezeFrame)> PrepareCardAsync(long seq, TitleCard card, CardKind kind, nint source)
    {
        // Geometry on the overlay thread (PMv2): the monitor of the named window, else of the last game window seen.
        var (monitor, overlayUp) = await _ui!.InvokeAsync(() =>
            (GameWindows.MonitorRectOf(StageWindowFor(source)), _overlay!.HasStage)).ConfigureAwait(false);
        var foregroundTask = Task.Run(() => RenderForeground(card, kind, monitor), CancellationToken.None);
        DibSurface? background = null;
        try
        {
            // No freeze frame when a card already covers the screen: CK3's frame is not what the player sees then.
            using var frame = source != 0 && !overlayUp ? await CaptureWithTimeoutAsync(source).ConfigureAwait(false) : null;
            if (frame != null)
            {
                UxLog.Write($"freeze frame: {frame.ScreenRect} in {frame.ElapsedMs} ms");
                await _ui.InvokeAsync(() => _overlay!.ShowFreeze(seq, frame)).ConfigureAwait(false);
            }
            background = await Task.Run(() => RenderBackground(kind, monitor, frame), CancellationToken.None).ConfigureAwait(false);
            var (foreground, layout) = await foregroundTask.ConfigureAwait(false);
            return (new CardSurfaces(background, foreground, monitor, layout), frame != null);
        }
        catch
        {
            background?.Dispose();
            _ = foregroundTask.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.surface.Dispose(); }, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>PrintWindow with a 600 ms budget: a slow capture is abandoned (and disposed when it finishes).</summary>
    private static async Task<CapturedFrame?> CaptureWithTimeoutAsync(nint hwnd)
    {
        var grab = Task.Run(() => FrameGrab.Capture(hwnd), CancellationToken.None);
        if (await Task.WhenAny(grab, Task.Delay(600, CancellationToken.None)).ConfigureAwait(false) == grab)
            return await grab.ConfigureAwait(false);
        UxLog.Write("freeze frame: PrintWindow took longer than 600 ms; fading in without it");
        _ = grab.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); }, TaskScheduler.Default);
        return null;
    }

    /// <summary>Awaits <paramref name="task"/>, or throws as soon as <paramref name="ct"/> is cancelled.</summary>
    private static async Task WaitOrCancelAsync(Task task, CancellationToken ct)
    {
        if (ct.CanBeCanceled)
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult()))
            {
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task) ct.ThrowIfCancellationRequested();
            }
        }
        await task.ConfigureAwait(false);
    }

    private static DibSurface RenderBackground(CardKind kind, ScreenRect monitor, CapturedFrame? frame)
    {
        var started = Stopwatch.StartNew();
        var surface = DibSurface.Create(monitor.Width, monitor.Height);
        try
        {
            using (var underlay = frame?.Surface.CreateView(PixelFormat.Format32bppRgb))
            using (var view = surface.CreateView(PixelFormat.Format32bppPArgb))
                TitleCardRenderer.RenderBackground(view, kind, underlay);
            UxLog.Write($"card background ({kind}) {monitor.Width}x{monitor.Height} in {started.ElapsedMilliseconds} ms");
            return surface;
        }
        catch
        {
            surface.Dispose();
            throw;
        }
    }

    private (DibSurface surface, CardLayout layout) RenderForeground(TitleCard card, CardKind kind, ScreenRect monitor)
    {
        var started = Stopwatch.StartNew();
        var surface = DibSurface.Create(monitor.Width, monitor.Height);
        try
        {
            CardLayout layout;
            using (var view = surface.CreateView(PixelFormat.Format32bppPArgb))
                layout = TitleCardRenderer.RenderForeground(view, card, kind, Fonts);
            UxLog.Write($"card text ({kind}) '{card.Title}' in {started.ElapsedMilliseconds} ms");
            return (surface, layout);
        }
        catch
        {
            surface.Dispose();
            throw;
        }
    }

    // ===================================================================================================== focus and audio

    public bool BringToFront(IntPtr hwnd)
    {
        if (hwnd == 0) return false;
        RegisterWindow(hwnd);
        return Foreground.Activate(hwnd, IsGameProcess);
    }

    public Task RampProcessVolumeAsync(int pid, float target, TimeSpan duration, CancellationToken ct)
    {
        if (!TryEnsureInitialized()) return Task.CompletedTask;
        RegisterGameProcess(pid);
        return _audio!.RampAsync(pid, target, duration, ct);
    }

    public Task RestoreProcessVolumeAsync(int pid, TimeSpan duration, CancellationToken ct)
    {
        if (!TryEnsureInitialized()) return Task.CompletedTask;
        return _audio!.RestoreAsync(pid, duration, ct);
    }

    // ===================================================================================================== lifetime

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_onProcessExit != null) AppDomain.CurrentDomain.ProcessExit -= _onProcessExit;
            if (_onUnhandled != null) AppDomain.CurrentDomain.UnhandledException -= _onUnhandled;
        }
        try
        {
            if (_ui is { IsAlive: true } ui && _overlay != null)
                ui.InvokeAsync(() => _overlay.Dispose()).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception e) { UxLog.Write($"dispose: overlay cleanup failed: {e.Message}"); }
        _ui?.Dispose();
        _audio?.RestoreAllNow();   // levels of games that already exited stay pending in audio.json for the next start
        _audio?.Dispose();
        Stager.RestoreAll();
        if (_fonts is { IsValueCreated: true }) _fonts.Value.Dispose();   // waits for a render still using them
    }

    /// <summary>Exit/crash path: put the user's world back (audio levels, window styles, no cursor clip). Idempotent.</summary>
    private void EmergencyRestore(string why)
    {
        try
        {
            UxLog.Write($"restoring audio and windows ({why})");
            _audio?.RestoreAllNow();
            Stager.RestoreAll();
            WindowStager.ReleaseCursorClip();
        }
        catch (Exception e) { UxLog.Write($"emergency restore failed: {e.Message}"); }
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        UxLog.Write("used before Initialize; initializing with default options");
        Initialize(new UxOptions());
    }

    private bool TryEnsureInitialized()
    {
        if (_disposed) return false;
        try { EnsureInitialized(); return _ui is { IsAlive: true }; }
        catch (Exception e) { UxLog.Write($"initialization failed: {e.Message}"); return false; }
    }

    private void RegisterWindow(nint hwnd)
    {
        int pid = GameWindows.ProcessIdOf(hwnd);
        if (pid == 0 || pid == Environment.ProcessId) return;
        RegisterGameProcess(pid);
        Interlocked.Exchange(ref _stageWindow, hwnd);
    }

    private nint StageWindowFor(nint source) =>
        source != 0 && GameWindows.IsAlive(source) ? source
        : GameWindows.IsAlive(Interlocked.CompareExchange(ref _stageWindow, 0, 0)) ? _stageWindow
        : 0;   // primary monitor

    /// <summary>
    /// True for registered game processes (the same process that was registered, not a later one reusing its pid) and for any
    /// process named like a game (Bannerlord can take the foreground on its own before the bridge has registered it). Nothing
    /// is cached by pid. Never throws: a process that cannot be inspected or exits meanwhile is not a game.
    /// </summary>
    public bool IsGameProcess(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (_registered.TryGetValue(pid, out long start))
            {
                if (start == StartTicks(p)) return true;
                _registered.TryRemove(new KeyValuePair<int, long>(pid, start));   // the registered process is gone
            }
            return GameProcessNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    private static long StartTicksOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return StartTicks(p);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return 0; }
    }

    /// <summary>The process's start time (UTC ticks), or 0 when it cannot be read.</summary>
    private static long StartTicks(Process p)
    {
        try { return p.StartTime.ToUniversalTime().Ticks; }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { return 0; }
    }
}
