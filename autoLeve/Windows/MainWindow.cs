using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using ImGuiNET;

namespace autoLeve.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    // We give this window a hidden ID using ##.
    // The user will see "My Amazing Window" as window title,
    // but for ImGui the ID is "My Amazing Window##With a hidden ID"
    public MainWindow(Plugin plugin)
        : base("AutoLeve Control Panel##MainWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var config = plugin.Configuration;

        ImGui.Separator();
        ImGui.Text("=== 自動循環控制 ===");
        ImGui.TextDisabled("attack1 = NPC A，attack2 = NPC B");

        if (ImGui.Button("標記目前目標為 attack1(NPC A)"))
        {
            plugin.SemiAutoAssistant.MarkCurrentTargetAttack1();
        }
        ImGui.SameLine();
        if (ImGui.Button("標記目前目標為 attack2(NPC B)"))
        {
            plugin.SemiAutoAssistant.MarkCurrentTargetAttack2();
        }

        if (ImGui.Button("開始自動循環 (A↔B)"))
        {
            if (!config.SemiAutoTestFlowAEnabled || !config.SemiAutoTestFlowBEnabled)
            {
                config.SemiAutoTestFlowAEnabled = true;
                config.SemiAutoTestFlowBEnabled = true;
                config.Save();
            }
            plugin.SemiAutoAssistant.Start();
        }
        ImGui.SameLine();
        if (ImGui.Button("停止"))
        {
            plugin.SemiAutoAssistant.Stop("使用者停止");
        }

        var targetLeveName = config.SemiAutoTargetLeveName;
        if (ImGui.InputText("目標理符名稱", ref targetLeveName, 128))
        {
            config.SemiAutoTargetLeveName = targetLeveName;
            config.Save();
        }

        var targetTurnInCount = config.SemiAutoTargetTurnInCount;
        if (ImGui.InputInt("目標繳交次數 (0=不限)", ref targetTurnInCount))
        {
            if (targetTurnInCount < 0)
            {
                targetTurnInCount = 0;
            }
            config.SemiAutoTargetTurnInCount = targetTurnInCount;
            config.Save();
        }

        ImGui.TextDisabled("目前僅支援舊薩雷安。使用前請先站在轉角並標記兩個 NPC。");
        ImGui.TextDisabled("請先用「確認操作」手動提交一次高山茶（用於定位背包中的高山茶位置）。");
    }

}
