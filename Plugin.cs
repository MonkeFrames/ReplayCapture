using BepInEx;
using MonkeFrames.ReplayCapture.Replays;
using UnityEngine;

namespace MonkeFrames.ReplayCapture;

[BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
public class Plugin : BepInPlugin
{
    public void Awake()
    {
        GameObject replayObject = new GameObject("MFAutoReplay");
        replayObject.AddComponent<ReplayManager>();
        replayObject.AddComponent<PunCallbacks>();
    }
}