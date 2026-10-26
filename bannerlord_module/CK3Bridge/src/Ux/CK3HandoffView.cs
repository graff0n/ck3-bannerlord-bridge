using System;
using System.Diagnostics;
using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Missions.MissionLogics;
using TaleWorlds.MountAndBlade.Missions.Objectives;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace CK3Bridge
{
    /// <summary>
    /// The in-Bannerlord half of the CK3 transition (docs/HOW_IT_WORKS.md, "The switch
    /// between the games"). Presenter of <see cref="BattleSession"/> (attached in CK3BattleViews):
    ///  1. Hold: IsReady() is false until the battle may be shown ("go" received when preloading, window uncloaked,
    ///     Bannerlord's process in the foreground). MissionScreen.AreViewsReady gates OnSceneRenderingStarted, which
    ///     removes the loading card. There is deliberately no timeout: a held battle is
    ///     only released by "go" or ended by an explicit cancel (the bridge cancels after 10 min).
    ///  2. Title card: the loading card is replaced in the same frame by the identical in-engine card over a black
    ///     backdrop; the battlefield emerges (backdrop ContextAlpha 1 -> 0 over 1.2 s), the title holds, then fades.
    ///     ContextAlpha is used because Widget.AlphaFactor does not reach text (TextWidget.OnRender). When the bridge shows
    ///     the title itself (BattleSession.InEngineTitle false, PROTOCOL.md §13.6) both cards are only the black backdrop:
    ///     it fades over the same 1.2 s while the bridge's card dissolves, and the battle follows with no title hold.
    ///  3. Native objective "Rout ..." with an enemy-casualty progress bar (MissionObjectiveLogic, behaviour #26).
    ///  4. After the result: footer countdown "Returning to your realm in N · [Enter] now · [Space] stay on the field";
    ///     the return itself (native fade, EndMission) is BattleSession.BeginReturn.
    /// The focus-lost escape menu is allowed only while fighting (MissionScreen.IsOpeningEscapeMenuOnFocusChangeAllowed
    /// asks every view), so window swaps never pause the game.
    /// Every engine callback is guarded: an exception must never reach the mission loop.
    /// </summary>
    internal sealed class CK3HandoffView : MissionView
    {
        private enum Stage
        {
            Hidden,
            TitleHold,
            TitleFade,
            Battle,
            Result,
            Linger,
            Returning,
        }

        private const float BackdropFadeSeconds = 1.2f;
        private const float TitleHoldSeconds = 2.5f;
        private const float TitleFadeSeconds = 1.0f;
        /// <summary>Animation steps are capped so a hitch (the first frames after a load are slow) never skips a fade.</summary>
        private const float MaxStepSeconds = 0.1f;
        private const double HeldAfterGoLogSeconds = 15.0;
        private const int BackdropLayerOrder = 299;
        private const int CardLayerOrder = 300;

        private readonly BattleSession _session;
        private readonly CK3ScoreboardVM _scoreboard;
        private readonly Stopwatch _heldAfterGo = new Stopwatch();
        private CK3CardVM _dataSource;
        private GauntletLayer _backdropLayer;
        private GauntletLayer _cardLayer;
        private Stage _stage = Stage.Hidden;
        private float _stageTime;
        private float _countdownLeft;
        private string _outcome = "";
        private string _holdReason;
        private double _nextHeldLog;
        private bool _pausedLastFrame;
        private bool _photoMode;
        private int _enemyTotal;
        private BattleSide _enemySide;

        public CK3HandoffView(BattleSession session, CK3ScoreboardVM scoreboard)
        {
            _session = session;
            _scoreboard = scoreboard;
            ViewOrderPriority = 60;
        }

        public override void OnMissionScreenInitialize()
        {
            base.OnMissionScreenInitialize();
            Guard(nameof(OnMissionScreenInitialize), () =>
            {
                _dataSource = new CK3CardVM();
                _dataSource.SetCard(_session.TitleCard);
                _backdropLayer = new GauntletLayer("CK3Backdrop", BackdropLayerOrder);
                _backdropLayer.LoadMovie("CK3Backdrop", _dataSource);
                _cardLayer = new GauntletLayer("CK3TitleCard", CardLayerOrder);
                _cardLayer.LoadMovie("CK3TitleCard", _dataSource);
                // Opaque from the start: the loading card covers both until OnSceneRenderingStarted, which then swaps the
                // loading card for this identical card without a frame of the bare scene in between.
                SetAlpha(1f, 1f);
                MissionScreen.AddLayer(_backdropLayer);
                MissionScreen.AddLayer(_cardLayer);

                BuiltSide enemy = _session.Armies.EnemySide;
                _enemySide = enemy.Side;
                _enemyTotal = enemy.Combatant.NumberOfAllMembers;
            });
        }

        public override void OnMissionScreenFinalize()
        {
            Guard(nameof(OnMissionScreenFinalize), () =>
            {
                if (_backdropLayer != null) MissionScreen.RemoveLayer(_backdropLayer);
                if (_cardLayer != null) MissionScreen.RemoveLayer(_cardLayer);
                _backdropLayer = null;
                _cardLayer = null;
                _dataSource?.OnFinalize();
                _dataSource = null;
            });
            base.OnMissionScreenFinalize();
        }

        // ------------------------------------------------------------------ hold

        public override bool IsReady()
        {
            try
            {
                if (_stage != Stage.Hidden) return true;
                // Same card as the loading card above it, also after a "title_in_engine" that arrived during the hold.
                _dataSource?.SetCard(_session.TitleCard);
                string reason = HoldReason();
                if (reason == null)
                {
                    if (_holdReason != null) BridgeIo.Log($"{_session.BattleId}: hold released, showing the battle");
                    _holdReason = null;
                    return true;
                }
                _session.MarkHeld();
                if (reason != _holdReason) BridgeIo.Log($"{_session.BattleId}: holding the loading card ({reason})");
                _holdReason = reason;
                LogLongHoldAfterGo(reason);
                return false;
            }
            catch (Exception e)
            {
                BridgeIo.Log("CK3HandoffView.IsReady failed: " + e);
                return _session.GoReceived;   // fail open only once the bridge asked for the battle
            }
        }

        /// <summary>Why the battle must stay behind the loading card, or null when it may be shown.</summary>
        private string HoldReason()
        {
            if (_session.IsStopping) return "the battle is being stopped";
            if (_session.AwaitingGo) return "preloaded, waiting for 'go'";
            if (WindowResidency.IsCloaked) return "the window is cloaked, waiting for 'fg'";
            if (!WindowResidency.IsOwnProcessForeground) return "Bannerlord is not the foreground window";
            return null;
        }

        /// <summary>The bridge's hold timeout starts at "go"; the module only reports a long wait.</summary>
        private void LogLongHoldAfterGo(string reason)
        {
            if (!_session.GoReceived) return;
            if (!_heldAfterGo.IsRunning)
            {
                _heldAfterGo.Start();
                _nextHeldLog = HeldAfterGoLogSeconds;
                return;
            }
            if (_heldAfterGo.Elapsed.TotalSeconds < _nextHeldLog) return;
            BridgeIo.Log($"{_session.BattleId}: still holding {_heldAfterGo.Elapsed.TotalSeconds:0} s after 'go' ({reason})");
            _nextHeldLog += HeldAfterGoLogSeconds;
        }

        public override bool IsOpeningEscapeMenuOnFocusChangeAllowed() => _session.Phase == BattlePhase.Fighting;

        // ------------------------------------------------------------------ reveal and title card

        public override void OnSceneRenderingStarted()
        {
            base.OnSceneRenderingStarted();
            Guard(nameof(OnSceneRenderingStarted), () =>
            {
                _dataSource?.SetCard(_session.TitleCard);
                Enter(Stage.TitleHold);
                SetAlpha(1f, 1f);
                _session.OnRevealed();
                StartObjective();
            });
        }

        public override void OnMissionScreenTick(float dt)
        {
            base.OnMissionScreenTick(dt);
            Guard(nameof(OnMissionScreenTick), () => Tick(dt));
        }

        /// <summary>
        /// One screen frame. <paramref name="dt"/> is real time (MissionScreen.OnFrameTick): fades use it capped, the
        /// return countdown uses it as is. The phase logic runs even if the card layers could not be built.
        /// </summary>
        private void Tick(float dt)
        {
            if (_stage == Stage.Hidden) return;
            float realDt = Math.Max(dt, 0f);
            _stageTime += Math.Min(realDt, MaxStepSeconds);

            // Keys count only after a whole unpaused frame: the Enter that closes the escape menu must not also return.
            bool paused = BattleSession.IsGamePaused;
            bool keysLive = !paused && !_pausedLastFrame;
            _pausedLastFrame = paused;

            BattlePhase phase = _session.Phase;
            if (phase >= BattlePhase.FadingOut)
            {
                // The native fade (global layer, above every mission layer) covers the screen from here on.
                if (_stage != Stage.Returning)
                {
                    Enter(Stage.Returning);
                    _dataSource?.HideFooter();
                }
                return;
            }
            if (phase == BattlePhase.Result && _stage < Stage.Result) EnterResult();

            switch (_stage)
            {
                case Stage.TitleHold:
                    if (!_session.InEngineTitle)
                    {
                        // Only the backdrop: it fades while the bridge's card dissolves, then straight to the battle.
                        SetAlpha(1f - _stageTime / BackdropFadeSeconds, 0f);
                        if (_stageTime >= BackdropFadeSeconds) EnterBattle();
                        break;
                    }
                    SetAlpha(1f - _stageTime / BackdropFadeSeconds, 1f);
                    if (_stageTime >= TitleHoldSeconds) Enter(Stage.TitleFade);
                    break;
                case Stage.TitleFade:
                    SetAlpha(0f, 1f - _stageTime / TitleFadeSeconds);
                    if (_stageTime >= TitleFadeSeconds) EnterBattle();
                    break;
                case Stage.Battle:
                    if (Mission.IsDeploymentFinished) _session.OnFighting();
                    break;
                case Stage.Result:
                    TickResult(realDt, paused, keysLive);
                    break;
                case Stage.Linger:
                    UpdateFooterVisibility(paused);
                    if (keysLive && ReturnKeyPressed()) _session.BeginReturn();
                    break;
            }
        }

        /// <summary>The title card (or the bare backdrop) is gone: the battle is in view.</summary>
        private void EnterBattle()
        {
            Enter(Stage.Battle);
            _dataSource?.SetCard(null);
            SetAlpha(0f, 0f);
            FinishDeploymentIfAuto();
        }

        /// <summary>
        /// presentation.deployment = "auto": what the Order of Battle "Ready" button does
        /// (MissionOrderDeploymentControllerVM.ExecuteBeginMission -> DeploymentHandler.FinishDeployment), once the title
        /// card is gone. Stock closes its Order of Battle UI in OnDeploymentFinished.
        /// </summary>
        private void FinishDeploymentIfAuto()
        {
            if (!string.Equals(_session.Request.Presentation.Deployment, "auto", StringComparison.OrdinalIgnoreCase)) return;
            if (Mission.IsDeploymentFinished || Mission.Mode != MissionMode.Deployment) return;
            DeploymentHandler handler = Mission.GetMissionBehavior<DeploymentHandler>();
            if (handler == null) return;
            handler.FinishDeployment();
            BridgeIo.Log($"{_session.BattleId}: deployment finished automatically (presentation.deployment = auto)");
        }

        // ------------------------------------------------------------------ objective

        private void StartObjective()
        {
            MissionObjectiveLogic logic = Mission.GetMissionBehavior<MissionObjectiveLogic>();
            if (logic == null) return;
            CardTexts texts = _session.Texts;
            // A generic objective without a completion callback completes on its first tick (GenericMissionObjective
            // .IsCompletionRequirementsMet defaults to true); it ends with the battle result.
            MissionObjective objective = MissionObjective
                .CreateGenericObjectiveBuilder(Mission, "ck3xbl_battle", new TextObject("{=!}" + texts.Title), new TextObject("{=!}" + texts.Objective))
                .SetIsCompletionRequirementsMetCallback(_ => _session.Phase >= BattlePhase.Result)
                .SetProgressCallback(_ => EnemyProgress())
                .Build();
            logic.StartObjective(objective);
        }

        /// <summary>Enemy agents out of the fight (killed, wounded, routed) of all the enemy side will field.</summary>
        private MissionObjectiveProgressInfo EnemyProgress()
        {
            if (_enemyTotal <= 0) return default;
            var side = _session.Armies.Accounting.Side(_enemySide);
            int fallen = side.Killed + side.Wounded + side.Routed;
            return new MissionObjectiveProgressInfo { RequiredProgressAmount = _enemyTotal, CurrentProgressAmount = Math.Min(fallen, _enemyTotal) };
        }

        // ------------------------------------------------------------------ result

        /// <summary>presentation.texts: the result footer in the player's language (null = the English of CardText).</summary>
        private ModuleTexts ModuleTexts => _session.Request.Presentation?.Texts;

        /// <summary>
        /// The battle result is in: the title card makes way for the footer (the centre stays free for the stock
        /// scoreboard and victory/defeat music). presentation.returnCountdownSeconds: 0 returns at once, -1 waits for the
        /// player, otherwise the countdown runs.
        /// </summary>
        private void EnterResult()
        {
            MissionResult result = Mission.MissionResult;
            _outcome = CardText.Outcome(_session.Texts.Title, result?.PlayerVictory == true, result?.PlayerDefeated == true, ModuleTexts);
            _dataSource?.SetCard(null);
            SetAlpha(0f, 1f);
            int seconds = _session.Request.Presentation.ReturnCountdownSeconds;
            if (seconds == 0)
            {
                Enter(Stage.Result);
                _session.BeginReturn();
                return;
            }
            if (seconds < 0)
            {
                EnterLinger();
                return;
            }
            Enter(Stage.Result);
            _countdownLeft = seconds;
            _dataSource?.ShowFooter(CardText.CountdownFooter(_outcome, CardText.SecondsShown(_countdownLeft), ModuleTexts));
        }

        private void TickResult(float realDt, bool paused, bool keysLive)
        {
            UpdateFooterVisibility(paused);
            if (paused) return;   // escape menu, options, inquiries: the countdown waits
            if (keysLive && TaleWorlds.InputSystem.Input.IsKeyPressed(InputKey.Space))
            {
                EnterLinger();
                _scoreboard?.Close();
                return;
            }
            _countdownLeft -= realDt;
            if (_countdownLeft <= 0f || (keysLive && ReturnKeyPressed()))
            {
                _session.BeginReturn();
                return;
            }
            _dataSource?.ShowFooter(CardText.CountdownFooter(_outcome, CardText.SecondsShown(_countdownLeft), ModuleTexts));
        }

        /// <summary>"Stay on the field": no countdown; Enter, the scoreboard's Done or the bridge's return_now return.</summary>
        private void EnterLinger()
        {
            Enter(Stage.Linger);
            _dataSource?.ShowFooter(CardText.WaitFooter(_outcome, ModuleTexts));
        }

        /// <summary>
        /// Global key state (TaleWorlds.InputSystem.Input), independent of which Gauntlet layer has focus: the scoreboard
        /// takes the focus when it opens. Bannerlord only receives keys while it is the foreground window.
        /// </summary>
        private static bool ReturnKeyPressed() =>
            TaleWorlds.InputSystem.Input.IsKeyPressed(InputKey.Enter) || TaleWorlds.InputSystem.Input.IsKeyPressed(InputKey.NumpadEnter);

        /// <summary>The footer steps aside for the escape menu (photo mode is handled in SetAlpha).</summary>
        private void UpdateFooterVisibility(bool paused) => SetAlpha(0f, paused ? 0f : 1f);

        // ------------------------------------------------------------------ photo mode

        public override void OnPhotoModeActivated()
        {
            base.OnPhotoModeActivated();
            _photoMode = true;
            Guard(nameof(OnPhotoModeActivated), () => SetAlpha(0f, 0f));
        }

        public override void OnPhotoModeDeactivated()
        {
            base.OnPhotoModeDeactivated();
            _photoMode = false;
        }

        // ------------------------------------------------------------------ helpers

        private void Enter(Stage stage)
        {
            _stage = stage;
            _stageTime = 0f;
        }

        /// <summary>Layer opacities; both stay invisible while photo mode is on (stock views do the same).</summary>
        private void SetAlpha(float backdrop, float card)
        {
            float visible = _photoMode ? 0f : 1f;
            if (_backdropLayer != null) _backdropLayer.UIContext.ContextAlpha = Clamp01(backdrop) * visible;
            if (_cardLayer != null) _cardLayer.UIContext.ContextAlpha = Clamp01(card) * visible;
        }

        private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

        private static void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                BridgeIo.Log($"CK3HandoffView.{what} failed: {e}");
            }
        }
    }
}
