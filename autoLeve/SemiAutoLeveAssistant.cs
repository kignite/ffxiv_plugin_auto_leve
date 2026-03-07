using Dalamud.Hooking;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace autoLeve;

public enum SemiAutoLeveState
{
    Idle,
    WaitingForNpcDialog,
    DialogDetected,
}

public enum NpcFlowMode
{
    Unknown,
    NpcA,
}

public enum NpcAStep
{
    AwaitTalk,
    AwaitSelectString,
    AwaitGuildLeveSelect,
    AwaitGuildLeveAccept,
    AwaitNpcAInteractionEnd,
}

public enum NpcBStep
{
    AwaitTalkStart,
    AwaitRequestSelectItem,
    AwaitRequestSubmit,
    AwaitConfirmYesno,
    AwaitTalkAfterSubmit,
    AwaitJournalResult,
    AwaitFinalTalkEnd,
}

public sealed class SemiAutoLeveAssistant : IDisposable
{
    private const int GuildLeveAcceptTransitionTimeoutMs = 1800;
    private const int GuildLevePostAcceptSettleMs = 900;
    private const int GuildLeveExitAfterAcceptDelayMs = 1000;
    private const int BConfirmPressLimit = 15;
    private const int AutoRetargetSettleMs = 850;
    private static nint gameWindowHandle = nint.Zero;

    private enum GuildLeveCaptureMode
    {
        Any,
        AcceptOnly,
    }

    private const int GuildLeveLevelCellMaxLookahead = 8;

    private static readonly string[] WatchedAddonNames =
    [
        "GuildLeve",
        "SelectString",
        "SelectIconString",
        "SelectYesno",
        "Request",
        "RequestItem",
        "InventoryExpansion",
        "Talk",
        "JournalResult",
    ];

    private static readonly AddonEventType[] CaptureEventTypes =
    [
        AddonEventType.DragDropBegin,
        AddonEventType.DragDropInsert,
        AddonEventType.DragDropEnd,
        AddonEventType.DragDropRollOver,
        AddonEventType.DragDropRollOut,
        AddonEventType.DragDropCancel,
        AddonEventType.MouseClick,
        AddonEventType.ListItemClick,
        AddonEventType.ButtonClick,
    ];

    private readonly Configuration configuration;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly IGameGui gameGui;
    private readonly IClientState clientState;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IAddonEventManager addonEventManager;
    private readonly Hook<AtkUnitBase.Delegates.FireCallback> fireCallbackHook;

    private string? lastDetectedAddon;
    private string? lastPhaseLogKey;
    private DateTime lastActionAtUtc;
    private NpcFlowMode lastMode = NpcFlowMode.Unknown;
    private NpcAStep npcAStep = NpcAStep.AwaitTalk;
    private int guildLeveSelectStrategy;
    private string? lastGuildLeveDetailTitle;
    private int guildLeveNoProgressCount;
    private DateTime guildLeveTargetStableSinceUtc;
    private DateTime guildLeveOpenedAtUtc;
    private DateTime lastConfiguredGuildLeveCallbackAtUtc;
    private DateTime lastGuildLeveSelectCallbackAtUtc;
    private string? guildLeveTitleBeforeSelectCallback;
    private DateTime lastGuildLeveAcceptActionAtUtc;
    private int guildLeveAcceptRetryStage;
    private bool guildLeveExitAfterAcceptSent;
    private DateTime lastGuildLeveExitActionAtUtc;
    private bool waitingForNpcBTurnIn;
    private NpcBStep npcBStep = NpcBStep.AwaitTalkStart;
    private bool npcBTurnInHadInteraction;
    private bool npcBCompletionObserved;
    private int npcBTalkPhaseCount;
    private bool npcBRequestStageObserved;
    private bool npcBReadyToFinishOnDialogClose;
    private DateTime npcBTurnInStartedAtUtc;
    private DateTime npcBTurnInLastInteractionAtUtc;
    private DateTime lastAutoRetargetAtUtc;
    private DateTime lastAutoInteractKeyAtUtc;
    private DateTime autoInteractBlockedUntilUtc;
    private int npcBConfirmPressCount;
    private bool pendingSwitchToAAfterDialogClose;
    private DateTime pendingSwitchToAStartedAtUtc;
    private bool pendingStopAfterDialogClose;
    private string? pendingStopReason;
    private int sessionTurnInCompletedCount;
    private bool guildLeveCallbackCaptureArmed;
    private int guildLeveCallbackCaptureRemaining;
    private DateTime guildLeveCallbackCaptureUntilUtc;
    private GuildLeveCaptureMode guildLeveCaptureMode = GuildLeveCaptureMode.Any;
    private bool npcBCallbackCaptureArmed;
    private int npcBCallbackCaptureRemaining;
    private DateTime npcBCallbackCaptureUntilUtc;
    private bool anyCallbackCaptureArmed;
    private int anyCallbackCaptureRemaining;
    private DateTime anyCallbackCaptureUntilUtc;
    private bool anyCallbackCaptureSawData;
    private string lastGenericCaptureSummary = "(none)";
    private bool anyReceiveCaptureArmed;
    private int anyReceiveCaptureRemaining;
    private DateTime anyReceiveCaptureUntilUtc;
    private bool anyReceiveCaptureSawData;
    private string lastReceiveCaptureSummary = "(none)";
    private string? lastReceiveAddonName;
    private int lastReceiveEventType;
    private int lastReceiveEventParam;
    private readonly List<IAddonEventHandle> addonEventCaptureHandles = new();
    private nint addonEventCaptureAddonPtr;
    private string? addonEventCaptureAddonName;
    private bool flowDiagnosticCaptureArmed;
    private int flowDiagnosticCaptureRemaining;
    private DateTime flowDiagnosticCaptureUntilUtc;
    private string? lastCapturedAddonName;
    private int lastCapturedCount;
    private CapturedAtkValue[] lastCapturedValues = new CapturedAtkValue[8];
    private bool bUseLearnedSelectCallback;
    private string? bLearnedSelectAddonName;
    private int bLearnedSelectCount;
    private CapturedAtkValue[] bLearnedSelectValues = new CapturedAtkValue[8];

    public SemiAutoLeveState State { get; private set; } = SemiAutoLeveState.Idle;
    public bool IsArmed { get; private set; }

    public unsafe SemiAutoLeveAssistant(
        Configuration configuration,
        IChatGui chatGui,
        IPluginLog log,
        IGameGui gameGui,
        IClientState clientState,
        IGameInteropProvider gameInteropProvider,
        IAddonLifecycle addonLifecycle,
        IAddonEventManager addonEventManager)
    {
        this.configuration = configuration;
        this.chatGui = chatGui;
        this.log = log;
        this.gameGui = gameGui;
        this.clientState = clientState;
        this.addonLifecycle = addonLifecycle;
        this.addonEventManager = addonEventManager;
        fireCallbackHook = gameInteropProvider.HookFromAddress<AtkUnitBase.Delegates.FireCallback>(
            (nint)AtkUnitBase.MemberFunctionPointers.FireCallback,
            OnFireCallbackDetour);
        fireCallbackHook.Enable();
        addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, OnAddonPreReceiveEvent);
    }

    public string LastGenericCaptureSummary => lastGenericCaptureSummary;
    public string LastReceiveCaptureSummary => lastReceiveCaptureSummary;

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, OnAddonPreReceiveEvent);
        ClearAddonEventCaptureHandles();
        fireCallbackHook.Dispose();
    }

    public string StatusSummary =>
        $"Enabled={configuration.SemiAutoLeveEnabled}, TestA={configuration.SemiAutoTestFlowAEnabled}, TestB={configuration.SemiAutoTestFlowBEnabled}, BConfirmSpam={configuration.SemiAutoBUseConfirmSpam}, BKeyConfirm={configuration.SemiAutoBUseKeyboardConfirmKey}, TurnIn={sessionTurnInCompletedCount}/{configuration.SemiAutoTargetTurnInCount}, Mode={lastMode}, AStep={npcAStep}, BWaiting={waitingForNpcBTurnIn}, BStep={npcBStep}, Talk={configuration.SemiAutoM3AutoAdvanceTalk}, Menu2={configuration.SemiAutoM3AutoSelectStringFirstOption}, Target={configuration.SemiAutoTargetLeveName}, ForceCb=[{configuration.SemiAutoGuildLeveSelectArg0},{configuration.SemiAutoGuildLeveSelectArg1},{configuration.SemiAutoGuildLeveSelectLeveId}], M34TwoArg={configuration.SemiAutoM34UseTwoArgCallback}[{configuration.SemiAutoM34TwoArgCmd},{configuration.SemiAutoM34TwoArgLeveId}], Armed={IsArmed}, State={State}" +
        (lastDetectedAddon is null ? string.Empty : $", LastAddon={lastDetectedAddon}");

    public void Start()
    {
        if (!configuration.SemiAutoLeveEnabled)
        {
            chatGui.Print("[autoLeve] 請先在設定中開啟半自動模式。");
            return;
        }

        if (clientState.LocalPlayer == null)
        {
            chatGui.Print("[autoLeve] 尚未登入角色。");
            return;
        }

        if (!configuration.SemiAutoTestFlowAEnabled && !configuration.SemiAutoTestFlowBEnabled)
        {
            chatGui.Print("[autoLeve] 請至少開啟一個測試流程（A 或 B）。");
            return;
        }

        // 依需求：腳本每次啟動前先切到烹調師裝備套組。
        var gsOk = TrySendChatCommand("/gs change 烹調師");
        if (configuration.SemiAutoVerboseLogging)
        {
            log.Information("Semi-auto leve pre-start: /gs change 烹調師 => {Result}", gsOk ? "ok" : "failed");
        }

        IsArmed = true;
        lastDetectedAddon = null;
        lastMode = NpcFlowMode.Unknown;
        npcAStep = NpcAStep.AwaitTalk;
        guildLeveSelectStrategy = 0;
        lastGuildLeveDetailTitle = null;
        guildLeveNoProgressCount = 0;
        guildLeveTargetStableSinceUtc = DateTime.MinValue;
        lastGuildLeveSelectCallbackAtUtc = DateTime.MinValue;
        guildLeveTitleBeforeSelectCallback = null;
        lastGuildLeveAcceptActionAtUtc = DateTime.MinValue;
        guildLeveAcceptRetryStage = 0;
        guildLeveExitAfterAcceptSent = false;
        lastGuildLeveExitActionAtUtc = DateTime.MinValue;
        waitingForNpcBTurnIn = false;
        npcBStep = NpcBStep.AwaitTalkStart;
        npcBTurnInHadInteraction = false;
        npcBCompletionObserved = false;
        npcBTalkPhaseCount = 0;
        npcBRequestStageObserved = false;
        npcBReadyToFinishOnDialogClose = false;
        npcBTurnInStartedAtUtc = DateTime.MinValue;
        npcBTurnInLastInteractionAtUtc = DateTime.MinValue;
        lastAutoRetargetAtUtc = DateTime.MinValue;
        lastAutoInteractKeyAtUtc = DateTime.MinValue;
        autoInteractBlockedUntilUtc = DateTime.MinValue;
        npcBConfirmPressCount = 0;
        pendingSwitchToAAfterDialogClose = false;
        pendingSwitchToAStartedAtUtc = DateTime.MinValue;
        pendingStopAfterDialogClose = false;
        pendingStopReason = null;
        sessionTurnInCompletedCount = 0;

        if (configuration.SemiAutoTestFlowAEnabled && configuration.SemiAutoTestFlowBEnabled)
        {
            EnterAFlow(true);
        }
        else if (!configuration.SemiAutoTestFlowAEnabled && configuration.SemiAutoTestFlowBEnabled)
        {
            EnterBFlow(true);
        }

        TransitionTo(SemiAutoLeveState.WaitingForNpcDialog);
        if (configuration.SemiAutoTestFlowAEnabled && configuration.SemiAutoTestFlowBEnabled)
        {
            if (configuration.SemiAutoTargetTurnInCount > 0)
            {
                chatGui.Print($"[autoLeve] 已開始 {configuration.SemiAutoTargetTurnInCount} 次理符繳交循環。");
            }
            else
            {
                chatGui.Print("[autoLeve] 已開始理符繳交循環（不限次數）。");
            }
        }
        else if (configuration.SemiAutoTestFlowAEnabled)
        {
            chatGui.Print("[autoLeve] 半自動理符助手已啟動，模式=A-only（只接理符）。");
        }
        else
        {
            chatGui.Print("[autoLeve] 半自動理符助手已啟動，模式=B-only（只繳交）。");
        }
    }

    public void Stop(string? reason = null)
    {
        if (!IsArmed && State == SemiAutoLeveState.Idle)
        {
            return;
        }

        IsArmed = false;
        npcAStep = NpcAStep.AwaitTalk;
        guildLeveSelectStrategy = 0;
        lastGuildLeveDetailTitle = null;
        guildLeveNoProgressCount = 0;
        guildLeveTargetStableSinceUtc = DateTime.MinValue;
        lastGuildLeveSelectCallbackAtUtc = DateTime.MinValue;
        guildLeveTitleBeforeSelectCallback = null;
        lastGuildLeveAcceptActionAtUtc = DateTime.MinValue;
        guildLeveAcceptRetryStage = 0;
        guildLeveExitAfterAcceptSent = false;
        lastGuildLeveExitActionAtUtc = DateTime.MinValue;
        waitingForNpcBTurnIn = false;
        npcBStep = NpcBStep.AwaitTalkStart;
        npcBTurnInHadInteraction = false;
        npcBCompletionObserved = false;
        npcBTurnInStartedAtUtc = DateTime.MinValue;
        npcBTurnInLastInteractionAtUtc = DateTime.MinValue;
        lastAutoRetargetAtUtc = DateTime.MinValue;
        lastAutoInteractKeyAtUtc = DateTime.MinValue;
        autoInteractBlockedUntilUtc = DateTime.MinValue;
        npcBConfirmPressCount = 0;
        pendingSwitchToAAfterDialogClose = false;
        pendingSwitchToAStartedAtUtc = DateTime.MinValue;
        pendingStopAfterDialogClose = false;
        pendingStopReason = null;
        sessionTurnInCompletedCount = 0;
        TransitionTo(SemiAutoLeveState.Idle);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            log.Warning("Semi-auto leve stopped: {Reason}", reason);
        }
        if (!string.IsNullOrWhiteSpace(reason))
        {
            chatGui.Print($"[autoLeve] 半自動理符助手停止：{reason}");
        }
    }

    public void DumpVisibleMenuEntries()
    {
        var detected = FindVisibleWatchedAddon();
        if (detected is null)
        {
            chatGui.Print("[autoLeve] 目前沒有偵測到可讀取的對話/選單視窗。");
            return;
        }

        var (addonName, addonPtr) = detected.Value;
        chatGui.Print($"[autoLeve] Dump addon: {addonName}");
        DumpAddonAtkValues(addonName, addonPtr);
    }

    public void ArmGuildLeveCallbackCaptureOnce()
    {
        guildLeveCallbackCaptureArmed = true;
        guildLeveCallbackCaptureRemaining = 30;
        guildLeveCallbackCaptureUntilUtc = DateTime.UtcNow.AddSeconds(10);
        guildLeveCaptureMode = GuildLeveCaptureMode.Any;
        chatGui.Print("[autoLeve] 已啟用 GuildLeve callback 捕捉，請手動點一次目標理符。");
    }

    public void ArmGuildLeveAcceptCallbackCaptureOnce()
    {
        guildLeveCallbackCaptureArmed = true;
        guildLeveCallbackCaptureRemaining = 30;
        guildLeveCallbackCaptureUntilUtc = DateTime.UtcNow.AddSeconds(12);
        guildLeveCaptureMode = GuildLeveCaptureMode.AcceptOnly;
        chatGui.Print("[autoLeve] 已啟用 M3-4 接受 callback 捕捉，請手動點一次「接受」。");
    }

    public void ArmNpcBCallbackCaptureOnce()
    {
        npcBCallbackCaptureArmed = true;
        npcBCallbackCaptureRemaining = 50;
        npcBCallbackCaptureUntilUtc = DateTime.UtcNow.AddSeconds(20);
        chatGui.Print("[autoLeve] 已啟用 B 流程 callback 捕捉，請在交貨流程手動按一次確認鍵(NUM0)。");
    }

    public void ArmAnyCallbackCaptureOnce()
    {
        anyCallbackCaptureArmed = true;
        anyCallbackCaptureRemaining = 80;
        anyCallbackCaptureUntilUtc = DateTime.UtcNow.AddSeconds(15);
        anyCallbackCaptureSawData = false;
        chatGui.Print("[autoLeve] 已啟用通用 callback 捕捉，請手動點擊你要測試的按鈕。");
    }

    public void ArmSingleStepCapture()
    {
        anyCallbackCaptureArmed = true;
        anyCallbackCaptureRemaining = 1;
        anyCallbackCaptureUntilUtc = DateTime.UtcNow.AddSeconds(10);
        anyCallbackCaptureSawData = false;
        chatGui.Print("[autoLeve] 單步捕捉已啟用：請手動做一次操作。");
    }

    public void ArmSingleReceiveEventCapture()
    {
        anyReceiveCaptureArmed = true;
        anyReceiveCaptureRemaining = 1;
        anyReceiveCaptureUntilUtc = DateTime.UtcNow.AddSeconds(10);
        anyReceiveCaptureSawData = false;
        lastReceiveCaptureSummary = "(none)";
        lastReceiveAddonName = null;
        lastReceiveEventType = 0;
        lastReceiveEventParam = 0;
        ClearAddonEventCaptureHandles();
        TryAttachAddonEventCapture();
        chatGui.Print("[autoLeve] 單步 ReceiveEvent 捕捉已啟用：請手動做一次操作。");
    }

    public void MarkCurrentTargetAttack1() => MarkCurrentTarget("attack1");

    public void MarkCurrentTargetAttack2() => MarkCurrentTarget("attack2");

    public void ReplayLastCapturedCallback(string? targetAddonOverride)
    {
        if (lastCapturedCount <= 0)
        {
            chatGui.Print("[autoLeve] 尚未有可重播的捕捉資料。");
            return;
        }

        var addonName = string.IsNullOrWhiteSpace(targetAddonOverride)
            ? lastCapturedAddonName
            : targetAddonOverride.Trim();

        if (string.IsNullOrWhiteSpace(addonName) || addonName == "(unknown)")
        {
            chatGui.Print("[autoLeve] 重播失敗：請填入可見 addon 名稱（例如 Request）。");
            return;
        }

        var addonPtr = gameGui.GetAddonByName(addonName);
        if (!TryFireCapturedCallback(addonPtr, lastCapturedCount, lastCapturedValues))
        {
            chatGui.Print($"[autoLeve] 重播失敗：addon \"{addonName}\" 不可見或不可用。");
            return;
        }

        chatGui.Print($"[autoLeve] 已重播捕捉 callback 到 addon=\"{addonName}\"，count={lastCapturedCount}");
        if (configuration.SemiAutoVerboseLogging)
        {
            log.Warning(
                "Semi-auto leve debug replay: targetAddon={Addon}, fromCapturedAddon={CapturedAddon}, count={Count}",
                addonName,
                lastCapturedAddonName ?? "(none)",
                lastCapturedCount);
        }
    }

    public unsafe void ReplayLastReceiveEvent(string? targetAddonOverride)
    {
        if (lastReceiveEventType <= 0)
        {
            chatGui.Print("[autoLeve] 尚未有可重播的 ReceiveEvent 資料。");
            return;
        }

        var addonName = string.IsNullOrWhiteSpace(targetAddonOverride)
            ? lastReceiveAddonName
            : targetAddonOverride.Trim();

        if (string.IsNullOrWhiteSpace(addonName) || addonName == "(unknown)")
        {
            chatGui.Print("[autoLeve] ReceiveEvent 重播失敗：請填入可見 addon 名稱（例如 Request）。");
            return;
        }

        var addonPtr = gameGui.GetAddonByName(addonName);
        if (addonPtr == nint.Zero)
        {
            chatGui.Print($"[autoLeve] ReceiveEvent 重播失敗：找不到 addon \"{addonName}\"。");
            return;
        }

        var unitBase = (AtkUnitBase*)addonPtr;
        if (unitBase == null || !unitBase->IsVisible)
        {
            chatGui.Print($"[autoLeve] ReceiveEvent 重播失敗：addon \"{addonName}\" 不可見。");
            return;
        }

        unitBase->ReceiveEvent((AtkEventType)lastReceiveEventType, lastReceiveEventParam, null, null);
        chatGui.Print($"[autoLeve] 已重播 ReceiveEvent：addon={addonName}, type={lastReceiveEventType}, param={lastReceiveEventParam}");
    }

    public void ArmFlowDiagnosticCaptureOnce()
    {
        flowDiagnosticCaptureArmed = true;
        flowDiagnosticCaptureRemaining = 150;
        flowDiagnosticCaptureUntilUtc = DateTime.UtcNow.AddSeconds(25);
        chatGui.Print("[autoLeve] 已啟用流程診斷捕捉(A/B)，請完整操作一次你要測的按鈕流程。");
    }

    public void ApplyLastCapturedCallbackToBSelect()
    {
        if (string.IsNullOrWhiteSpace(lastCapturedAddonName) || lastCapturedCount <= 0)
        {
            chatGui.Print("[autoLeve] 尚無可套用捕捉資料。");
            return;
        }

        bUseLearnedSelectCallback = true;
        bLearnedSelectAddonName = lastCapturedAddonName;
        bLearnedSelectCount = Math.Min(lastCapturedCount, bLearnedSelectValues.Length);
        Array.Copy(lastCapturedValues, bLearnedSelectValues, bLearnedSelectCount);
        chatGui.Print($"[autoLeve] 已套用最近捕捉為 B 選物：addon={bLearnedSelectAddonName}, count={bLearnedSelectCount}");
    }

    public unsafe void DebugSelectGuildLeveByCallbackArgs(int arg0, int arg1, int leveId)
    {
        if (leveId <= 0)
        {
            chatGui.Print("[autoLeve] leveId 必須大於 0。");
            return;
        }

        var addonPtr = gameGui.GetAddonByName("GuildLeve");
        if (addonPtr == nint.Zero)
        {
            chatGui.Print("[autoLeve] 找不到 GuildLeve 視窗，請先打開理符清單。");
            return;
        }

        var unitBase = (AtkUnitBase*)addonPtr;
        if (unitBase == null || !unitBase->IsVisible)
        {
            chatGui.Print("[autoLeve] GuildLeve 視窗不可見。");
            return;
        }

        var values = stackalloc AtkValue[3];
        values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        values[0].Int = arg0;
        values[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        values[1].Int = arg1;
        values[2].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        values[2].Int = leveId;
        unitBase->FireCallback(3, values, true);

        chatGui.Print($"[autoLeve] 已送出 GuildLeve 3 參數 callback: [{arg0},{arg1},{leveId}]");
        log.Warning("Semi-auto leve debug: fire callback [{Arg0},{Arg1},{LeveId}]", arg0, arg1, leveId);
    }

    public void ApplyGuildLeveSelectCallbackToAuto(int arg0, int arg1, int leveId)
    {
        if (leveId <= 0)
        {
            chatGui.Print("[autoLeve] 無法套用：leveId 必須大於 0。");
            return;
        }

        configuration.SemiAutoGuildLeveSelectArg0 = arg0;
        configuration.SemiAutoGuildLeveSelectArg1 = arg1;
        configuration.SemiAutoGuildLeveSelectLeveId = leveId;
        configuration.SemiAutoUseConfiguredGuildLeveSelectCallback = true;
        configuration.Save();
        chatGui.Print($"[autoLeve] 已套用到自動 M3-3：[{arg0},{arg1},{leveId}]");
    }

    public unsafe void DebugAcceptGuildLeveByTwoArgCallback(int cmd, int leveId)
    {
        if (leveId <= 0)
        {
            chatGui.Print("[autoLeve] M3-4 leveId 必須大於 0。");
            return;
        }

        var addonPtr = gameGui.GetAddonByName("GuildLeve");
        if (!TryFireGuildLeveTwoArgCallback(addonPtr, cmd, leveId))
        {
            chatGui.Print("[autoLeve] M3-4 測試失敗：找不到可見 GuildLeve 視窗。");
            return;
        }

        chatGui.Print($"[autoLeve] 已送出 M3-4 2參數 callback: [{cmd},{leveId}]");
    }

    public void DebugBRequestSelectFirstItem()
    {
        if (TryFireDebugRequestCallback(4))
        {
            chatGui.Print("[autoLeve] 手動B測試：已送出 Request callback 4（選第一格）");
        }
        else
        {
            chatGui.Print("[autoLeve] 手動B測試失敗：找不到可見 Request/RequestItem 視窗。");
        }
    }

    public void DebugBRequestSubmit()
    {
        if (TryFireDebugRequestCallback(0))
        {
            chatGui.Print("[autoLeve] 手動B測試：已送出 Request callback 0（提交）");
        }
        else
        {
            chatGui.Print("[autoLeve] 手動B測試失敗：找不到可見 Request/RequestItem 視窗。");
        }
    }

    public void DebugBTalkAdvance()
    {
        var talkPtr = gameGui.GetAddonByName("Talk");
        if (TryFireCallbackEmpty(talkPtr))
        {
            chatGui.Print("[autoLeve] 手動B測試：已送出 Talk empty callback。");
            return;
        }

        chatGui.Print("[autoLeve] 手動B測試失敗：找不到可見 Talk 視窗。");
    }

    public unsafe void DebugBRequestCallback4Arg(uint a0, uint a1, uint a2, uint a3)
    {
        foreach (var addonName in new[] { "Request", "RequestItem" })
        {
            var addonPtr = gameGui.GetAddonByName(addonName);
            if (TryFireRequestFourArgCallback(addonPtr, a0, a1, a2, a3))
            {
                chatGui.Print($"[autoLeve] 手動B測試：已送出 {addonName} callback4 [{a0},{a1},{a2},{a3}]");
                if (configuration.SemiAutoVerboseLogging)
                {
                    log.Information("Semi-auto leve debug B: {Addon} callback4 [{A0},{A1},{A2},{A3}]", addonName, a0, a1, a2, a3);
                }
                return;
            }
        }

        chatGui.Print("[autoLeve] 手動B測試失敗：找不到可見 Request/RequestItem 視窗。");
    }

    public unsafe void DebugFireGenericCallback(
        string addonName,
        int count,
        int t0, int v0,
        int t1, int v1,
        int t2, int v2,
        int t3, int v3,
        int t4, int v4)
    {
        if (string.IsNullOrWhiteSpace(addonName))
        {
            chatGui.Print("[autoLeve] 通用 callback 失敗：addon 名稱為空。");
            return;
        }

        var clampedCount = Math.Clamp(count, 0, 5);
        var addonPtr = gameGui.GetAddonByName(addonName);
        if (addonPtr == nint.Zero)
        {
            chatGui.Print($"[autoLeve] 通用 callback 失敗：找不到 addon \"{addonName}\"。");
            return;
        }

        var unitBase = (AtkUnitBase*)addonPtr;
        if (unitBase == null || !unitBase->IsVisible)
        {
            chatGui.Print($"[autoLeve] 通用 callback 失敗：addon \"{addonName}\" 不可見。");
            return;
        }

        var values = stackalloc AtkValue[5];
        FillGenericAtkValue(values, 0, t0, v0);
        FillGenericAtkValue(values, 1, t1, v1);
        FillGenericAtkValue(values, 2, t2, v2);
        FillGenericAtkValue(values, 3, t3, v3);
        FillGenericAtkValue(values, 4, t4, v4);
        unitBase->FireCallback((uint)clampedCount, values, true);

        chatGui.Print($"[autoLeve] 通用 callback 已送出：addon={addonName}, count={clampedCount}");
        if (configuration.SemiAutoVerboseLogging)
        {
            log.Warning(
                "Semi-auto leve debug generic fire: addon={Addon}, count={Count}, a0=({T0},{V0}), a1=({T1},{V1}), a2=({T2},{V2}), a3=({T3},{V3}), a4=({T4},{V4})",
                addonName,
                clampedCount,
                t0, v0,
                t1, v1,
                t2, v2,
                t3, v3,
                t4, v4);
        }
    }

    public void ApplyGuildLeveAcceptTwoArgToAuto(int cmd, int leveId)
    {
        if (leveId <= 0)
        {
            chatGui.Print("[autoLeve] 無法套用 M3-4：leveId 必須大於 0。");
            return;
        }

        configuration.SemiAutoM34UseTwoArgCallback = true;
        configuration.SemiAutoM34TwoArgCmd = cmd;
        configuration.SemiAutoM34TwoArgLeveId = leveId;
        configuration.Save();
        chatGui.Print($"[autoLeve] 已套用到自動 M3-4：[{cmd},{leveId}]");
    }

    public void Update()
    {
        if (anyReceiveCaptureArmed)
        {
            TryAttachAddonEventCapture();
        }

        if (!configuration.SemiAutoLeveEnabled)
        {
            if (IsArmed || State != SemiAutoLeveState.Idle)
            {
                Stop("設定已關閉");
            }
            return;
        }

        if (!IsArmed)
        {
            return;
        }

        var positionalMode = DetectFlowModeByPosition();

        var detected = FindVisibleWatchedAddon();
        var flowMode = ResolveFlowMode(positionalMode, detected?.AddonName);
        if (flowMode != NpcFlowMode.Unknown && lastMode != flowMode)
        {
            lastMode = flowMode;
        }

        if (detected is null)
        {
            if (pendingStopAfterDialogClose)
            {
                var reason = pendingStopReason ?? "已達指定繳交次數";
                pendingStopAfterDialogClose = false;
                pendingStopReason = null;
                Stop(reason);
                return;
            }

            if (pendingSwitchToAAfterDialogClose)
            {
                pendingSwitchToAAfterDialogClose = false;
                pendingSwitchToAStartedAtUtc = DateTime.MinValue;
                EnterAFlow(true);
                TransitionTo(SemiAutoLeveState.WaitingForNpcDialog);
                return;
            }

            if (npcAStep == NpcAStep.AwaitNpcAInteractionEnd && guildLeveAcceptRetryStage > 0)
            {
                CompleteAFlowAfterAccept();
                return;
            }

            if (waitingForNpcBTurnIn &&
                npcBTurnInHadInteraction &&
                npcBReadyToFinishOnDialogClose &&
                (DateTime.UtcNow - npcBTurnInLastInteractionAtUtc).TotalMilliseconds > 700)
            {
                var counted = npcBCompletionObserved;
                if (!counted)
                {
                    log.Warning("Semi-auto leve B flow closed without JournalResult, skip counting this round.");
                }
                CompleteBFlow("B 流程完成（第三段對話已結束）", counted: counted);
                return;
            }

            if (State != SemiAutoLeveState.WaitingForNpcDialog)
            {
                TransitionTo(SemiAutoLeveState.WaitingForNpcDialog);
            }

            TryAutoInitiateNpcDialogByMark();
            return;
        }

        var (detectedAddon, addonPtr) = detected.Value;
        lastDetectedAddon = detectedAddon;
        if (State != SemiAutoLeveState.DialogDetected)
        {
            TransitionTo(SemiAutoLeveState.DialogDetected);
        }

        if (pendingStopAfterDialogClose)
        {
            if (TryStartActionWindow(120))
            {
                if (detectedAddon == "Talk")
                {
                    _ = TrySendConfirmKeyNumpad0() || TrySendCallbackConfirm(detectedAddon, addonPtr);
                }
                else if (detectedAddon == "SelectString")
                {
                    if (TryFindCallbackIndexByText(addonPtr, "取消", out var cancelCb))
                    {
                        _ = TryFireCallbackInt(addonPtr, cancelCb);
                    }
                    else
                    {
                        _ = TryFireCallbackInt(addonPtr, 3);
                    }
                }
                else
                {
                    _ = TrySendCallbackConfirm(detectedAddon, addonPtr);
                }
            }
            return;
        }

        if (pendingSwitchToAAfterDialogClose)
        {
            var pendingMs = (DateTime.UtcNow - pendingSwitchToAStartedAtUtc).TotalMilliseconds;
            if (pendingMs > 6000)
            {
                pendingSwitchToAAfterDialogClose = false;
                pendingSwitchToAStartedAtUtc = DateTime.MinValue;
                EnterAFlow(true);
                TransitionTo(SemiAutoLeveState.WaitingForNpcDialog);
                chatGui.Print("[autoLeve] B 收尾逾時，強制切回 attack1 並進入 A 流程。");
                if (configuration.SemiAutoVerboseLogging)
                {
                    log.Warning("Semi-auto leve B->A drain timeout, force switch to A. addon={Addon}", detectedAddon);
                }
                return;
            }

            // B 結束後若還有對話視窗，持續推進/關閉，直到 detected=null 才切回 A。
            if (detectedAddon == "Talk")
            {
                // Talk 視窗優先用 NUM0，更接近玩家實際操作。
                if (TryStartActionWindow(120))
                {
                    _ = TrySendConfirmKeyNumpad0() || TrySendCallbackConfirm(detectedAddon, addonPtr);
                }
            }
            else if (TryStartActionWindow(120))
            {
                var closed = false;
                if (detectedAddon == "SelectString")
                {
                    if (TryFindCallbackIndexByText(addonPtr, "取消", out var cancelCb))
                    {
                        closed = TryFireCallbackInt(addonPtr, cancelCb);
                    }
                    else
                    {
                        closed = TryFireCallbackInt(addonPtr, 3);
                    }
                }
                else
                {
                    closed = TrySendCallbackConfirm(detectedAddon, addonPtr);
                }

                if (closed && configuration.SemiAutoVerboseLogging)
                {
                    log.Information("Semi-auto leve B->A drain: advance addon={Addon}", detectedAddon);
                }
            }
            return;
        }

        if (TryMarkPhaseLogged($"detected:{detectedAddon}") && configuration.SemiAutoVerboseLogging)
        {
            log.Information("Semi-auto leve dialog detected: {AddonName}", detectedAddon);
        }

        if (waitingForNpcBTurnIn)
        {
            HandleNpcBTurnInStep(detectedAddon, addonPtr);
            return;
        }

        if (!configuration.SemiAutoTestFlowAEnabled)
        {
            return;
        }

        HandleNpcAStep(detectedAddon, addonPtr);
    }

    private void HandleNpcBTurnInStep(string detectedAddon, nint addonPtr)
    {
        if (!IsNpcBTurnInAddon(detectedAddon))
        {
            return;
        }

        ObserveNpcBDialogProgress(detectedAddon);

        if (detectedAddon == "JournalResult")
        {
            npcBCompletionObserved = true;
        }

        if (configuration.SemiAutoBUseConfirmSpam)
        {
            HandleNpcBTurnInStepByConfirmSpam(detectedAddon, addonPtr);
            return;
        }

        if (!TryStartActionWindow(140))
        {
            return;
        }

        switch (npcBStep)
        {
            case NpcBStep.AwaitTalkStart:
                if (detectedAddon == "Talk")
                {
                    if (TryFireCallbackEmpty(addonPtr))
                    {
                        MarkNpcBAction(detectedAddon, "empty", NpcBStep.AwaitRequestSelectItem);
                    }
                }
                else if (detectedAddon == "Request")
                {
                    npcBStep = NpcBStep.AwaitRequestSelectItem;
                }
                break;

            case NpcBStep.AwaitRequestSelectItem:
                if (TryFireLearnedBSelectCallback())
                {
                    MarkNpcBAction(bLearnedSelectAddonName ?? "Request", "learned", NpcBStep.AwaitRequestSubmit);
                }
                else if (detectedAddon == "Request" && TryFireRequestSelectFirstSlot(addonPtr))
                {
                    // 依 flowdiag：Request 選第一格常見 4 參數 callback [2,0,44,0]
                    MarkNpcBAction(detectedAddon, "[2,0,44,0]", NpcBStep.AwaitRequestSubmit);
                }
                else if (detectedAddon == "Request" && TryFireCallbackInt(addonPtr, 4))
                {
                    // 舊版 fallback：某些環境可用 int 4 選第一格。
                    MarkNpcBAction(detectedAddon, "4", NpcBStep.AwaitRequestSubmit);
                }
                break;

            case NpcBStep.AwaitRequestSubmit:
                if (detectedAddon == "Request" && TryFireCallbackInt(addonPtr, 0))
                {
                    MarkNpcBAction(detectedAddon, "0", NpcBStep.AwaitConfirmYesno);
                }
                break;

            case NpcBStep.AwaitConfirmYesno:
                if (detectedAddon == "SelectYesno" && TryFireCallbackInt(addonPtr, 0))
                {
                    MarkNpcBAction(detectedAddon, "0", NpcBStep.AwaitTalkAfterSubmit);
                }
                else if (detectedAddon == "Talk")
                {
                    // 部分流程不會彈 SelectYesno，提交後直接回 Talk。
                    npcBStep = NpcBStep.AwaitTalkAfterSubmit;
                    if (configuration.SemiAutoVerboseLogging)
                    {
                        log.Information("Semi-auto leve B action: skip SelectYesno, continue with Talk");
                    }
                }
                break;

            case NpcBStep.AwaitTalkAfterSubmit:
                if (detectedAddon == "Talk" && TryFireCallbackEmpty(addonPtr))
                {
                    MarkNpcBAction(detectedAddon, "empty", NpcBStep.AwaitJournalResult);
                }
                else if (detectedAddon == "JournalResult")
                {
                    npcBStep = NpcBStep.AwaitJournalResult;
                }
                break;

            case NpcBStep.AwaitJournalResult:
                if (detectedAddon == "JournalResult" && TryFireCallbackInt(addonPtr, 0))
                {
                    MarkNpcBAction(detectedAddon, "0", NpcBStep.AwaitFinalTalkEnd);
                }
                break;

            case NpcBStep.AwaitFinalTalkEnd:
                if (detectedAddon == "Talk" && TryFireCallbackEmpty(addonPtr))
                {
                    MarkNpcBAction(detectedAddon, "empty", NpcBStep.AwaitFinalTalkEnd);
                }
                break;
        }
    }

    private void TryAutoInitiateNpcDialogByMark()
    {
        if (!IsArmed)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var mark = waitingForNpcBTurnIn ? "attack2" : "attack1";

        if ((now - lastAutoRetargetAtUtc).TotalMilliseconds >= 1500)
        {
            TryTargetByMark(mark);
            lastAutoRetargetAtUtc = now;
            autoInteractBlockedUntilUtc = now.AddMilliseconds(AutoRetargetSettleMs);
            if (configuration.SemiAutoVerboseLogging)
            {
                log.Information("Semi-auto leve auto-init: retarget {Mark}", mark);
            }
            return;
        }

        if ((now - lastAutoInteractKeyAtUtc).TotalMilliseconds < 450)
        {
            return;
        }

        if (now < autoInteractBlockedUntilUtc)
        {
            return;
        }

        if (TrySendConfirmKeyNumpad0())
        {
            lastAutoInteractKeyAtUtc = now;
            if (configuration.SemiAutoVerboseLogging)
            {
                log.Information("Semi-auto leve auto-init: send confirm key NUM0");
            }
        }
    }

    private void HandleNpcBTurnInStepByConfirmSpam(string detectedAddon, nint addonPtr)
    {
        ObserveNpcBDialogProgress(detectedAddon);

        if (!TryStartActionWindow(120))
        {
            return;
        }

        var didSendConfirm = configuration.SemiAutoBUseKeyboardConfirmKey
            ? TrySendConfirmKeyNumpad0()
            : TrySendCallbackConfirm(detectedAddon, addonPtr);
        if (!didSendConfirm)
        {
            return;
        }

        if (!TryConsumeBConfirmBudget("B-confirm-spam"))
        {
            return;
        }

        npcBTurnInHadInteraction = true;
        npcBTurnInLastInteractionAtUtc = DateTime.UtcNow;

        if (configuration.SemiAutoVerboseLogging)
        {
            var mode = configuration.SemiAutoBUseKeyboardConfirmKey ? "NUM0" : "callback0";
            log.Information("Semi-auto leve B confirm-spam: addon={Addon}, mode={Mode}", detectedAddon, mode);
        }
    }

    private void ObserveNpcBDialogProgress(string detectedAddon)
    {
        switch (detectedAddon)
        {
            case "Request":
            case "RequestItem":
                npcBRequestStageObserved = true;
                break;
            case "SelectYesno":
                break;
            case "JournalResult":
                npcBCompletionObserved = true;
                break;
            case "Talk":
                if (!npcBRequestStageObserved)
                {
                    npcBTalkPhaseCount = Math.Max(npcBTalkPhaseCount, 1);
                }
                else if (!npcBCompletionObserved)
                {
                    npcBTalkPhaseCount = Math.Max(npcBTalkPhaseCount, 2);
                }
                else
                {
                    npcBTalkPhaseCount = Math.Max(npcBTalkPhaseCount, 3);
                    npcBReadyToFinishOnDialogClose = true;
                }
                break;
        }
    }

    private bool TrySendCallbackConfirm(string detectedAddon, nint addonPtr)
    {
        return detectedAddon switch
        {
            "Talk" => TryFireCallbackEmpty(addonPtr) || TryFireCallbackInt(addonPtr, 0),
            "Request" => TryFireCallbackInt(addonPtr, 0),
            "RequestItem" => TryFireCallbackInt(addonPtr, 0),
            "SelectYesno" => TryFireCallbackInt(addonPtr, 0),
            "JournalResult" => TryFireCallbackInt(addonPtr, 0),
            _ => false,
        };
    }

    private bool TrySendConfirmKeyNumpad0()
    {
        var hWnd = gameWindowHandle;
        if (hWnd == nint.Zero)
        {
            hWnd = Process.GetCurrentProcess().MainWindowHandle;
            gameWindowHandle = hWnd;
        }

        if (hWnd == nint.Zero)
        {
            return false;
        }

        _ = SendMessage(hWnd, 0x100, (nint)VirtualKey.NUMPAD0, nint.Zero); // WM_KEYDOWN
        Thread.Sleep(40);
        _ = SendMessage(hWnd, 0x101, (nint)VirtualKey.NUMPAD0, nint.Zero); // WM_KEYUP
        return true;
    }

    private unsafe bool TryFireCallbackEmpty(nint addonPtr)
    {
        if (addonPtr == nint.Zero)
        {
            return false;
        }

        var unitBase = (AtkUnitBase*)addonPtr;
        if (unitBase == null || !unitBase->IsVisible)
        {
            return false;
        }

        unitBase->FireCallback(0, null, true);
        return true;
    }

    private unsafe bool TryFireRequestSelectFirstSlot(nint addonPtr)
    {
        return TryFireRequestFourArgCallback(addonPtr, 2, 0, 44, 0);
    }

    private unsafe bool TryFireRequestFourArgCallback(nint addonPtr, uint a0, uint a1, uint a2, uint a3)
    {
        if (addonPtr == nint.Zero)
        {
            return false;
        }

        var unitBase = (AtkUnitBase*)addonPtr;
        if (unitBase == null || !unitBase->IsVisible)
        {
            return false;
        }

        var values = stackalloc AtkValue[4];
        values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        values[0].Int = (int)a0;
        values[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt;
        values[1].UInt = a1;
        values[2].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt;
        values[2].UInt = a2;
        values[3].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt;
        values[3].UInt = a3;
        unitBase->FireCallback(4, values, true);
        return true;
    }

    private bool TryFireLearnedBSelectCallback()
    {
        if (!bUseLearnedSelectCallback ||
            string.IsNullOrWhiteSpace(bLearnedSelectAddonName) ||
            bLearnedSelectCount <= 0)
        {
            return false;
        }

        var addonPtr = gameGui.GetAddonByName(bLearnedSelectAddonName);
        return TryFireCapturedCallback(addonPtr, bLearnedSelectCount, bLearnedSelectValues);
    }

    private unsafe bool TryFireCapturedCallback(nint addonPtr, int count, CapturedAtkValue[] captured)
    {
        if (addonPtr == nint.Zero || count <= 0)
        {
            return false;
        }

        var unitBase = (AtkUnitBase*)addonPtr;
        if (unitBase == null || !unitBase->IsVisible)
        {
            return false;
        }

        var fireCount = Math.Min(count, captured.Length);
        var values = stackalloc AtkValue[fireCount];
        for (var i = 0; i < fireCount; i++)
        {
            var cv = captured[i];
            values[i].Type = cv.Type;
            if (cv.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt)
            {
                values[i].UInt = cv.UInt;
            }
            else
            {
                values[i].Int = cv.Int;
            }
        }

        unitBase->FireCallback((uint)fireCount, values, true);
        return true;
    }

    private void MarkNpcBAction(string addon, string callbackLabel, NpcBStep nextStep)
    {
        npcBTurnInHadInteraction = true;
        npcBTurnInLastInteractionAtUtc = DateTime.UtcNow;
        npcBStep = nextStep;
        if (configuration.SemiAutoVerboseLogging)
        {
            log.Information("Semi-auto leve B action: {Addon} callback {Callback}, next={NextStep}", addon, callbackLabel, nextStep);
        }
    }

    private void HandleNpcAStep(string detectedAddon, nint addonPtr)
    {
        switch (npcAStep)
        {
            case NpcAStep.AwaitTalk:
                if (detectedAddon == "Talk")
                {
                    if (configuration.SemiAutoM3AutoAdvanceTalk &&
                        TryStartActionWindow() &&
                        TryFireCallbackInt(addonPtr, 0))
                    {
                        if (configuration.SemiAutoVerboseLogging)
                        {
                            log.Information("Semi-auto leve action: Talk callback 0");
                        }
                    }
                    npcAStep = NpcAStep.AwaitSelectString;
                }
                else if (detectedAddon == "SelectString")
                {
                    npcAStep = NpcAStep.AwaitSelectString;
                }
                else if (detectedAddon == "GuildLeve")
                {
                    npcAStep = NpcAStep.AwaitGuildLeveSelect;
                }
                break;

            case NpcAStep.AwaitSelectString:
                if (detectedAddon == "GuildLeve")
                {
                    npcAStep = NpcAStep.AwaitGuildLeveSelect;
                    break;
                }

                if (!configuration.SemiAutoM3AutoSelectStringFirstOption ||
                    detectedAddon != "SelectString")
                {
                    break;
                }

                if (TryStartActionWindow() &&
                    TryFireCallbackInt(addonPtr, 1))
                {
                    npcAStep = NpcAStep.AwaitGuildLeveSelect;
                    if (configuration.SemiAutoVerboseLogging)
                    {
                        log.Information("Semi-auto leve action: SelectString callback 1 (製作任務)");
                    }
                }
                break;

            case NpcAStep.AwaitGuildLeveSelect:
                if (!configuration.SemiAutoM3AutoSelectTargetLeveByName)
                {
                    break;
                }

                if (detectedAddon == "SelectString")
                {
                    AdvanceGuildLeveSelectStrategy("unexpected SelectString during GuildLeveSelect");
                    guildLeveNoProgressCount = 0;
                    npcAStep = NpcAStep.AwaitSelectString;
                    guildLeveOpenedAtUtc = DateTime.MinValue;
                    if (configuration.SemiAutoVerboseLogging)
                    {
                        log.Warning("Semi-auto leve action: left GuildLeve unexpectedly, waiting SelectString flow to re-enter");
                    }
                    break;
                }

                if (detectedAddon != "GuildLeve")
                {
                    if (guildLeveAcceptRetryStage > 0)
                    {
                        CompleteAFlowAfterAccept();
                    }
                    guildLeveOpenedAtUtc = DateTime.MinValue;
                    break;
                }

                if (guildLeveOpenedAtUtc == DateTime.MinValue)
                {
                    guildLeveOpenedAtUtc = DateTime.UtcNow;
                }

                var currentDetailTitle = TryGetGuildLeveDetailTitle(addonPtr);
                var targetSelected = IsGuildLeveTargetSelected(addonPtr, configuration.SemiAutoTargetLeveName);
                if (targetSelected)
                {
                    if (guildLeveTargetStableSinceUtc == DateTime.MinValue)
                    {
                        guildLeveTargetStableSinceUtc = DateTime.UtcNow;
                        if (configuration.SemiAutoVerboseLogging)
                        {
                            log.Information(
                                "Semi-auto leve action: target detail detected, waiting stable before accept, detail={Detail}",
                                currentDetailTitle ?? "(unknown)");
                        }
                        break;
                    }

                    if ((DateTime.UtcNow - guildLeveTargetStableSinceUtc).TotalMilliseconds < 320)
                    {
                        if (configuration.SemiAutoVerboseLogging)
                        {
                            log.Information(
                                "Semi-auto leve action: target detail not stable yet, wait={WaitMs}ms",
                                (int)(DateTime.UtcNow - guildLeveTargetStableSinceUtc).TotalMilliseconds);
                        }
                        break;
                    }

                    npcAStep = NpcAStep.AwaitGuildLeveAccept;
                    guildLeveSelectStrategy = 0;
                    guildLeveNoProgressCount = 0;
                    guildLeveAcceptRetryStage = 0;
                    lastGuildLeveAcceptActionAtUtc = DateTime.MinValue;
                    break;
                }
                else
                {
                    guildLeveTargetStableSinceUtc = DateTime.MinValue;
                }

                if (!string.IsNullOrEmpty(currentDetailTitle))
                {
                    if (string.Equals(currentDetailTitle, lastGuildLeveDetailTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        guildLeveNoProgressCount++;
                        if (guildLeveNoProgressCount >= 3)
                        {
                            AdvanceGuildLeveSelectStrategy("no cursor movement");
                            guildLeveNoProgressCount = 0;
                        }
                    }
                    else
                    {
                        lastGuildLeveDetailTitle = currentDetailTitle;
                        guildLeveNoProgressCount = 0;
                    }
                }

                // 避免 GuildLeve 初次打開時 UI 尚未就緒，導致第一發 callback 被吃掉。
                if ((DateTime.UtcNow - guildLeveOpenedAtUtc).TotalMilliseconds < 400)
                {
                    if (configuration.SemiAutoVerboseLogging)
                    {
                        log.Information("Semi-auto leve action: waiting GuildLeve warmup before callback, current={Current}, target={Target}", currentDetailTitle ?? "(unknown)", configuration.SemiAutoTargetLeveName);
                    }
                    break;
                }

                // 優先嘗試配置 callback，避免被一般 action delay 擋住。
                if (TryFireConfiguredGuildLeveCallback(addonPtr, configuration.SemiAutoTargetLeveName))
                {
                    lastGuildLeveSelectCallbackAtUtc = DateTime.UtcNow;
                    guildLeveTitleBeforeSelectCallback = currentDetailTitle;
                    lastGuildLeveDetailTitle = currentDetailTitle;
                    // callback 送出後先留在 Select，等待詳情標題實際切到目標再進 Accept。
                    npcAStep = NpcAStep.AwaitGuildLeveSelect;
                    chatGui.Print($"[autoLeve] 已送出目標理符強制 callback: [{configuration.SemiAutoGuildLeveSelectArg0},{configuration.SemiAutoGuildLeveSelectArg1},{configuration.SemiAutoGuildLeveSelectLeveId}]");
                    break;
                }

                if (!TryStartActionWindow(140))
                {
                    break;
                }

                if (!TryResolveGuildLeveStepCallback(addonPtr, configuration.SemiAutoTargetLeveName, out var selectCallback))
                {
                    if (configuration.SemiAutoVerboseLogging)
                    {
                        log.Warning("Semi-auto leve action: cannot resolve step callback for target leve {Target}", configuration.SemiAutoTargetLeveName);
                    }
                    break;
                }

                if (TryFireCallbackInt(addonPtr, selectCallback))
                {
                    lastGuildLeveSelectCallbackAtUtc = DateTime.UtcNow;
                    guildLeveTitleBeforeSelectCallback = currentDetailTitle;
                    lastGuildLeveDetailTitle = currentDetailTitle;
                    guildLeveTargetStableSinceUtc = DateTime.MinValue;
                    // 保持在選取階段，等待下一輪確認詳情文字已切到目標後再進 Accept。
                    npcAStep = NpcAStep.AwaitGuildLeveSelect;
                    chatGui.Print($"[autoLeve] 已選取目標理符：{configuration.SemiAutoTargetLeveName} (cb={selectCallback})");
                }
                break;

            case NpcAStep.AwaitGuildLeveAccept:
                if (!configuration.SemiAutoM3AutoAcceptLeve)
                {
                    // 只測 M3-3 時不應卡在 Accept。
                    npcAStep = NpcAStep.AwaitGuildLeveSelect;
                    break;
                }

                if (detectedAddon == "SelectString")
                {
                    AdvanceGuildLeveSelectStrategy("unexpected SelectString during GuildLeveAccept");
                    guildLeveNoProgressCount = 0;
                    npcAStep = NpcAStep.AwaitSelectString;
                    guildLeveOpenedAtUtc = DateTime.MinValue;
                    if (configuration.SemiAutoVerboseLogging)
                    {
                        log.Warning("Semi-auto leve action: left GuildLeve during accept, waiting SelectString flow to re-enter");
                    }
                    break;
                }

                if (detectedAddon != "GuildLeve")
                {
                    if (guildLeveAcceptRetryStage > 0)
                    {
                        npcAStep = NpcAStep.AwaitNpcAInteractionEnd;
                    }
                    guildLeveOpenedAtUtc = DateTime.MinValue;
                    break;
                }

                var acceptDetailTitle = TryGetGuildLeveDetailTitle(addonPtr);
                if (!IsGuildLeveTargetSelected(addonPtr, configuration.SemiAutoTargetLeveName))
                {
                    if (configuration.SemiAutoVerboseLogging)
                    {
                        log.Warning("Semi-auto leve action: selected detail does not match target={Target}, reselecting", configuration.SemiAutoTargetLeveName);
                    }

                    npcAStep = NpcAStep.AwaitGuildLeveSelect;
                    guildLeveOpenedAtUtc = DateTime.UtcNow;
                    break;
                }

                if ((DateTime.UtcNow - lastGuildLeveSelectCallbackAtUtc).TotalMilliseconds < 320)
                {
                    if (configuration.SemiAutoVerboseLogging)
                    {
                        log.Information(
                            "Semi-auto leve action: waiting selection settle before accept, detail={Detail}, target={Target}",
                            acceptDetailTitle ?? "(unknown)",
                            configuration.SemiAutoTargetLeveName);
                    }
                    break;
                }

                if (!string.IsNullOrEmpty(guildLeveTitleBeforeSelectCallback) &&
                    IsTargetTitle(guildLeveTitleBeforeSelectCallback, acceptDetailTitle ?? string.Empty))
                {
                    if (configuration.SemiAutoVerboseLogging)
                    {
                        log.Warning(
                            "Semi-auto leve action: detail title unchanged after select callback ({Detail}), reselecting",
                            acceptDetailTitle ?? "(unknown)");
                    }
                    npcAStep = NpcAStep.AwaitGuildLeveSelect;
                    break;
                }

                if (!TryStartActionWindow(260))
                {
                    break;
                }

                var acceptCallback = ResolveGuildLeveAcceptCallback(addonPtr);
                if (guildLeveAcceptRetryStage == 0)
                {
                    var acceptSent = false;
                    var actionTag = string.Empty;
                    var canUseTwoArgAccept = configuration.SemiAutoM34TwoArgLeveId > 0;
                    if (canUseTwoArgAccept)
                    {
                        acceptSent = TryFireGuildLeveTwoArgCallback(
                            addonPtr,
                            configuration.SemiAutoM34TwoArgCmd,
                            configuration.SemiAutoM34TwoArgLeveId);
                        actionTag = configuration.SemiAutoM34UseTwoArgCallback
                            ? $"accept-twoarg([{configuration.SemiAutoM34TwoArgCmd},{configuration.SemiAutoM34TwoArgLeveId}])"
                            : $"accept-twoarg(auto,[{configuration.SemiAutoM34TwoArgCmd},{configuration.SemiAutoM34TwoArgLeveId}])";
                    }

                    if (!acceptSent)
                    {
                        acceptSent = TryFireCallbackInt(addonPtr, acceptCallback);
                        actionTag = canUseTwoArgAccept
                            ? $"accept-index-fallback(cb={acceptCallback})"
                            : $"accept-index(cb={acceptCallback})";
                    }
                    if (acceptSent)
                    {
                        guildLeveAcceptRetryStage++;
                        lastGuildLeveAcceptActionAtUtc = DateTime.UtcNow;
                        guildLeveExitAfterAcceptSent = false;
                        lastGuildLeveExitActionAtUtc = DateTime.MinValue;
                        npcAStep = NpcAStep.AwaitNpcAInteractionEnd;
                        if (configuration.SemiAutoVerboseLogging)
                        {
                            log.Warning(
                                "Semi-auto leve action: GuildLeve accept send {Action}, attempt={Attempt}",
                                actionTag,
                                guildLeveAcceptRetryStage);
                        }
                    }
                    break;
                }

                if ((DateTime.UtcNow - lastGuildLeveAcceptActionAtUtc).TotalMilliseconds < GuildLeveAcceptTransitionTimeoutMs)
                {
                    if (configuration.SemiAutoVerboseLogging)
                    {
                        log.Information("Semi-auto leve action: waiting post-accept transition");
                    }
                    break;
                }

                if (configuration.SemiAutoVerboseLogging)
                {
                    log.Warning("Semi-auto leve action: accept transition timeout, back to reselect");
                }
                guildLeveAcceptRetryStage = 0;
                npcAStep = NpcAStep.AwaitGuildLeveSelect;
                guildLeveOpenedAtUtc = DateTime.UtcNow;
                break;

            case NpcAStep.AwaitNpcAInteractionEnd:
                if (detectedAddon == "Talk")
                {
                    if (configuration.SemiAutoM3AutoAdvanceTalk &&
                        TryStartActionWindow(180) &&
                        TryFireCallbackInt(addonPtr, 0) &&
                        configuration.SemiAutoVerboseLogging)
                    {
                        log.Information("Semi-auto leve action: finish NPC A interaction via Talk callback 0");
                    }
                    break;
                }

                if (detectedAddon == "SelectString")
                {
                    // Accept 剛送出時可能短暫回到 SelectString；太早按取消會中斷接取流程。
                    if ((DateTime.UtcNow - lastGuildLeveAcceptActionAtUtc).TotalMilliseconds < GuildLevePostAcceptSettleMs)
                    {
                        if (configuration.SemiAutoVerboseLogging)
                        {
                            log.Information("Semi-auto leve action: hold SelectString cancel during post-accept settle");
                        }
                        break;
                    }

                    if (TryStartActionWindow(180))
                    {
                        if (TryFindCallbackIndexByText(addonPtr, "取消", out var cancelCb) &&
                            TryFireCallbackInt(addonPtr, cancelCb))
                        {
                            if (configuration.SemiAutoVerboseLogging)
                            {
                                log.Information("Semi-auto leve action: finish NPC A interaction via SelectString cancel callback {Callback}", cancelCb);
                            }
                            break;
                        }

                        // fallback: list 最後一項通常是取消
                        if (TryFireCallbackInt(addonPtr, 3) && configuration.SemiAutoVerboseLogging)
                        {
                            log.Information("Semi-auto leve action: finish NPC A interaction via SelectString callback 3");
                        }
                    }
                    break;
                }

                if (detectedAddon == "GuildLeve")
                {
                    if (!guildLeveExitAfterAcceptSent &&
                        (DateTime.UtcNow - lastGuildLeveAcceptActionAtUtc).TotalMilliseconds > GuildLeveExitAfterAcceptDelayMs)
                    {
                        var exitLeveId = configuration.SemiAutoM34TwoArgLeveId > 0
                            ? configuration.SemiAutoM34TwoArgLeveId
                            : configuration.SemiAutoGuildLeveSelectLeveId;
                        if (TryFireGuildLeveTwoArgCallback(addonPtr, 7, exitLeveId))
                        {
                            guildLeveExitAfterAcceptSent = true;
                            lastGuildLeveExitActionAtUtc = DateTime.UtcNow;
                            if (configuration.SemiAutoVerboseLogging)
                            {
                                log.Information("Semi-auto leve action: sent GuildLeve exit callback [7,{LeveId}] after accept", exitLeveId);
                            }
                            break;
                        }
                    }

                    if (guildLeveExitAfterAcceptSent &&
                        (DateTime.UtcNow - lastGuildLeveExitActionAtUtc).TotalMilliseconds > 1800)
                    {
                        var cancelCallback = ResolveGuildLeveCancelCallback(addonPtr);
                        if (TryFireCallbackInt(addonPtr, cancelCallback) && configuration.SemiAutoVerboseLogging)
                        {
                            log.Warning("Semi-auto leve action: GuildLeve exit timeout, fallback cancel callback {Callback}", cancelCallback);
                        }
                        guildLeveExitAfterAcceptSent = false;
                        lastGuildLeveExitActionAtUtc = DateTime.UtcNow;
                    }
                    break;
                }
                break;
        }
    }

    private bool TryStopIfLeveAllowanceExhausted(nint addonPtr)
    {
        if (!TryResolveLeveAllowance(addonPtr, out var allowance, out var source))
        {
            return false;
        }

        if (allowance > 0)
        {
            return false;
        }

        chatGui.Print("[autoLeve] 偵測到理符次數耗盡（受理限額=0），停止自動循環。");
        if (configuration.SemiAutoVerboseLogging)
        {
            log.Warning("Semi-auto leve stop: allowance exhausted (source={Source})", source);
        }
        Stop("理符次數耗盡（受理限額=0）");
        return true;
    }

    private unsafe bool TryResolveLeveAllowance(nint addonPtr, out int allowance, out string source)
    {
        allowance = -1;
        source = "(none)";
        if (addonPtr == nint.Zero)
        {
            return false;
        }

        var unitBase = (AtkUnitBase*)addonPtr;
        if (unitBase == null || !unitBase->IsVisible || unitBase->AtkValues == null)
        {
            return false;
        }

        var count = unitBase->AtkValuesCount;
        for (var i = 0; i < count; i++)
        {
            var text = unitBase->AtkValues[i].GetValueAsString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // 僅使用已驗證可靠的訊號：
            // 在「已經領取全部現有理符。」前一格會放剩餘次數（如 12、0）。
            // 注意：不要用「受理限額 0/16」判斷，該欄位的左值是已受理數，不是理符剩餘次數。
            if (text.Contains("已經領取全部現有理符", StringComparison.Ordinal))
            {
                for (var back = i - 1; back >= Math.Max(0, i - 6); back--)
                {
                    var prev = unitBase->AtkValues[back].GetValueAsString()?.Trim();
                    if (string.IsNullOrWhiteSpace(prev))
                    {
                        continue;
                    }

                    if (int.TryParse(prev, out allowance) && allowance >= 0 && allowance <= 100)
                    {
                        source = $"before-full-msg@{back}:{prev}";
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private void CompleteAFlowAfterAccept()
    {
        npcAStep = NpcAStep.AwaitTalk;
        guildLeveSelectStrategy = 0;
        lastGuildLeveDetailTitle = null;
        guildLeveNoProgressCount = 0;
        guildLeveTargetStableSinceUtc = DateTime.MinValue;
        guildLeveOpenedAtUtc = DateTime.MinValue;
        lastGuildLeveSelectCallbackAtUtc = DateTime.MinValue;
        guildLeveTitleBeforeSelectCallback = null;
        lastGuildLeveAcceptActionAtUtc = DateTime.MinValue;
        guildLeveAcceptRetryStage = 0;
        guildLeveExitAfterAcceptSent = false;
        lastGuildLeveExitActionAtUtc = DateTime.MinValue;
        if (configuration.SemiAutoTestFlowBEnabled)
        {
            EnterBFlow(configuration.SemiAutoTestFlowAEnabled);
            if (configuration.SemiAutoVerboseLogging)
            {
                log.Information("Semi-auto leve action: GuildLeve accept complete");
                log.Information("Semi-auto leve B flow: armed and waiting for turn-in dialogs");
            }
        }
        else
        {
            Stop("A 流程完成（B 測試停用）");
        }
    }

    private static bool IsNpcBTurnInAddon(string addonName)
    {
        return addonName is "Talk" or "SelectString" or "SelectYesno" or "Request" or "RequestItem" or "JournalResult";
    }

    private void EnterAFlow(bool retargetAttack1)
    {
        waitingForNpcBTurnIn = false;
        npcAStep = NpcAStep.AwaitTalk;
        npcBConfirmPressCount = 0;
        var now = DateTime.UtcNow;
        lastAutoInteractKeyAtUtc = now;
        autoInteractBlockedUntilUtc = now.AddMilliseconds(500);
        if (retargetAttack1)
        {
            TryTargetByMark("attack1");
            lastAutoRetargetAtUtc = now;
            autoInteractBlockedUntilUtc = now.AddMilliseconds(1200);
        }
    }

    private void EnterBFlow(bool retargetAttack2)
    {
        waitingForNpcBTurnIn = true;
        npcBStep = NpcBStep.AwaitTalkStart;
        npcBTurnInHadInteraction = false;
        npcBCompletionObserved = false;
        npcBTalkPhaseCount = 0;
        npcBRequestStageObserved = false;
        npcBReadyToFinishOnDialogClose = false;
        var now = DateTime.UtcNow;
        npcBTurnInStartedAtUtc = now;
        npcBTurnInLastInteractionAtUtc = now;
        npcBConfirmPressCount = 0;
        lastAutoInteractKeyAtUtc = now;
        autoInteractBlockedUntilUtc = now.AddMilliseconds(500);
        if (retargetAttack2)
        {
            TryTargetByMark("attack2");
            lastAutoRetargetAtUtc = now;
            autoInteractBlockedUntilUtc = now.AddMilliseconds(1300);
        }
    }

    private void CompleteBFlow(string reason, bool counted = false)
    {
        waitingForNpcBTurnIn = false;
        npcBStep = NpcBStep.AwaitTalkStart;
        npcBTurnInHadInteraction = false;
        npcBCompletionObserved = false;
        npcBTalkPhaseCount = 0;
        npcBRequestStageObserved = false;
        npcBReadyToFinishOnDialogClose = false;
        npcBTurnInStartedAtUtc = DateTime.MinValue;
        npcBTurnInLastInteractionAtUtc = DateTime.MinValue;
        npcBConfirmPressCount = 0;

        if (counted)
        {
            sessionTurnInCompletedCount++;

            if (configuration.SemiAutoTargetTurnInCount > 0 &&
                sessionTurnInCompletedCount >= configuration.SemiAutoTargetTurnInCount)
            {
                pendingSwitchToAAfterDialogClose = false;
                pendingSwitchToAStartedAtUtc = DateTime.MinValue;
                pendingStopAfterDialogClose = true;
                pendingStopReason = $"已達指定繳交次數：{sessionTurnInCompletedCount}/{configuration.SemiAutoTargetTurnInCount}";
                TransitionTo(SemiAutoLeveState.WaitingForNpcDialog);
                return;
            }
        }

        if (configuration.SemiAutoTestFlowAEnabled && configuration.SemiAutoTestFlowBEnabled)
        {
            pendingSwitchToAAfterDialogClose = true;
            pendingSwitchToAStartedAtUtc = DateTime.UtcNow;
            chatGui.Print("[autoLeve] B 流程完成，等待離開對話後切回 attack1。");
            TransitionTo(SemiAutoLeveState.WaitingForNpcDialog);
            return;
        }

        Stop(reason);
    }

    private void MarkCurrentTarget(string markName)
    {
        if (clientState.LocalPlayer == null)
        {
            chatGui.Print("[autoLeve] 尚未登入角色。");
            return;
        }

        if (clientState.LocalPlayer.TargetObject == null)
        {
            chatGui.Print("[autoLeve] 目前沒有目標，請先選中 NPC。");
            return;
        }

        var cmd = $"/mk {markName} <t>";
        var ok = TrySendChatCommand(cmd);
        chatGui.Print(ok
            ? $"[autoLeve] 已標記目前目標為 {markName}。"
            : $"[autoLeve] 標記失敗：{cmd}");
    }

    private void TryTargetByMark(string markName)
    {
        var ok = TrySendChatCommand($"/ta <{markName}>");
        if (!ok)
        {
            ok = TrySendChatCommand($"/target <{markName}>");
        }

        if (configuration.SemiAutoVerboseLogging)
        {
            log.Information("Semi-auto leve target by mark {Mark}: {Result}", markName, ok ? "ok" : "failed");
        }
    }

    private NpcFlowMode ResolveFlowMode(NpcFlowMode positionalMode, string? detectedAddon)
    {
        if (positionalMode != NpcFlowMode.Unknown)
        {
            return positionalMode;
        }

        if (string.IsNullOrEmpty(detectedAddon))
        {
            return NpcFlowMode.Unknown;
        }

        return detectedAddon switch
        {
            "GuildLeve" => NpcFlowMode.NpcA,
            "Talk" => NpcFlowMode.NpcA,
            "SelectString" => NpcFlowMode.Unknown,
            "JournalResult" => lastMode == NpcFlowMode.Unknown ? NpcFlowMode.NpcA : lastMode,
            _ => NpcFlowMode.Unknown,
        };
    }

    private (string AddonName, nint AddonPtr)? FindVisibleWatchedAddon()
    {
        foreach (var addonName in WatchedAddonNames)
        {
            var addon = gameGui.GetAddonByName(addonName);
            if (addon == nint.Zero)
            {
                continue;
            }

            unsafe
            {
                var unitBase = (AtkUnitBase*)addon;
                if (unitBase != null && unitBase->IsVisible)
                {
                    return (addonName, addon);
                }
            }
        }

        return null;
    }

    private void TransitionTo(SemiAutoLeveState newState)
    {
        if (State == newState)
        {
            return;
        }

        State = newState;
        if (configuration.SemiAutoVerboseLogging)
        {
            log.Information("Semi-auto leve state -> {State}", newState);
        }
    }

    private bool TryMarkPhaseLogged(string key)
    {
        if (lastPhaseLogKey == key)
        {
            return false;
        }

        lastPhaseLogKey = key;
        return true;
    }

    private bool TryStartActionWindow(int? overrideDelayMs = null)
    {
        var now = DateTime.UtcNow;
        var actionDelayMs = overrideDelayMs ?? Math.Clamp(configuration.SemiAutoActionDelayMs, 150, 5000);
        if ((now - lastActionAtUtc).TotalMilliseconds < actionDelayMs)
        {
            return false;
        }

        lastActionAtUtc = now;
        return true;
    }

    private NpcFlowMode DetectFlowModeByPosition()
    {
        var player = clientState.LocalPlayer;
        if (player == null)
        {
            return NpcFlowMode.Unknown;
        }

        var territory = clientState.TerritoryType;
        var playerPos = player.Position;
        var radius = Math.Max(1f, configuration.NpcDetectRadius);
        var radiusSq = radius * radius;

        var inA = configuration.NpcAConfigured &&
                  configuration.NpcATerritory == territory &&
                  Vector3.DistanceSquared(playerPos, new Vector3(configuration.NpcAX, configuration.NpcAY, configuration.NpcAZ)) <= radiusSq;

        if (inA)
        {
            return NpcFlowMode.NpcA;
        }

        return NpcFlowMode.Unknown;
    }

    private unsafe bool TryFindCallbackIndexByText(nint addonPtr, string targetText, out int callbackIndex)
    {
        callbackIndex = -1;
        if (addonPtr == nint.Zero || string.IsNullOrWhiteSpace(targetText))
        {
            return false;
        }

        var unitBase = (AtkUnitBase*)addonPtr;
        if (unitBase == null || !unitBase->IsVisible || unitBase->AtkValues == null || unitBase->AtkValuesCount <= 0)
        {
            return false;
        }

        for (var i = 0; i < unitBase->AtkValuesCount; i++)
        {
            var text = unitBase->AtkValues[i].GetValueAsString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text.Contains(targetText, StringComparison.OrdinalIgnoreCase))
            {
                callbackIndex = i;
                return true;
            }
        }

        return false;
    }

    private int ResolveGuildLeveAcceptCallback(nint addonPtr)
    {
        if (TryFindCallbackIndexByText(addonPtr, "接受", out var callbackFromText))
        {
            return callbackFromText;
        }

        // fallback: 保留手動設定值，避免特殊語系或 UI 差異時完全失效
        return Math.Clamp(configuration.SemiAutoGuildLeveAcceptCallback, 0, 2500);
    }

    private int ResolveGuildLeveCancelCallback(nint addonPtr)
    {
        if (TryFindCallbackIndexByText(addonPtr, "取消", out var callbackFromText))
        {
            return callbackFromText;
        }

        return 1492;
    }

    private unsafe bool TryResolveGuildLeveStepCallback(nint addonPtr, string targetText, out int callbackIndex)
    {
        callbackIndex = -1;
        if (addonPtr == nint.Zero || string.IsNullOrWhiteSpace(targetText))
        {
            return false;
        }

        var unitBase = (AtkUnitBase*)addonPtr;
        if (unitBase == null || !unitBase->IsVisible || unitBase->AtkValues == null || unitBase->AtkValuesCount <= 0)
        {
            return false;
        }

        if (!TryCollectGuildLeveEntries(unitBase, out var entries) || entries.Count == 0)
        {
            if (configuration.SemiAutoVerboseLogging)
            {
                log.Warning("Semi-auto leve resolve(step): no leve entries collected");
            }
            return false;
        }

        if (configuration.SemiAutoVerboseLogging)
        {
            log.Information("Semi-auto leve collect: entries={Count}", entries.Count);
        }

        var normalizedTarget = NormalizeText(targetText);
        var targetPos = entries.FindIndex(x => string.Equals(x.Title, normalizedTarget, StringComparison.OrdinalIgnoreCase));
        if (targetPos < 0)
        {
            if (configuration.SemiAutoVerboseLogging)
            {
                log.Warning("Semi-auto leve resolve(step): target not found in entries, target={Target}", targetText);
            }
            return false;
        }

        var currentTitle = TryGetGuildLeveDetailTitle(unitBase);
        var currentPos = string.IsNullOrEmpty(currentTitle)
            ? -1
            : entries.FindIndex(x => string.Equals(x.Title, currentTitle, StringComparison.OrdinalIgnoreCase));

        var nextPos = targetPos;
        if (currentPos >= 0 && currentPos != targetPos)
        {
            nextPos = currentPos + Math.Sign(targetPos - currentPos);
        }

        var callbackSource = "list-index";
        callbackIndex = ResolveGuildLeveEntryCallback(entries[nextPos]);

        // strategy=1: 改用文字命中的 raw callback，避免不同 UI 結構下 list-index 對應錯位。
        if (guildLeveSelectStrategy == 1 &&
            TryResolveGuildLeveRawCallbackByTitle(unitBase, entries[nextPos].Title, out var rawCallback))
        {
            callbackIndex = rawCallback;
            callbackSource = "raw-text";
        }

        log.Information(
            "Semi-auto leve resolve(step): current={CurrentTitle}/{CurrentPos}, target={Target}/{TargetPos}, next={NextTitle}/{NextPos}, cb={Callback}, strategy={Strategy}, using={Source}",
            currentTitle ?? "(unknown)",
            currentPos,
            targetText,
            targetPos,
            entries[nextPos].Title,
            nextPos,
            callbackIndex,
            guildLeveSelectStrategy,
            callbackSource);

        return true;
    }

    private unsafe bool TryCollectGuildLeveEntries(AtkUnitBase* unitBase, out List<GuildLeveEntry> entries)
    {
        entries = new List<GuildLeveEntry>();
        if (unitBase == null || unitBase->AtkValues == null || unitBase->AtkValuesCount <= 0)
        {
            return false;
        }

        for (var i = 0; i < unitBase->AtkValuesCount; i++)
        {
            var text = unitBase->AtkValues[i].GetValueAsString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var normalized = NormalizeText(text);
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            if (!LooksLikeLeveListTitle(normalized))
            {
                continue;
            }

            var titleOnly = ExtractLeveTitle(normalized);
            if (string.IsNullOrEmpty(titleOnly))
            {
                continue;
            }

            // 部分版本/語系在清單列與等級欄間會有跳號，向後容忍多格避免漏收。
            var hasLevelCell = false;
            for (var offset = 1; offset <= GuildLeveLevelCellMaxLookahead; offset++)
            {
                var nextIdx = i + offset;
                if (nextIdx >= unitBase->AtkValuesCount)
                {
                    break;
                }

                var nextText = NormalizeText(unitBase->AtkValues[nextIdx].GetValueAsString() ?? string.Empty);
                if (LooksLikeLevelCell(nextText))
                {
                    hasLevelCell = true;
                    break;
                }

                if (LooksLikeLeveListTitle(nextText))
                {
                    break;
                }
            }

            if (!hasLevelCell)
            {
                if (configuration.SemiAutoVerboseLogging)
                {
                    log.Debug("Semi-auto leve collect: skip rawIdx={RawIndex}, title={Title}, reason=no level cell nearby", i, titleOnly);
                }
                continue;
            }

            entries.Add(new GuildLeveEntry(entries.Count, titleOnly));
        }

        return entries.Count > 0;
    }

    private unsafe bool TryResolveGuildLeveRawCallbackByTitle(AtkUnitBase* unitBase, string title, out int callbackIndex)
    {
        callbackIndex = -1;
        if (unitBase == null || unitBase->AtkValues == null || unitBase->AtkValuesCount <= 0 || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var normalizedTitle = NormalizeText(title);
        for (var i = 0; i < unitBase->AtkValuesCount; i++)
        {
            var text = unitBase->AtkValues[i].GetValueAsString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var normalized = NormalizeText(text);
            if (!LooksLikeLeveListTitle(normalized))
            {
                continue;
            }

            var titleOnly = ExtractLeveTitle(normalized);
            if (!string.Equals(titleOnly, normalizedTitle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            callbackIndex = i;
            return true;
        }

        return false;
    }

    private unsafe bool TryFireConfiguredGuildLeveCallback(nint addonPtr, string targetText)
    {
        if (addonPtr == nint.Zero || string.IsNullOrWhiteSpace(targetText))
        {
            if (configuration.SemiAutoVerboseLogging)
            {
                log.Warning("Semi-auto leve action: configured callback skipped (invalid addon/target)");
            }
            return false;
        }

        if (configuration.SemiAutoGuildLeveSelectLeveId <= 0)
        {
            if (configuration.SemiAutoVerboseLogging)
            {
                log.Warning("Semi-auto leve action: configured callback skipped (leveId <= 0)");
            }
            return false;
        }

        if ((DateTime.UtcNow - lastConfiguredGuildLeveCallbackAtUtc).TotalMilliseconds < 250)
        {
            return false;
        }

        var unitBase = (AtkUnitBase*)addonPtr;
        if (unitBase == null || !unitBase->IsVisible)
        {
            if (configuration.SemiAutoVerboseLogging)
            {
                log.Warning("Semi-auto leve action: configured callback skipped (GuildLeve not visible)");
            }
            return false;
        }

        var values = stackalloc AtkValue[3];
        values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        values[0].Int = configuration.SemiAutoGuildLeveSelectArg0;
        values[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        values[1].Int = configuration.SemiAutoGuildLeveSelectArg1;
        values[2].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        values[2].Int = configuration.SemiAutoGuildLeveSelectLeveId;
        unitBase->FireCallback(3, values, true);
        lastConfiguredGuildLeveCallbackAtUtc = DateTime.UtcNow;
        chatGui.Print($"[autoLeve] M3-3 使用 callback: [{configuration.SemiAutoGuildLeveSelectArg0},{configuration.SemiAutoGuildLeveSelectArg1},{configuration.SemiAutoGuildLeveSelectLeveId}]");

        log.Warning(
            "Semi-auto leve action: force configured callback [{Arg0},{Arg1},{LeveId}] for target={Target}",
            configuration.SemiAutoGuildLeveSelectArg0,
            configuration.SemiAutoGuildLeveSelectArg1,
            configuration.SemiAutoGuildLeveSelectLeveId,
            targetText);
        return true;
    }

    private unsafe bool TryFireGuildLeveTwoArgCallback(nint addonPtr, int cmd, int leveId)
    {
        if (addonPtr == nint.Zero || leveId <= 0)
        {
            return false;
        }

        var unitBase = (AtkUnitBase*)addonPtr;
        if (unitBase == null || !unitBase->IsVisible)
        {
            return false;
        }

        var values = stackalloc AtkValue[2];
        values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        values[0].Int = cmd;
        values[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt;
        values[1].UInt = (uint)Math.Max(0, leveId);
        unitBase->FireCallback(2, values, true);
        return true;
    }

    private bool TryFireDebugRequestCallback(int callback)
    {
        foreach (var addonName in new[] { "Request", "RequestItem" })
        {
            var addonPtr = gameGui.GetAddonByName(addonName);
            if (TryFireCallbackInt(addonPtr, callback))
            {
                if (configuration.SemiAutoVerboseLogging)
                {
                    log.Information("Semi-auto leve debug B: {Addon} callback {Callback}", addonName, callback);
                }
                return true;
            }
        }

        return false;
    }

    private static unsafe void FillGenericAtkValue(AtkValue* values, int index, int rawType, int rawValue)
    {
        var type = (FFXIVClientStructs.FFXIV.Component.GUI.ValueType)rawType;
        values[index].Type = type;
        if (type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt)
        {
            values[index].UInt = unchecked((uint)rawValue);
        }
        else
        {
            values[index].Int = rawValue;
        }
    }

    private int ResolveGuildLeveEntryCallback(GuildLeveEntry entry)
    {
        // strategy=0: ListIndex。strategy=1 時會優先嘗試 raw-text callback。
        return guildLeveSelectStrategy switch
        {
            1 => entry.ListIndex + 1,
            _ => entry.ListIndex,
        };
    }

    private void AdvanceGuildLeveSelectStrategy(string reason)
    {
        guildLeveSelectStrategy = (guildLeveSelectStrategy + 1) % 2;
        log.Warning("Semi-auto leve select strategy -> {Strategy} ({Reason})", guildLeveSelectStrategy, reason);
    }

    private unsafe string? TryGetGuildLeveDetailTitle(AtkUnitBase* unitBase)
    {
        if (unitBase == null || unitBase->AtkValues == null || unitBase->AtkValuesCount <= 0)
        {
            return null;
        }

        for (var i = unitBase->AtkValuesCount - 1; i >= 0; i--)
        {
            var text = unitBase->AtkValues[i].GetValueAsString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var normalized = NormalizeText(text);
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            var isLeveText =
                normalized.Contains("委託", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("任務", StringComparison.OrdinalIgnoreCase);
            if (!isLeveText)
            {
                continue;
            }

            var titleOnly = ExtractLeveTitle(normalized);
            if (!string.IsNullOrEmpty(titleOnly))
            {
                // 只以文字結構判斷「詳情區」：標題後方常伴隨長段描述/委託人文字，列表項通常沒有。
                if (HasLikelyGuildLeveDetailContext(unitBase, i))
                {
                    return titleOnly;
                }
            }
        }

        return null;
    }

    private unsafe string? TryGetGuildLeveDetailTitle(nint addonPtr)
    {
        if (addonPtr == nint.Zero)
        {
            return null;
        }

        return TryGetGuildLeveDetailTitle((AtkUnitBase*)addonPtr);
    }

    private unsafe bool IsGuildLeveTargetSelected(nint addonPtr, string targetText)
    {
        if (addonPtr == nint.Zero || string.IsNullOrWhiteSpace(targetText))
        {
            return false;
        }

        if (IsTargetTitle(TryGetGuildLeveDetailTitle(addonPtr), targetText))
        {
            return true;
        }

        var unitBase = (AtkUnitBase*)addonPtr;
        if (unitBase == null || !unitBase->IsVisible || unitBase->AtkValues == null || unitBase->AtkValuesCount <= 0)
        {
            return false;
        }

        var normalizedTarget = NormalizeText(targetText);
        var matchedCount = 0;
        var hasDetailMatch = false;

        for (var i = 0; i < unitBase->AtkValuesCount; i++)
        {
            var text = unitBase->AtkValues[i].GetValueAsString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var titleOnly = ExtractLeveTitle(NormalizeText(text));
            if (!string.Equals(titleOnly, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matchedCount++;
            if (i >= 1000)
            {
                hasDetailMatch = true;
                break;
            }
        }

        // 至少要有詳情命中，或同名文字出現兩次(列表 + 詳情)才視為選對。
        return hasDetailMatch || matchedCount >= 2;
    }

    private static bool IsTargetTitle(string? currentTitle, string targetText)
    {
        if (string.IsNullOrWhiteSpace(currentTitle) || string.IsNullOrWhiteSpace(targetText))
        {
            return false;
        }

        return string.Equals(
            NormalizeText(currentTitle),
            NormalizeText(targetText),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeLeveListTitle(string normalizedText)
    {
        if (string.IsNullOrEmpty(normalizedText))
        {
            return false;
        }

        return normalizedText.StartsWith("製作委託：", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.StartsWith("採集委託：", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeLevelCell(string normalizedText)
    {
        if (string.IsNullOrEmpty(normalizedText) || !normalizedText.EndsWith("級", StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(normalizedText[..^1], out _);
    }

    private static string ExtractLeveTitle(string text)
    {
        var sepIdx = text.IndexOf('：');
        if (sepIdx < 0)
        {
            sepIdx = text.IndexOf(':');
        }

        if (sepIdx >= 0 && sepIdx + 1 < text.Length)
        {
            return NormalizeText(text[(sepIdx + 1)..]);
        }

        return text;
    }

    private static string NormalizeText(string text)
    {
        return string.Concat(text.Where(c => !char.IsWhiteSpace(c))).Trim();
    }

    private unsafe bool HasLikelyGuildLeveDetailContext(AtkUnitBase* unitBase, int titleIndex)
    {
        if (unitBase == null || unitBase->AtkValues == null || unitBase->AtkValuesCount <= 0)
        {
            return false;
        }

        var hasLongParagraphNearby = false;
        var hasNpcLabelNearby = false;
        var maxLookahead = Math.Min(16, unitBase->AtkValuesCount - titleIndex - 1);
        for (var offset = 1; offset <= maxLookahead; offset++)
        {
            var raw = unitBase->AtkValues[titleIndex + offset].GetValueAsString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var normalized = NormalizeText(raw);
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            if (LooksLikeLeveListTitle(normalized))
            {
                break;
            }

            if (normalized.Length >= 24 && (normalized.Contains('。') || normalized.Contains('，') || normalized.Contains('、')))
            {
                hasLongParagraphNearby = true;
            }

            if (normalized.Contains("店主", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("：", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("萊韋耶勒爾", StringComparison.OrdinalIgnoreCase))
            {
                hasNpcLabelNearby = true;
            }

            if (hasLongParagraphNearby || hasNpcLabelNearby)
            {
                return true;
            }
        }

        return false;
    }

    private unsafe void OnFireCallbackDetour(AtkUnitBase* unitBase, uint valueCount, AtkValue* values, bool updateState)
    {
        fireCallbackHook.Original(unitBase, valueCount, values, updateState);

        var isGuildCaptureActive = IsGuildLeveCallbackCaptureActive();
        var isNpcBCaptureActive = IsNpcBCallbackCaptureActive();
        var isAnyCaptureActive = IsAnyCallbackCaptureActive();
        var isFlowDiagActive = IsFlowDiagnosticCaptureActive();
        if (!isGuildCaptureActive && !isNpcBCaptureActive && !isAnyCaptureActive && !isFlowDiagActive)
        {
            return;
        }

        var callerAddon = ResolveAddonNameByUnitBase((nint)unitBase);
        if (callerAddon is null)
        {
            if (isAnyCaptureActive)
            {
                LogGenericCapturedCallback("(unknown)", (nint)unitBase, valueCount, values, updateState);
            }
            if (isFlowDiagActive)
            {
                LogFlowDiagnosticCapturedCallback("(unknown)", (nint)unitBase, valueCount, values, updateState);
            }
            if (isGuildCaptureActive && guildLeveCaptureMode == GuildLeveCaptureMode.AcceptOnly)
            {
                var countUnknown = (int)valueCount;
                log.Warning(
                    "Semi-auto leve hook: unknown addon callback while waiting accept, ptr=0x{Ptr:X}, count={Count}, updateState={UpdateState}",
                    (nint)unitBase,
                    countUnknown,
                    updateState);
                for (var i = 0; i < countUnknown; i++)
                {
                    log.Warning("Semi-auto leve hook: unknown cb[{Index}]={Value}", i, DescribeAtkValue(values[i]));
                }
            }
            if (isNpcBCaptureActive)
            {
                var countUnknown = (int)valueCount;
                log.Warning(
                    "Semi-auto leve hook: unknown addon callback while waiting B flow, ptr=0x{Ptr:X}, count={Count}, updateState={UpdateState}",
                    (nint)unitBase,
                    countUnknown,
                    updateState);
                for (var i = 0; i < countUnknown; i++)
                {
                    log.Warning("Semi-auto leve hook: B unknown cb[{Index}]={Value}", i, DescribeAtkValue(values[i]));
                }
            }
            return;
        }

        var count = (int)valueCount;

        if (isAnyCaptureActive)
        {
            LogGenericCapturedCallback(callerAddon, (nint)unitBase, valueCount, values, updateState);
        }
        if (isFlowDiagActive)
        {
            LogFlowDiagnosticCapturedCallback(callerAddon, (nint)unitBase, valueCount, values, updateState);
        }

        if (isGuildCaptureActive && callerAddon == "GuildLeve")
        {
            if (guildLeveCaptureMode == GuildLeveCaptureMode.AcceptOnly &&
                LooksLikeGuildLeveSelectionCallback(valueCount, values))
            {
                if (configuration.SemiAutoVerboseLogging)
                {
                    log.Information("Semi-auto leve hook: skip GuildLeve select-like callback while waiting accept (count={Count})", count);
                    for (var i = 0; i < count; i++)
                    {
                        log.Information("Semi-auto leve hook: skipped GuildLeve cb[{Index}]={Value}", i, DescribeAtkValue(values[i]));
                    }
                }
                return;
            }

            guildLeveCallbackCaptureRemaining--;
            log.Warning(
                "Semi-auto leve hook: GuildLeve callback captured, count={Count}, updateState={UpdateState}, remaining={Remaining}",
                count,
                updateState,
                guildLeveCallbackCaptureRemaining);

            for (var i = 0; i < count; i++)
            {
                log.Warning("Semi-auto leve hook: GuildLeve cb[{Index}]={Value}", i, DescribeAtkValue(values[i]));
            }

            if (guildLeveCallbackCaptureRemaining <= 0)
            {
                guildLeveCallbackCaptureArmed = false;
                chatGui.Print("[autoLeve] GuildLeve callback 捕捉結束（已達上限）。");
            }
            else if (guildLeveCaptureMode == GuildLeveCaptureMode.AcceptOnly)
            {
                guildLeveCallbackCaptureArmed = false;
                chatGui.Print("[autoLeve] M3-4 接受 callback 已捕捉完成。");
            }
        }

        if (isNpcBCaptureActive && IsNpcBTurnInAddon(callerAddon))
        {
            npcBCallbackCaptureRemaining--;
            log.Warning(
                "Semi-auto leve hook: B callback captured, addon={Addon}, count={Count}, updateState={UpdateState}, remaining={Remaining}",
                callerAddon,
                count,
                updateState,
                npcBCallbackCaptureRemaining);

            for (var i = 0; i < count; i++)
            {
                log.Warning("Semi-auto leve hook: B {Addon} cb[{Index}]={Value}", callerAddon, i, DescribeAtkValue(values[i]));
            }

            if (npcBCallbackCaptureRemaining <= 0)
            {
                npcBCallbackCaptureArmed = false;
                chatGui.Print("[autoLeve] B callback 捕捉結束（已達上限）。");
            }
        }
    }

    private bool IsGuildLeveCallbackCaptureActive()
    {
        if (!guildLeveCallbackCaptureArmed)
        {
            return false;
        }

        if (guildLeveCallbackCaptureRemaining <= 0 || DateTime.UtcNow > guildLeveCallbackCaptureUntilUtc)
        {
            guildLeveCallbackCaptureArmed = false;
            chatGui.Print("[autoLeve] GuildLeve callback 捕捉結束（逾時/已停止）。");
            return false;
        }

        return true;
    }

    private bool IsNpcBCallbackCaptureActive()
    {
        if (!npcBCallbackCaptureArmed)
        {
            return false;
        }

        if (npcBCallbackCaptureRemaining <= 0 || DateTime.UtcNow > npcBCallbackCaptureUntilUtc)
        {
            npcBCallbackCaptureArmed = false;
            chatGui.Print("[autoLeve] B callback 捕捉結束（逾時/已停止）。");
            return false;
        }

        return true;
    }

    private bool IsAnyCallbackCaptureActive()
    {
        if (!anyCallbackCaptureArmed)
        {
            return false;
        }

        if (anyCallbackCaptureRemaining <= 0 || DateTime.UtcNow > anyCallbackCaptureUntilUtc)
        {
            anyCallbackCaptureArmed = false;
            if (!anyCallbackCaptureSawData)
            {
                chatGui.Print("[autoLeve] 通用 callback 捕捉結束：0筆。這一步可能不是 FireCallback（可能是 DragDrop/事件鏈）。");
            }
            else
            {
                chatGui.Print("[autoLeve] 通用 callback 捕捉結束（逾時/已停止）。");
            }
            return false;
        }

        return true;
    }

    private bool IsFlowDiagnosticCaptureActive()
    {
        if (!flowDiagnosticCaptureArmed)
        {
            return false;
        }

        if (flowDiagnosticCaptureRemaining <= 0 || DateTime.UtcNow > flowDiagnosticCaptureUntilUtc)
        {
            flowDiagnosticCaptureArmed = false;
            chatGui.Print("[autoLeve] 流程診斷捕捉結束（逾時/已停止）。");
            return false;
        }

        return true;
    }

    private bool IsAnyReceiveCaptureActive()
    {
        if (!anyReceiveCaptureArmed)
        {
            return false;
        }

        if (anyReceiveCaptureRemaining <= 0 || DateTime.UtcNow > anyReceiveCaptureUntilUtc)
        {
            anyReceiveCaptureArmed = false;
            ClearAddonEventCaptureHandles();
            if (!anyReceiveCaptureSawData)
            {
                chatGui.Print("[autoLeve] ReceiveEvent 捕捉結束：0筆。此操作可能不是 UI ReceiveEvent。");
            }
            else
            {
                chatGui.Print("[autoLeve] ReceiveEvent 捕捉結束（逾時/已停止）。");
            }
            return false;
        }

        return true;
    }

    private void OnAddonPreReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (!IsAnyReceiveCaptureActive())
        {
            return;
        }

        if (args is not AddonReceiveEventArgs receive)
        {
            return;
        }

        anyReceiveCaptureSawData = true;
        anyReceiveCaptureRemaining--;

        var addonName = args.AddonName;
        lastReceiveAddonName = addonName;
        lastReceiveEventType = (int)receive.AtkEventType;
        lastReceiveEventParam = receive.EventParam;
        lastReceiveCaptureSummary =
            $"{addonName} type={(int)receive.AtkEventType} param={receive.EventParam} event=0x{receive.AtkEvent:X} data=0x{receive.Data:X}";

        log.Warning(
            "Semi-auto leve receive capture: addon={Addon}, eventType={EventType}, param={Param}, eventPtr=0x{EventPtr:X}, dataPtr=0x{DataPtr:X}, remaining={Remaining}",
            addonName,
            (int)receive.AtkEventType,
            receive.EventParam,
            receive.AtkEvent,
            receive.Data,
            anyReceiveCaptureRemaining);

        if (anyReceiveCaptureRemaining <= 0)
        {
            anyReceiveCaptureArmed = false;
            ClearAddonEventCaptureHandles();
            chatGui.Print("[autoLeve] ReceiveEvent 單步捕捉完成。");
        }
    }

    private void TryAttachAddonEventCapture()
    {
        if (!anyReceiveCaptureArmed || addonEventCaptureHandles.Count > 0)
        {
            return;
        }

        foreach (var addonName in new[] { "Request", "RequestItem", "InventoryExpansion" })
        {
            var addonPtr = gameGui.GetAddonByName(addonName);
            if (addonPtr == nint.Zero)
            {
                continue;
            }

            unsafe
            {
                var unit = (AtkUnitBase*)addonPtr;
                if (unit == null || !unit->IsVisible)
                {
                    continue;
                }

                var rootNodePtr = (nint)unit->RootNode;
                if (rootNodePtr == nint.Zero)
                {
                    continue;
                }

                var registered = 0;
                foreach (var eventType in CaptureEventTypes)
                {
                    var handle = addonEventManager.AddEvent(addonPtr, rootNodePtr, eventType, OnAddonEventCaptured);
                    if (handle != null)
                    {
                        addonEventCaptureHandles.Add(handle);
                        registered++;
                    }
                }

                if (addonEventCaptureHandles.Count > 0)
                {
                    addonEventCaptureAddonPtr = addonPtr;
                    addonEventCaptureAddonName = addonName;
                    chatGui.Print($"[autoLeve] 已綁定 UI事件捕捉: addon={addonName}, root=0x{rootNodePtr:X}, events={registered}");
                    return;
                }
            }
        }

        chatGui.Print("[autoLeve] UI事件捕捉尚未綁定：請先打開 Request/RequestItem 視窗再按一次。");
    }

    private void ClearAddonEventCaptureHandles()
    {
        if (addonEventCaptureHandles.Count == 0)
        {
            return;
        }

        foreach (var handle in addonEventCaptureHandles)
        {
            addonEventManager.RemoveEvent(handle);
        }

        addonEventCaptureHandles.Clear();
        addonEventCaptureAddonPtr = nint.Zero;
        addonEventCaptureAddonName = null;
    }

    private void OnAddonEventCaptured(AddonEventType eventType, AddonEventData data)
    {
        if (!IsAnyReceiveCaptureActive())
        {
            return;
        }

        anyReceiveCaptureSawData = true;
        anyReceiveCaptureRemaining--;

        var addonName = addonEventCaptureAddonName ?? ResolveAddonNameByUnitBase(data.AddonPointer) ?? "(unknown)";
        lastReceiveAddonName = addonName;
        lastReceiveEventType = (int)data.AtkEventType;
        lastReceiveEventParam = unchecked((int)data.Param);
        lastReceiveCaptureSummary =
            $"{addonName} mgrType={(int)eventType} atkType={(int)data.AtkEventType} param={data.Param} addon=0x{data.AddonPointer:X} node=0x{data.NodeTargetPointer:X}";

        log.Warning(
            "Semi-auto leve addon-event capture: addon={Addon}, mgrType={MgrType}, atkType={AtkType}, param={Param}, addonPtr=0x{AddonPtr:X}, nodePtr=0x{NodePtr:X}, remaining={Remaining}",
            addonName,
            (int)eventType,
            (int)data.AtkEventType,
            data.Param,
            data.AddonPointer,
            data.NodeTargetPointer,
            anyReceiveCaptureRemaining);

        if (anyReceiveCaptureRemaining <= 0)
        {
            anyReceiveCaptureArmed = false;
            ClearAddonEventCaptureHandles();
            chatGui.Print("[autoLeve] ReceiveEvent 單步捕捉完成。");
        }
    }

    private unsafe void LogGenericCapturedCallback(string addonName, nint unitBasePtr, uint valueCount, AtkValue* values, bool updateState)
    {
        anyCallbackCaptureRemaining--;
        anyCallbackCaptureSawData = true;
        var count = (int)valueCount;
        var argText = string.Join(", ", Enumerable.Range(0, count).Select(i => DescribeAtkValue(values[i])));
        lastGenericCaptureSummary = $"{addonName} ptr=0x{unitBasePtr:X} count={count} updateState={updateState} args=[{argText}]";
        lastCapturedAddonName = addonName;
        lastCapturedCount = Math.Min(count, lastCapturedValues.Length);
        for (var i = 0; i < lastCapturedCount; i++)
        {
            lastCapturedValues[i] = new CapturedAtkValue(values[i]);
        }
        log.Warning(
            "Semi-auto leve hook: generic callback captured, addon={Addon}, ptr=0x{Ptr:X}, count={Count}, updateState={UpdateState}, remaining={Remaining}",
            addonName,
            unitBasePtr,
            count,
            updateState,
            anyCallbackCaptureRemaining);

        for (var i = 0; i < count; i++)
        {
            log.Warning("Semi-auto leve hook: generic {Addon} cb[{Index}]={Value}", addonName, i, DescribeAtkValue(values[i]));
        }

        if (anyCallbackCaptureRemaining <= 0)
        {
            anyCallbackCaptureArmed = false;
            chatGui.Print("[autoLeve] 通用 callback 捕捉結束（已達上限）。");
        }
    }

    private unsafe void LogFlowDiagnosticCapturedCallback(string addonName, nint unitBasePtr, uint valueCount, AtkValue* values, bool updateState)
    {
        flowDiagnosticCaptureRemaining--;
        var count = (int)valueCount;
        log.Warning(
            "Semi-auto leve hook: flowdiag captured, addon={Addon}, ptr=0x{Ptr:X}, count={Count}, updateState={UpdateState}, step={Step}, waitingB={WaitingB}, lastAddon={LastAddon}, remaining={Remaining}",
            addonName,
            unitBasePtr,
            count,
            updateState,
            npcAStep,
            waitingForNpcBTurnIn,
            lastDetectedAddon ?? "(none)",
            flowDiagnosticCaptureRemaining);

        for (var i = 0; i < count; i++)
        {
            log.Warning("Semi-auto leve hook: flowdiag {Addon} cb[{Index}]={Value}", addonName, i, DescribeAtkValue(values[i]));
        }

        LogVisibleAddonPointersForDiagnostic();

        if (flowDiagnosticCaptureRemaining <= 0)
        {
            flowDiagnosticCaptureArmed = false;
            chatGui.Print("[autoLeve] 流程診斷捕捉結束（已達上限）。");
        }
    }

    private void LogVisibleAddonPointersForDiagnostic()
    {
        foreach (var addonName in WatchedAddonNames)
        {
            var ptr = gameGui.GetAddonByName(addonName);
            if (ptr == nint.Zero)
            {
                continue;
            }

            unsafe
            {
                var unit = (AtkUnitBase*)ptr;
                if (unit == null || !unit->IsVisible)
                {
                    continue;
                }
            }

            log.Warning("Semi-auto leve hook: flowdiag visible addon={Addon}, ptr=0x{Ptr:X}", addonName, ptr);
        }
    }

    private string? ResolveAddonNameByUnitBase(nint unitBasePtr)
    {
        foreach (var addonName in WatchedAddonNames)
        {
            var ptr = gameGui.GetAddonByName(addonName);
            if (ptr != nint.Zero && ptr == unitBasePtr)
            {
                return addonName;
            }
        }

        return null;
    }

    private readonly record struct CapturedAtkValue(FFXIVClientStructs.FFXIV.Component.GUI.ValueType Type, int Int, uint UInt)
    {
        public CapturedAtkValue(AtkValue value)
            : this(value.Type, value.Int, value.UInt)
        {
        }
    }

    private static unsafe string DescribeAtkValue(AtkValue value)
    {
        return value.Type switch
        {
            FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int => $"Int:{value.Int}",
            FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt => $"UInt:{value.UInt}",
            FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool => $"Bool:{(value.Byte != 0)}",
            FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Float => $"Float:{value.Float}",
            FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String => $"String:\"{ReadUtf8String(value.String)}\"",
            _ => $"{value.Type}:{value.Int}",
        };
    }

    private static unsafe bool LooksLikeGuildLeveSelectionCallback(uint valueCount, AtkValue* values)
    {
        if (valueCount < 3 || values == null)
        {
            return false;
        }

        if (values[0].Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int ||
            values[1].Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int ||
            values[2].Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int)
        {
            return false;
        }

        // 已知 M3-3 選理符常見 signature: [13, x, 16xx]
        return values[0].Int == 13 && values[2].Int >= 1600 && values[2].Int <= 1999;
    }

    private static unsafe string ReadUtf8String(byte* ptr)
    {
        if (ptr == null)
        {
            return string.Empty;
        }

        var text = Marshal.PtrToStringUTF8((nint)ptr);
        return text ?? string.Empty;
    }

    private sealed record GuildLeveEntry(int ListIndex, string Title);

    private unsafe bool TryFireCallbackInt(nint addonPtr, int value)
    {
        if (addonPtr == nint.Zero)
        {
            return false;
        }

        var unitBase = (AtkUnitBase*)addonPtr;
        if (unitBase == null || !unitBase->IsVisible)
        {
            return false;
        }

        unitBase->FireCallbackInt(value);
        return true;
    }

    private unsafe bool TrySendChatCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        if (framework == null)
        {
            return false;
        }

        var uiModule = framework->GetUIModule();
        if (uiModule == null)
        {
            return false;
        }

        var shellModule = uiModule->GetRaptureShellModule();
        if (shellModule == null)
        {
            return false;
        }

        using var utf8 = new Utf8String();
        utf8.SetString(command);
        shellModule->ExecuteCommandInner(&utf8, uiModule);
        return true;
    }

    private bool TryConsumeBConfirmBudget(string source)
    {
        npcBConfirmPressCount++;
        if (npcBConfirmPressCount <= BConfirmPressLimit)
        {
            if (configuration.SemiAutoVerboseLogging)
            {
                log.Information("Semi-auto leve B confirm budget: {Count}/{Limit} source={Source}", npcBConfirmPressCount, BConfirmPressLimit, source);
            }
            return true;
        }

        log.Warning("Semi-auto leve B confirm budget exceeded: {Count}/{Limit} source={Source}", npcBConfirmPressCount, BConfirmPressLimit, source);
        if (waitingForNpcBTurnIn)
        {
            CompleteBFlow($"B 流程確認操作超過 {BConfirmPressLimit} 次，改為收尾切回 A。");
            return false;
        }

        Stop($"B 流程確認操作超過 {BConfirmPressLimit} 次，已停止。");
        return false;
    }

    private unsafe void DumpAddonAtkValues(string addonName, nint addonPtr)
    {
        if (addonPtr == nint.Zero)
        {
            chatGui.Print("[autoLeve] addon pointer 為空。");
            return;
        }

        var unitBase = (AtkUnitBase*)addonPtr;
        if (unitBase == null || !unitBase->IsVisible || unitBase->AtkValues == null)
        {
            chatGui.Print("[autoLeve] addon 不可見或無 AtkValues。");
            return;
        }

        var count = unitBase->AtkValuesCount;
        chatGui.Print($"[autoLeve] {addonName} AtkValuesCount={count}");

        var printed = 0;
        for (var i = 0; i < count; i++)
        {
            var text = unitBase->AtkValues[i].GetValueAsString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
            log.Information("[autoLeve] dump {Addon} idx={Index} text={Text}", addonName, i, oneLine);
            if (printed < 25)
            {
                chatGui.Print($"[autoLeve] {addonName}[{i}] {oneLine}");
                printed++;
            }
        }

        if (printed == 0)
        {
            chatGui.Print("[autoLeve] 沒有可讀文字。");
        }
        else if (printed >= 25)
        {
            chatGui.Print("[autoLeve] 僅顯示前 25 筆，完整內容請看 /xllog。");
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

}
