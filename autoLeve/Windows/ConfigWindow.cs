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

        ImGui.Separator();
        ImGui.Text("流程捕捉");
        if (ImGui.Button("記錄下一次操作(單步)"))
        {
            plugin.SemiAutoAssistant.ArmSingleStepCapture();
        }
        ImGui.SameLine();
        if (ImGui.Button("記錄下一次UI事件(單步)"))
        {
            plugin.SemiAutoAssistant.ArmSingleReceiveEventCapture();
        }
        ImGui.SameLine();
        if (ImGui.Button("啟用通用 callback 捕捉"))
        {
            plugin.SemiAutoAssistant.ArmAnyCallbackCaptureOnce();
        }
        ImGui.SameLine();
        if (ImGui.Button("套用最近捕捉為B選物"))
        {
            plugin.SemiAutoAssistant.ApplyLastCapturedCallbackToBSelect();
        }
        var replayAddon = configuration.SemiAutoReplayTargetAddon;
        if (ImGui.InputText("重播目標addon(留空=記錄addon)", ref replayAddon, 64))
        {
            configuration.SemiAutoReplayTargetAddon = replayAddon;
            configuration.Save();
        }
        if (ImGui.Button("重播剛剛記錄"))
        {
            plugin.SemiAutoAssistant.ReplayLastCapturedCallback(configuration.SemiAutoReplayTargetAddon);
        }
        ImGui.SameLine();
        if (ImGui.Button("重播剛剛UI事件"))
        {
            plugin.SemiAutoAssistant.ReplayLastReceiveEvent(configuration.SemiAutoReplayTargetAddon);
        }
        ImGui.TextDisabled("先用通用捕捉記錄手動選物，再套用到 B 自動選物。");
        ImGui.TextWrapped($"通用捕捉最近一次: {plugin.SemiAutoAssistant.LastGenericCaptureSummary}");
        ImGui.TextWrapped($"UI事件捕捉最近一次: {plugin.SemiAutoAssistant.LastReceiveCaptureSummary}");

        ImGui.Separator();
        ImGui.Text("通用 API 測試");
        var dbgAddon = configuration.SemiAutoDebugGenericAddon;
        if (ImGui.InputText("Addon 名稱", ref dbgAddon, 64))
        {
            configuration.SemiAutoDebugGenericAddon = dbgAddon;
            configuration.Save();
        }

        var dbgCount = configuration.SemiAutoDebugGenericCount;
        if (ImGui.SliderInt("Count", ref dbgCount, 0, 5))
        {
            configuration.SemiAutoDebugGenericCount = dbgCount;
            configuration.Save();
        }

        DrawGenericArgEditor("arg0", configuration.SemiAutoDebugGenericType0, configuration.SemiAutoDebugGenericValue0, (t, v) =>
        {
            configuration.SemiAutoDebugGenericType0 = t;
            configuration.SemiAutoDebugGenericValue0 = v;
        });
        DrawGenericArgEditor("arg1", configuration.SemiAutoDebugGenericType1, configuration.SemiAutoDebugGenericValue1, (t, v) =>
        {
            configuration.SemiAutoDebugGenericType1 = t;
            configuration.SemiAutoDebugGenericValue1 = v;
        });
        DrawGenericArgEditor("arg2", configuration.SemiAutoDebugGenericType2, configuration.SemiAutoDebugGenericValue2, (t, v) =>
        {
            configuration.SemiAutoDebugGenericType2 = t;
            configuration.SemiAutoDebugGenericValue2 = v;
        });
        DrawGenericArgEditor("arg3", configuration.SemiAutoDebugGenericType3, configuration.SemiAutoDebugGenericValue3, (t, v) =>
        {
            configuration.SemiAutoDebugGenericType3 = t;
            configuration.SemiAutoDebugGenericValue3 = v;
        });
        DrawGenericArgEditor("arg4", configuration.SemiAutoDebugGenericType4, configuration.SemiAutoDebugGenericValue4, (t, v) =>
        {
            configuration.SemiAutoDebugGenericType4 = t;
            configuration.SemiAutoDebugGenericValue4 = v;
        });

        if (ImGui.Button("送出通用 callback"))
        {
            plugin.SemiAutoAssistant.DebugFireGenericCallback(
                configuration.SemiAutoDebugGenericAddon,
                configuration.SemiAutoDebugGenericCount,
                configuration.SemiAutoDebugGenericType0, configuration.SemiAutoDebugGenericValue0,
                configuration.SemiAutoDebugGenericType1, configuration.SemiAutoDebugGenericValue1,
                configuration.SemiAutoDebugGenericType2, configuration.SemiAutoDebugGenericValue2,
                configuration.SemiAutoDebugGenericType3, configuration.SemiAutoDebugGenericValue3,
                configuration.SemiAutoDebugGenericType4, configuration.SemiAutoDebugGenericValue4);
        }
        ImGui.TextDisabled("Type: 3=Int, 4=UInt。先用通用捕捉拿到型別/值，再重播。");

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

    }

    private void DrawGenericArgEditor(string label, int type, int value, Action<int, int> onChanged)
    {
        var localType = type;
        var localValue = value;
        var changed = false;

        if (ImGui.InputInt($"{label} type", ref localType))
        {
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.InputInt($"{label} value", ref localValue))
        {
            changed = true;
        }

        if (changed)
        {
            onChanged(localType, localValue);
            configuration.Save();
        }
    }
}
