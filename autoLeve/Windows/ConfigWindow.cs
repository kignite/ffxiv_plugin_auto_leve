using System;
using System.Numerics;
using ImGuiNET;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility.Raii;

namespace autoLeve.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly Plugin plugin;
    private int manualGuildLeveArg0 = 13;
    private int manualGuildLeveArg1 = 13;
    private int manualGuildLeveId = 1647;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Plugin plugin) : base("A Wonderful Configuration Window###With a constant ID")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(560, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        configuration = plugin.Configuration;
        manualGuildLeveArg0 = configuration.SemiAutoGuildLeveSelectArg0;
        manualGuildLeveArg1 = configuration.SemiAutoGuildLeveSelectArg1;
        manualGuildLeveId = configuration.SemiAutoGuildLeveSelectLeveId;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        // Flags must be added or removed before Draw() is being called, or they won't apply
        if (configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public override void Draw()
    {
        using var scroll = ImRaii.Child("config_scroll", Vector2.Zero, false, ImGuiWindowFlags.None);

        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable Config Window", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.Text("半自動理符助手 (MVP)");

        var semiEnabled = configuration.SemiAutoLeveEnabled;
        if (ImGui.Checkbox("啟用半自動模式", ref semiEnabled))
        {
            configuration.SemiAutoLeveEnabled = semiEnabled;
            configuration.Save();
            if (!semiEnabled)
            {
                plugin.SemiAutoAssistant.Stop("設定關閉");
            }
        }

        var verbose = configuration.SemiAutoVerboseLogging;
        if (ImGui.Checkbox("詳細日誌 (Verbose Log)", ref verbose))
        {
            configuration.SemiAutoVerboseLogging = verbose;
            configuration.Save();
        }

        var autoAdvanceTalk = configuration.SemiAutoM3AutoAdvanceTalk;
        if (ImGui.Checkbox("M3: 自動推進 Talk 對話", ref autoAdvanceTalk))
        {
            configuration.SemiAutoM3AutoAdvanceTalk = autoAdvanceTalk;
            configuration.Save();
        }

        var autoSelectString = configuration.SemiAutoM3AutoSelectStringFirstOption;
        if (ImGui.Checkbox("M3-2: SelectString 選第2項(製作任務)", ref autoSelectString))
        {
            configuration.SemiAutoM3AutoSelectStringFirstOption = autoSelectString;
            configuration.Save();
        }

        var autoSelectTarget = configuration.SemiAutoM3AutoSelectTargetLeveByName;
        if (ImGui.Checkbox("M3-3: 自動選取指定理符", ref autoSelectTarget))
        {
            configuration.SemiAutoM3AutoSelectTargetLeveByName = autoSelectTarget;
            configuration.Save();
        }

        var autoAccept = configuration.SemiAutoM3AutoAcceptLeve;
        if (ImGui.Checkbox("M3-4: 自動點擊接受", ref autoAccept))
        {
            configuration.SemiAutoM3AutoAcceptLeve = autoAccept;
            configuration.Save();
        }

        var testFlowA = configuration.SemiAutoTestFlowAEnabled;
        if (ImGui.Checkbox("測試流程 A（接理符）", ref testFlowA))
        {
            configuration.SemiAutoTestFlowAEnabled = testFlowA;
            configuration.Save();
        }

        var testFlowB = configuration.SemiAutoTestFlowBEnabled;
        if (ImGui.Checkbox("測試流程 B（繳交）", ref testFlowB))
        {
            configuration.SemiAutoTestFlowBEnabled = testFlowB;
            configuration.Save();
        }
        ImGui.TextDisabled("A=開且B=關: 只測A；A=關且B=開: 只測B；A=開且B=開: A→B整段。");

        var actionDelay = configuration.SemiAutoActionDelayMs;
        if (ImGui.SliderInt("全域動作延遲(ms)", ref actionDelay, 200, 2000))
        {
            configuration.SemiAutoActionDelayMs = actionDelay;
            configuration.Save();
        }

        var targetName = configuration.SemiAutoTargetLeveName;
        if (ImGui.InputText("目標理符名稱", ref targetName, 128))
        {
            configuration.SemiAutoTargetLeveName = targetName;
            configuration.Save();
        }

        var guildAccept = configuration.SemiAutoGuildLeveAcceptCallback;
        if (ImGui.SliderInt("GuildLeve 接受索引", ref guildAccept, 0, 2500))
        {
            configuration.SemiAutoGuildLeveAcceptCallback = guildAccept;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.Text("GuildLeve callback 捕捉");
        if (ImGui.Button("啟用 callback 捕捉"))
        {
            plugin.SemiAutoAssistant.ArmGuildLeveCallbackCaptureOnce();
        }
        ImGui.TextDisabled("按下後請手動點選理符，查看 /xllog 的 hook 輸出。");
        if (ImGui.Button("啟用 B callback 捕捉"))
        {
            plugin.SemiAutoAssistant.ArmNpcBCallbackCaptureOnce();
        }
        ImGui.TextDisabled("按下後請在交貨流程手動按一次確認鍵(NUM0)，查看 /xllog 的 hook 輸出。");
        ImGui.SetNextItemWidth(80f);
        ImGui.InputInt("arg0", ref manualGuildLeveArg0);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        ImGui.InputInt("arg1", ref manualGuildLeveArg1);
        ImGui.SetNextItemWidth(120f);
        ImGui.InputInt("leveId", ref manualGuildLeveId);
        if (ImGui.Button("測試並同步到自動M3-3"))
        {
            plugin.SemiAutoAssistant.DebugSelectGuildLeveByCallbackArgs(
                manualGuildLeveArg0,
                manualGuildLeveArg1,
                manualGuildLeveId);
            plugin.SemiAutoAssistant.ApplyGuildLeveSelectCallbackToAuto(
                manualGuildLeveArg0,
                manualGuildLeveArg1,
                manualGuildLeveId);
        }
        var detectRadius = configuration.NpcDetectRadius;
        if (ImGui.SliderFloat("NPC 判定半徑", ref detectRadius, 2f, 20f, "%.1f"))
        {
            configuration.NpcDetectRadius = detectRadius;
            configuration.Save();
        }

        ImGui.Text("NPC A 座標請到主視窗抓取目標 NPC 設定。");

        ImGui.TextWrapped(plugin.SemiAutoAssistant.StatusSummary);

        if (ImGui.Button("開始監看對話"))
        {
            plugin.SemiAutoAssistant.Start();
        }

        ImGui.SameLine();
        if (ImGui.Button("停止"))
        {
            plugin.SemiAutoAssistant.Stop("使用者停止");
        }

        ImGui.SameLine();
        if (ImGui.Button("Dump 目前選單"))
        {
            plugin.SemiAutoAssistant.DumpVisibleMenuEntries();
        }
    }
}
