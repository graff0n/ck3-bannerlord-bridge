// Composition root: builds every component by hand (constructor wiring), connects their events, starts and stops them in order.
using CK3xBL.Appearance;
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Bannerlord;
using CK3xBL.Core.Ck3;
using CK3xBL.Core.Config;
using CK3xBL.Core.Delivery;
using CK3xBL.Core.Diagnostics;
using CK3xBL.Core.Localization;
using CK3xBL.Core.Logs;
using CK3xBL.Core.Orchestration;
using CK3xBL.Mapping;
using CK3xBL.Ux;

namespace CK3xBL;

internal sealed class BridgeHost : IAsyncDisposable
{
    private readonly BridgePaths _paths;
    private readonly BridgeConfig _config;
    private readonly IJournal _journal;
    private readonly INotifier _notifier;
    private readonly UxInputGuardSource _inputGuards;
    private readonly Ck3Locator _ck3;
    private readonly Ck3LogMonitor _monitor;
    private readonly PauseStateReader _pause;
    private readonly BannerlordLink _bannerlord;
    private readonly TransitionUx _ux;
    private readonly AppearanceProvider _appearance;
    private readonly BattleOrchestrator _orchestrator;
    private readonly Ck3WindowModeGuard _ck3WindowMode;
    private readonly BattleArchive _archive;
    private volatile BridgeMode _mode;

    public BridgeHost(BridgePaths paths, BridgeConfig config, IJournal journal, INotifier notifier)
    {
        _paths = paths;
        _config = config;
        _journal = journal;
        _notifier = notifier;
        _mode = config.Mode;

        var ck3Logs = Path.Combine(config.Ck3.UserDir, "logs");
        _inputGuards = new UxInputGuardSource();
        var injector = new Win32KeyInjector();
        var desktop = new Win32Desktop(injector, _inputGuards);
        _monitor = new Ck3LogMonitor(Path.Combine(ck3Logs, config.Ck3.LogFile), journal,
            new LogTailerOptions { PollInterval = TimeSpan.FromMilliseconds(Math.Clamp(config.Ck3.LogPollMilliseconds, 20, 500)) });
        _ck3 = new Ck3Locator();
        // Attaches silently to what the file already holds (an earlier CK3 session's toggles are never replayed), PROTOCOL §15.
        _pause = new PauseStateReader(Path.Combine(ck3Logs, "dedicated_server.log"), Ck3Locator.IsRunning);

        var maaIndex = LoadMaaIndex(Path.Combine(paths.DataDir, "ck3_maa_index.json"));
        var d = config.Delivery;
        var keypad = new KeypadDelivery(_ck3, injector, _inputGuards, _monitor, maaIndex, new KeypadOptions
        {
            KeyDelay = TimeSpan.FromMilliseconds(Math.Clamp(d.KeyDelayMs, 1, 200)),
            ChunkSize = Math.Clamp(d.ChunkSize, 1, 64),
            HoldModifiersPerChunk = d.HoldModifiersPerChunk,
            AckTimeout = TimeSpan.FromSeconds(Math.Max(1, d.AckTimeoutSeconds)),
            BlockPhysicalInput = d.BlockPhysicalInput,
            AwardKeyBudget = Math.Max(0, d.KeypadAwardKeyBudget),
        }, journal);
        var runFile = new RunFileDelivery(_ck3, injector, _inputGuards, _monitor, maaIndex, new RunFileOptions
        {
            Ck3UserDir = config.Ck3.UserDir,
            KeyDelay = TimeSpan.FromMilliseconds(Math.Clamp(d.KeyDelayMs, 1, 200)),
            AckTimeout = TimeSpan.FromSeconds(Math.Max(1, d.AckTimeoutSeconds)),
            BlockPhysicalInput = d.BlockPhysicalInput,
        }, journal);
        var delivery = new DeliveryRouter(() => _mode, d.Channel, keypad, runFile, journal);

        var bl = config.Bannerlord;
        _bannerlord = new BannerlordLink(new BannerlordLinkOptions
        {
            GameDir = bl.GameDir,
            SharedDir = paths.SharedDir,
            Modules = bl.Modules,
            FallbackModules = bl.FallbackModules,
            KeepRunning = bl.KeepRunning,
            HeartbeatTimeout = TimeSpan.FromSeconds(Math.Max(1, bl.HeartbeatTimeoutSeconds)),
            LoadingHeartbeatTimeout = TimeSpan.FromSeconds(Math.Max(5, bl.LoadingHeartbeatTimeoutSeconds)),
            BootTimeout = TimeSpan.FromSeconds(Math.Max(30, bl.BootTimeoutSeconds)),
            HungKill = TimeSpan.FromSeconds(Math.Max(30, bl.HungKillSeconds)),
        }, journal);

        // UX lines go to bridge.log (the line already starts with "[ux] "); without a sink they would go to ux.log.
        UxLog.Sink = line => journal.Info("ux", line.StartsWith("[ux] ", StringComparison.Ordinal) ? line[5..] : line);
        _ux = new TransitionUx();
        _ux.OverlayAutoHidden += reason => journal.Info("ux", $"overlay removed by a safety valve: {reason}");
        _appearance = new AppearanceProvider { DataDirectory = paths.DataDir };

        // The battle loop's texts (cards, objective and host names, notices) speak the player's language; logs stay English.
        string language = Loc.Use(LanguageChoice.Resolve(config.Ux.Language, config.Ck3.UserDir, out var ck3Language));
        journal.Info("app", $"language: {language} (ux.language {config.Ux.Language}, CK3 {ck3Language ?? "unknown"})" +
                            (Loc.Reviewed ? "" : ", community translation, review appreciated"));

        _orchestrator = new BattleOrchestrator(new OrchestratorServices
        {
            Mapper = new BattleMapper(),
            Appearance = _appearance,
            Ux = _ux,
            Delivery = delivery,
            Bannerlord = _bannerlord,
            Ck3 = _ck3,
            Desktop = desktop,
            Notifier = notifier,
            Archive = _archive = new BattleArchive(paths.ArchiveDir, journal),
            Journal = journal,
            MapperOptions = () => config.ToMapperOptions(paths.DataDir),
            GameProcessSeen = _ux.RegisterGameProcess,
            ShowHint = _ux.ShowHintAsync,
            Ck3Paused = () => _pause.IsPaused,
        }, new OrchestratorOptions
        {
            OfferTimeout = TimeSpan.FromMinutes(Math.Max(1, config.Battle.OfferTimeoutMinutes)),
            EnterTimeout = TimeSpan.FromSeconds(Math.Max(15, config.Battle.EnterTimeoutSeconds)),
            QuietInput = TimeSpan.FromMilliseconds(Math.Clamp(d.QuietInputMs, 0, 2000)),
        });

        // A windowed CK3 breaks the seamless switch: checked once a battle is offered, before any card.
        _ck3WindowMode = new Ck3WindowModeGuard(Ck3WindowModeGuard.Win32Probe,
            text => notifier.Notify(Loc.T("notice.window_mode.title"), text, NoticeKind.Warning),
            _ux.Stager.EnsureBorderless, config.Ux.Ck3Windowed);
        _orchestrator.PhaseChanged += (phase, battleId) =>
        {
            // Off the orchestrator loop: restyling another process's window waits for that window.
            if (phase == BattlePhase.Offered)
                _ = Task.Run(() =>
                {
                    if (_ck3.Find() is { } ck3) _ck3WindowMode.Check(ck3.ProcessId, ck3.Hwnd);
                });
        };

        _monitor.EventReceived += _orchestrator.OnCk3Event;
        _monitor.EventReceived += e =>
        {
            // Bannerlord is normally preloaded at start; if it is not running when a battle begins, start it now.
            if (e is SnapshotCompleted && !_bannerlord.IsRunning) _ = _bannerlord.StartAsync(CancellationToken.None);
        };
        _bannerlord.StatusChanged += _orchestrator.OnBannerlordStatus;
        _bannerlord.Lost += _orchestrator.OnBannerlordLost;
        _bannerlord.ResultUpdated += _orchestrator.OnBannerlordResult;
        _bannerlord.ProblemChanged += problem =>
        {
            if (problem is not null) notifier.Notify(BannerlordProblemTitle, problem, NoticeKind.Warning);
        };
        _bannerlord.BootFailed += text => notifier.Notify("Bannerlord could not start the bridge module", text, NoticeKind.Warning);
        _pause.Attached += (paused, why) =>
            journal.Info("ck3", $"pause state at attach: {(paused is { } p ? p ? "paused" : "running" : "unknown")} ({why})");
        _pause.Restarted += () => journal.Info("ck3", "dedicated_server.log recreated (CK3 restarted); pause state unknown");
        _pause.Changed += paused => journal.Info("ck3", paused ? "paused" : "running");
    }

    public BridgeMode Mode => _mode;
    public BattleOrchestrator Orchestrator => _orchestrator;
    public IBannerlordLink Bannerlord => _bannerlord;
    public bool? Ck3Paused => _pause.IsPaused;
    public bool Ck3Running => _ck3.Find() is not null;
    public BridgePaths Paths => _paths;
    public BridgeConfig Config => _config;
    public bool BannerlordModuleMissing => _bannerlord.ModuleMissing;
    private string BannerlordProblemTitle => _bannerlord.ModuleMissing ? "Bannerlord module missing" : "Bannerlord is in use";

    /// <summary>The install check of the configured folders (file reads only; run off the UI thread).</summary>
    public List<InstallItem> CheckInstall() => InstallCheck.Run(new InstallTargets
    {
        Ck3Dir = _config.Ck3.GameDir,
        Ck3UserDir = _config.Ck3.UserDir,
        BannerlordDir = _config.Bannerlord.GameDir,
        XboxBannerlordDir = GameLocator.IsBannerlordDir(_config.Bannerlord.GameDir) ? null : GameLocator.FindXboxBannerlord(),
    });

    /// <summary>The CK3 mod is installed (its descriptor in the CK3 user folder points at a folder holding the mod).</summary>
    public bool Ck3ModInstalled =>
        InstallCheck.ReadModDescriptor(_config.Ck3.UserDir).Dir is { } dir && File.Exists(Path.Combine(dir, InstallCheck.ModProbeFile));

    public bool Ck3ModEnabled => Ck3ModList.IsEnabled(_config.Ck3.UserDir);

    public ModListChange EnableCk3Mod()
    {
        var change = Ck3ModList.Enable(_config.Ck3.UserDir);
        _journal.Info("ck3", $"enable the mod in {Ck3ModList.PathIn(_config.Ck3.UserDir)}: {change}");
        return change;
    }

    /// <summary>Starts every component. Missing games, mod or module are reported by the tray (CheckInstall), not here.</summary>
    public async Task StartAsync()
    {
        _archive.Prune();
        try { _ux.Initialize(new UxOptions { FontFamily = _config.Ux.FontFamily, CK3GameDir = _config.Ck3.GameDir }); }
        catch (Exception e) { _journal.Error("app", "transition UX failed to initialize; battles run without title cards", e); }
        try { _appearance.Start(_config.Ck3.UserDir, _config.Ck3.GameDir, _paths.CacheDir); }
        catch (Exception e) { _journal.Error("app", "appearance provider failed to start; heroes use stock looks", e); }

        // Before anything can write to the shared folder: files an earlier run left there would be taken as new work.
        _bannerlord.ResetTransport();
        _monitor.Start();
        _pause.Start();
        _orchestrator.Start();
        if (_config.Bannerlord.PreloadOnStart) await _bannerlord.StartAsync(CancellationToken.None);
        _journal.Info("app", $"ready (mode {_mode}, OUT channel {_monitor.LogPath}, session {_monitor.SessionKey})");
    }

    /// <summary>Launches CK3 in the given mode; the result channel follows the mode from now on. Returns a problem or null.</summary>
    public string? LaunchCk3(BridgeMode mode)
    {
        var problem = Ck3Launcher.Launch(_config.Ck3.GameDir, mode, _config.Ck3.ContinueLastSave, _journal);
        if (problem is null) _mode = mode;
        return problem;
    }

    public async Task StartBannerlordAsync()
    {
        bool reportedBefore = _bannerlord.Problem is not null;
        await _bannerlord.StartAsync(CancellationToken.None);
        // Asked for explicitly: say again why nothing started (a new problem is announced by ProblemChanged).
        if (reportedBefore && _bannerlord.Problem is { } problem) _notifier.Notify(BannerlordProblemTitle, problem, NoticeKind.Warning);
    }

    public Task StopBannerlordAsync() => _bannerlord.StopAsync(CancellationToken.None);

    public void AbortHotkey() => _orchestrator.OnAbortHotkey();

    public async ValueTask DisposeAsync()
    {
        _journal.Info("app", "shutting down");
        await _orchestrator.DisposeAsync();
        _monitor.Dispose();
        _pause.Dispose();
        try
        {
            _appearance.Stop();
            _appearance.Dispose();
        }
        catch (Exception e) { _journal.Warn("app", $"appearance stop: {e.Message}"); }
        // The UX goes before Bannerlord quits: disposing it puts back every volume it lowered while the game still has its audio
        // session (Windows gives a game's next instance the level it exited with).
        _ux.Dispose();
        await _bannerlord.StopAsync(CancellationToken.None);
        _bannerlord.Dispose();
        _inputGuards.Dispose();
        UxLog.Sink = null;
    }

    private MaaIndexTable? LoadMaaIndex(string path)
    {
        try
        {
            var table = MaaIndexTable.Load(path);
            _journal.Info("app", $"MaA keypad index: {table.Count} types");
            return table;
        }
        catch (Exception e)
        {
            _journal.Error("app", $"cannot read {path}; MaA losses are not sent to CK3", e);
            return null;
        }
    }
}
