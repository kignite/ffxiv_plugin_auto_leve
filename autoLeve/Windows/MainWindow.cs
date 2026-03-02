using System;
using System.Numerics;
using ImGuiNET;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace autoLeve.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly string goatImagePath;
    private readonly Plugin plugin;

    // We give this window a hidden ID using ##.
    // The user will see "My Amazing Window" as window title,
    // but for ImGui the ID is "My Amazing Window##With a hidden ID"
    public MainWindow(Plugin plugin, string goatImagePath)
        : base("My Amazing Window##With a hidden ID", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.goatImagePath = goatImagePath;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var player = Plugin.ClientState.LocalPlayer;
        var config = plugin.Configuration;

        ImGui.Text("=== 玩家資訊 ===");

        if (player == null)
        {
            ImGui.Text("尚未登入角色");
        }
        else
        {
            Vector3 p = player.Position;

            ImGui.Text($"Territory: {Plugin.ClientState.TerritoryType}");
            ImGui.Text($"玩家座標:");
            ImGui.Text($"X: {p.X:F2}");
            ImGui.Text($"Y: {p.Y:F2}");
            ImGui.Text($"Z: {p.Z:F2}");
        }

        ImGui.Separator();

        ImGui.Text("=== 目前目標 ===");

        var target = Plugin.TargetManager.Target;

        if (target == null)
        {
            ImGui.Text("沒有目標");
        }
        else
        {
            var pos = target.Position;

            ImGui.Text($"名稱: {target.Name}");
            ImGui.Text($"X: {pos.X:F2}");
            ImGui.Text($"Y: {pos.Y:F2}");
            ImGui.Text($"Z: {pos.Z:F2}");
        }

        if (ImGui.Button("抓取目前目標 NPC"))
        {
            if (target == null)
            {
                Plugin.ChatGui.Print("沒有目標");
            }
            else
            {
                var pos = target.Position;

                Plugin.ChatGui.Print(
                    $"NPC: {target.Name} | " +
                    $"Territory={Plugin.ClientState.TerritoryType} | " +
                    $"X={pos.X:F2} Y={pos.Y:F2} Z={pos.Z:F2}"
                );

                Plugin.Log.Information(
                    "NPC {Name} @ {Pos} territory={Territory}",
                    target.Name.TextValue,
                    pos,
                    Plugin.ClientState.TerritoryType
                );
            }
        }

        ImGui.Separator();
        ImGui.Text("=== NPC 位置設定 ===");

        if (ImGui.Button("設為 NPC A (接理符)"))
        {
            SaveNpcPoint(target);
        }

        if (config.NpcAConfigured)
        {
            ImGui.Text($"A: {config.NpcAName} @ T{config.NpcATerritory} ({config.NpcAX:F1},{config.NpcAY:F1},{config.NpcAZ:F1})");
        }
        else
        {
            ImGui.Text("A: 尚未設定");
        }
    }

    private void SaveNpcPoint(Dalamud.Game.ClientState.Objects.Types.IGameObject? target)
    {
        if (target == null)
        {
            Plugin.ChatGui.Print("沒有目標");
            return;
        }

        var config = plugin.Configuration;
        var pos = target.Position;
        var territory = Plugin.ClientState.TerritoryType;

        config.NpcAConfigured = true;
        config.NpcAName = target.Name.TextValue;
        config.NpcATerritory = territory;
        config.NpcAX = pos.X;
        config.NpcAY = pos.Y;
        config.NpcAZ = pos.Z;
        Plugin.ChatGui.Print($"[autoLeve] 已設定 NPC A: {config.NpcAName}");

        config.Save();
    }
}
