using MonkeFrames.ReplayCapture.Replays;
using Photon.Pun;
using UnityEngine;

namespace MonkeFrames.ReplayCapture;

public class PunCallbacks : MonoBehaviourPunCallbacks
{
    public override void OnJoinedRoom() => ReplayManager.Instance.StartRecording();
    public override void OnLeftRoom() => ReplayManager.Instance.StopRecording();
}