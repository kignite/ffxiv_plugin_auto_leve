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

        var bConfirmSpam = configuration.SemiAutoBUseConfirmSpam;
        if (ImGui.Checkbox("B: 優先使用確認操作連按", ref bConfirmSpam))
        {
            configuration.SemiAutoBUseConfirmSpam = bConfirmSpam;
            configuration.Save();
        }
        var bKeyboardConfirm = configuration.SemiAutoBUseKeyboardConfirmKey;
        if (ImGui.Checkbox("B: 連按時使用鍵盤NUM0(非callback0)", ref bKeyboardConfirm))
        {
            configuration.SemiAutoBUseKeyboardConfirmKey = bKeyboardConfirm;
            configuration.Save();
        }
        ImGui.TextDisabled("A+B 建議用 attack1/attack2 標記循環：/ta <attack1> -> A -> /ta <attack2> -> B");

        if (ImGui.Button("收合/展開主視窗"))
        {
            plugin.ToggleMainUi();
        }

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

        var detectRadius = configuration.NpcDetectRadius;
        if (ImGui.SliderFloat("NPC 判定半徑", ref detectRadius, 2f, 20f, "%.1f"))
        {
            configuration.NpcDetectRadius = detectRadius;
            configuration.Save();
        }

        ImGui.Text("NPC A 座標請到主視窗抓取目標 NPC 設定。");

    }
}
