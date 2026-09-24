using BepInEx;
using MonkeFrames.ReplayCapture.Replays;
using UnityEngine;

namespace MonkeFrames.ReplayCapture;

[BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
public class Plugin : BaseUnityPlugin
{
    public void Awake()
    {
        GameObject replayObject = new GameObject("MFAutoReplay");
        replayObject.AddComponent<ReplayManager>();
        replayObject.AddComponent<PunCallbacks>();
    }
}