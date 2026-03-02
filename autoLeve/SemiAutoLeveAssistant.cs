using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Numerics;
using System.Runtime.InteropServices;

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
}

public sealed class SemiAutoLeveAssistant : IDisposable
{
    private const int GuildLeveLevelCellMaxLookahead = 8;

    private static readonly string[] WatchedAddonNames =
    [
        "GuildLeve",
        "SelectString",
        "SelectIconString",
        "SelectYesno",
        "Request",
        "RequestItem",
        "Talk",
        "JournalResult",
    ];

    private readonly Configuration configuration;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly IGameGui gameGui;
    private readonly IClientState clientState;
    private readonly Hook<AtkUnitBase.Delegates.FireCallback> fireCallbackHook;

    private string? lastDetectedAddon;
    private string? lastPhaseLogKey;
    private DateTime lastActionAtUtc;
    private NpcFlowMode lastMode = NpcFlowMode.Unknown;
    private NpcAStep npcAStep = NpcAStep.AwaitTalk;
    private int guildLeveSelectStrategy;
    private string? lastGuildLeveDetailTitle;
    private int guildLeveNoProgressCount;
    private DateTime guildLeveOpenedAtUtc;
    private DateTime lastConfiguredGuildLeveCallbackAtUtc;
    private DateTime lastGuildLeveSelectCallbackAtUtc;
    private string? guildLeveTitleBeforeSelectCallback;
    private bool waitingForNpcBTurnIn;
    private bool npcBTurnInHadInteraction;
    private DateTime npcBTurnInStartedAtUtc;
    private DateTime npcBTurnInLastInteractionAtUtc;
    private bool guildLeveCallbackCaptureArmed;
    private int guildLeveCallbackCaptureRemaining;
    private DateTime guildLeveCallbackCaptureUntilUtc;
    private bool npcBCallbackCaptureArmed;
    private int npcBCallbackCaptureRemaining;
    private DateTime npcBCallbackCaptureUntilUtc;

    public SemiAutoLeveState State { get; private set; } = SemiAutoLeveState.Idle;
    public bool IsArmed { get; private set; }

    public unsafe SemiAutoLeveAssistant(
        Configuration configuration,
        IChatGui chatGui,
        IPluginLog log,
        IGameGui gameGui,
        IClientState clientState,
        IGameInteropProvider gameInteropProvider)
    {
        this.configuration = configuration;
        this.chatGui = chatGui;
        this.log = log;
        this.gameGui = gameGui;
        this.clientState = clientState;
        fireCallbackHook = gameInteropProvider.HookFromAddress<AtkUnitBase.Delegates.FireCallback>(
            (nint)AtkUnitBase.MemberFunctionPointers.FireCallback,
            OnFireCallbackDetour);
        fireCallbackHook.Enable();
    }

    public void Dispose()
    {
        fireCallbackHook.Dispose();
    }

    public string StatusSummary =>
        $"Enabled={configuration.SemiAutoLeveEnabled}, TestA={configuration.SemiAutoTestFlowAEnabled}, TestB={configuration.SemiAutoTestFlowBEnabled}, Mode={lastMode}, AStep={npcAStep}, BWaiting={waitingForNpcBTurnIn}, Talk={configuration.SemiAutoM3AutoAdvanceTalk}, Menu2={configuration.SemiAutoM3AutoSelectStringFirstOption}, Target={configuration.SemiAutoTargetLeveName}, ForceCb=[{configuration.SemiAutoGuildLeveSelectArg0},{configuration.SemiAutoGuildLeveSelectArg1},{configuration.SemiAutoGuildLeveSelectLeveId}], Armed={IsArmed}, State={State}" +
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

        IsArmed = true;
        lastDetectedAddon = null;
        lastMode = NpcFlowMode.Unknown;
        npcAStep = NpcAStep.AwaitTalk;
        guildLeveSelectStrategy = 0;
        lastGuildLeveDetailTitle = null;
        guildLeveNoProgressCount = 0;
        lastGuildLeveSelectCallbackAtUtc = DateTime.MinValue;
        guildLeveTitleBeforeSelectCallback = null;
        waitingForNpcBTurnIn = false;
        npcBTurnInHadInteraction = false;
        npcBTurnInStartedAtUtc = DateTime.MinValue;
        npcBTurnInLastInteractionAtUtc = DateTime.MinValue;

        if (!configuration.SemiAutoTestFlowAEnabled && configuration.SemiAutoTestFlowBEnabled)
        {
            waitingForNpcBTurnIn = true;
            npcBTurnInStartedAtUtc = DateTime.UtcNow;
            npcBTurnInLastInteractionAtUtc = DateTime.UtcNow;
        }

        TransitionTo(SemiAutoLeveState.WaitingForNpcDialog);
        if (configuration.SemiAutoTestFlowAEnabled && configuration.SemiAutoTestFlowBEnabled)
        {
            chatGui.Print("[autoLeve] 半自動理符助手已啟動，模式=A→B。");
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
        lastGuildLeveSelectCallbackAtUtc = DateTime.MinValue;
        guildLeveTitleBeforeSelectCallback = null;
        waitingForNpcBTurnIn = false;
        npcBTurnInHadInteraction = false;
        npcBTurnInStartedAtUtc = DateTime.MinValue;
        npcBTurnInLastInteractionAtUtc = DateTime.MinValue;
        TransitionTo(SemiAutoLeveState.Idle);
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
        chatGui.Print("[autoLeve] 已啟用 GuildLeve callback 捕捉，請手動點一次目標理符。");
    }

    public void ArmNpcBCallbackCaptureOnce()
    {
        npcBCallbackCaptureArmed = true;
        npcBCallbackCaptureRemaining = 50;
        npcBCallbackCaptureUntilUtc = DateTime.UtcNow.AddSeconds(20);
        chatGui.Print("[autoLeve] 已啟用 B 流程 callback 捕捉，請在交貨流程手動按一次確認鍵(NUM0)。");
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

    public void Update()
    {
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
            chatGui.Print("[autoLeve] 進入 NPC A 流程（接理符）");
        }

        if (detected is null)
        {
            if (waitingForNpcBTurnIn &&
                npcBTurnInHadInteraction &&
                (DateTime.UtcNow - npcBTurnInLastInteractionAtUtc).TotalMilliseconds > 1200)
            {
                Stop("B 流程完成（交貨視窗已關閉）");
                return;
            }

            if (State != SemiAutoLeveState.WaitingForNpcDialog)
            {
                TransitionTo(SemiAutoLeveState.WaitingForNpcDialog);
            }
            return;
        }

        var (detectedAddon, addonPtr) = detected.Value;
        lastDetectedAddon = detectedAddon;
        if (State != SemiAutoLeveState.DialogDetected)
        {
            TransitionTo(SemiAutoLeveState.DialogDetected);
        }

        if (TryMarkPhaseLogged($"detected:{detectedAddon}"))
        {
            chatGui.Print($"[autoLeve] 偵測到對話視窗：{detectedAddon}");
            if (configuration.SemiAutoVerboseLogging)
            {
                log.Information("Semi-auto leve dialog detected: {AddonName}", detectedAddon);
            }
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

        if (!TryStartActionWindow(140))
        {
            return;
        }

        if (!TryFireCallbackInt(addonPtr, 0))
        {
            return;
        }

        npcBTurnInHadInteraction = true;
        npcBTurnInLastInteractionAtUtc = DateTime.UtcNow;
        chatGui.Print($"[autoLeve] B 流程交貨：{detectedAddon} callback 0");
        if (configuration.SemiAutoVerboseLogging)
        {
            log.Information("Semi-auto leve B action: {Addon} callback 0", detectedAddon);
        }

        if (detectedAddon == "JournalResult")
        {
            Stop("B 流程完成（JournalResult）");
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
                    guildLeveOpenedAtUtc = DateTime.MinValue;
                    break;
                }

                if (guildLeveOpenedAtUtc == DateTime.MinValue)
                {
                    guildLeveOpenedAtUtc = DateTime.UtcNow;
                }

                var currentDetailTitle = TryGetGuildLeveDetailTitle(addonPtr);
                if (IsTargetTitle(currentDetailTitle, configuration.SemiAutoTargetLeveName))
                {
                    npcAStep = NpcAStep.AwaitGuildLeveAccept;
                    guildLeveSelectStrategy = 0;
                    guildLeveNoProgressCount = 0;
                    break;
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
                    guildLeveOpenedAtUtc = DateTime.MinValue;
                    break;
                }

                var acceptDetailTitle = TryGetGuildLeveDetailTitle(addonPtr);
                if (!IsTargetTitle(acceptDetailTitle, configuration.SemiAutoTargetLeveName))
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
                if (TryFireCallbackInt(addonPtr, acceptCallback))
                {
                    npcAStep = NpcAStep.AwaitTalk;
                    guildLeveSelectStrategy = 0;
                    lastGuildLeveDetailTitle = null;
                    guildLeveNoProgressCount = 0;
                    guildLeveOpenedAtUtc = DateTime.MinValue;
                    lastGuildLeveSelectCallbackAtUtc = DateTime.MinValue;
                    guildLeveTitleBeforeSelectCallback = null;
                    chatGui.Print($"[autoLeve] 已點擊接受 (cb={acceptCallback})，A 流程完成。");
                    if (configuration.SemiAutoTestFlowBEnabled)
                    {
                        waitingForNpcBTurnIn = true;
                        npcBTurnInHadInteraction = false;
                        npcBTurnInStartedAtUtc = DateTime.UtcNow;
                        npcBTurnInLastInteractionAtUtc = DateTime.UtcNow;
                        chatGui.Print("[autoLeve] 進入 B 流程：請與交貨 NPC 對話，將自動嘗試 callback 0。");
                        if (configuration.SemiAutoVerboseLogging)
                        {
                            log.Information("Semi-auto leve action: GuildLeve accept callback {Callback}", acceptCallback);
                            log.Information("Semi-auto leve B flow: armed and waiting for turn-in dialogs");
                        }
                    }
                    else
                    {
                        Stop("A 流程完成（B 測試停用）");
                    }
                }
                break;
        }
    }

    private static bool IsNpcBTurnInAddon(string addonName)
    {
        return addonName is "Talk" or "SelectString" or "SelectYesno" or "Request" or "RequestItem" or "JournalResult";
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

        string? fallbackTitle = null;
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

                fallbackTitle ??= titleOnly;
            }
        }

        return fallbackTitle;
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
        if (!isGuildCaptureActive && !isNpcBCaptureActive)
        {
            return;
        }

        var callerAddon = ResolveAddonNameByUnitBase((nint)unitBase);
        if (callerAddon is null)
        {
            return;
        }

        var count = (int)valueCount;

        if (isGuildCaptureActive && callerAddon == "GuildLeve")
        {
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
}
