using Dalamud.Configuration;
using System;

namespace autoLeve;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool SemiAutoLeveEnabled { get; set; } = false;
    public bool SemiAutoVerboseLogging { get; set; } = true;
    public bool SemiAutoM3AutoAdvanceTalk { get; set; } = true;
    public bool SemiAutoM3AutoSelectStringFirstOption { get; set; } = false;
    public bool SemiAutoM3AutoSelectTargetLeveByName { get; set; } = true;
    public bool SemiAutoM3AutoAcceptLeve { get; set; } = true;
    public bool SemiAutoTestFlowAEnabled { get; set; } = true;
    public bool SemiAutoTestFlowBEnabled { get; set; } = false;
    public bool SemiAutoUseConfiguredGuildLeveSelectCallback { get; set; } = false;
    public int SemiAutoActionDelayMs { get; set; } = 900;
    public string SemiAutoTargetLeveName { get; set; } = "治癒身心的茶";
    public int SemiAutoGuildLeveAcceptCallback { get; set; } = 1491;
    public bool SemiAutoM34UseTwoArgCallback { get; set; } = false;
    public int SemiAutoM34TwoArgCmd { get; set; } = 3;
    public int SemiAutoM34TwoArgLeveId { get; set; } = 1647;
    public int SemiAutoGuildLeveSelectArg0 { get; set; } = 13;
    public int SemiAutoGuildLeveSelectArg1 { get; set; } = 13;
    public int SemiAutoGuildLeveSelectLeveId { get; set; } = 1647;
    public string SemiAutoDebugGenericAddon { get; set; } = "Request";
    public int SemiAutoDebugGenericCount { get; set; } = 4;
    public int SemiAutoDebugGenericType0 { get; set; } = 3; // Int
    public int SemiAutoDebugGenericValue0 { get; set; } = 2;
    public int SemiAutoDebugGenericType1 { get; set; } = 4; // UInt
    public int SemiAutoDebugGenericValue1 { get; set; } = 0;
    public int SemiAutoDebugGenericType2 { get; set; } = 4; // UInt
    public int SemiAutoDebugGenericValue2 { get; set; } = 44;
    public int SemiAutoDebugGenericType3 { get; set; } = 4; // UInt
    public int SemiAutoDebugGenericValue3 { get; set; } = 0;
    public int SemiAutoDebugGenericType4 { get; set; } = 3; // Int
    public int SemiAutoDebugGenericValue4 { get; set; } = 0;
    public string SemiAutoReplayTargetAddon { get; set; } = "Request";

    public bool NpcAConfigured { get; set; } = true;
    public string NpcAName { get; set; } = "格里格";
    public uint NpcATerritory { get; set; } = 962;
    public float NpcAX { get; set; } = 46.83f;
    public float NpcAY { get; set; } = -15.65f;
    public float NpcAZ { get; set; } = 107.87f;

    public float NpcDetectRadius { get; set; } = 4f;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
