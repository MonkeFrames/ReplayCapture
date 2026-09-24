using Photon.Voice.Unity;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace MonkeFrames.ReplayCapture.Replays;

/// <summary>
/// Records every gorilla's movement and voice, then plays it back with script-free copies
/// ("puppets") that the Other Cameras and keyframes can film.
/// </summary>
[DefaultExecutionOrder(-40)]
public class ReplayManager : MonoBehaviour
{
    public static ReplayManager Instance;

    public static string Folder => Utils.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MonkeFrames", "replays");
    public const string Extension = ".mfreplay";

    // ---------------- Settings ----------------
    public int RecordRate = 60;
    public bool RecordVoices = true;
    public bool RecordMyMic = true;
    public float MaxMinutes = 10f;

    // ---------------- State ----------------
    public ReplayClip Clip { get; private set; }
    public bool Recording { get; private set; }
    public bool Playing;
    public double Time;

    public string BusyText { get; private set; } = "";
    public readonly List<ReplayClip> Library = new();

    public float RecordedSeconds => Clip == null ? 0f : Clip.FrameCount / (float)Mathf.Max(1, Clip.Rate);

    // ---------------- Recording internals ----------------
    private sealed class Binding
    {
        public VRRig Rig;
        public string Key;
        public ReplayTrack Track;
        public Transform[] Parts;
        public VoiceTap Tap;
        public bool Closed;
    }

    private readonly List<Binding> _bindings = new();
    private double _recStartReal, _recStartDsp;
    private int _recFrame;
    private float _nextScan;
    private float[] _audioTmp = new float[4096];
    private readonly HashSet<VRRig> _failedRigs = new();

    private AudioClip _mic;
    private string _micDevice;
    private bool _ownMic;
    private int _micSearches;
    private int _micPos = -1;
    private float _nextMicSearch;

    // ---------------- Background work ----------------
    private volatile bool _busy;

    public ReplayManager()
    {
        Instance = this;
    }

    private void Start()
    {
        try { Directory.CreateDirectory(Folder); } catch { }
        RefreshLibrary();

        // Fallback for setups where Application.onBeforeRender isn't delivered.
        if (GetComponent<ReplayManagerLate>() == null)
            gameObject.AddComponent<ReplayManagerLate>();
    }

    internal void LateFallback()
    {
        if (Recording) Capture();
        ApplyPose();
    }

    private void OnEnable() => Application.onBeforeRender += OnBeforeRender;
    private void OnDisable() => Application.onBeforeRender -= OnBeforeRender;

    private void OnBeforeRender()
    {
        if (Recording)
            Capture();

        ApplyPose();
    }

    // =====================================================================
    //  Recording
    // =====================================================================

    public void StartRecording()
    {
        if (Recording || _busy) return;

        // Keep unsaved edits (trim, names, hidden gorillas) of the replay that's open.
        if (Clip != null && Clip.Dirty && Clip.FilePath != null)
            Save();

        Clip = new ReplayClip
        {
            Name = "MFAutoReplay " + NetworkSystem.Instance.CurrentRoom + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"),
            Created = DateTime.Now,
            Rate = Mathf.Clamp(RecordRate, 10, 120),
        };

        _bindings.Clear();
        _failedRigs.Clear();
        _recFrame = 0;
        _recStartReal = UnityEngine.Time.realtimeSinceStartupAsDouble;
        _recStartDsp = AudioSettings.dspTime;
        _nextScan = 0f;
        ResetMicClock();
        _nextMicSearch = 0f;
        _micSearches = 0;
        _mic = null;
        _ownMic = false;
        Recording = true;

        ScanForRecording();
    }

    public void StopRecording()
    {
        if (!Recording) return;

        DrainAudio();
        foreach (Binding b in _bindings)
            Close(b);
        Recording = false;
        StopOwnMic();

        // Drop gorillas that were only there for an instant.
        for (int i = Clip.Tracks.Count - 1; i >= 0; i--)
        {
            ReplayTrack t = Clip.Tracks[i];
            t.TrimExcess();
            if (t.FrameCount < 2)
            {
                DestroyTrackObjects(t);
                Clip.Tracks.RemoveAt(i);
            }
        }
        _bindings.Clear();

        if (Clip.Tracks.Count == 0 || Clip.FrameCount < 2)
        {
            Unload();
            return;
        }

        Clip.InPoint = 0f;
        Clip.OutPoint = 0f;
        Time = 0;
        Playing = false;

        Save();
    }

    /// <summary>Close the current replay and remove its gorillas.</summary>
    public void Unload()
    {
        if (Recording) return;

        if (Clip != null)
        {
            foreach (ReplayTrack t in Clip.Tracks)
                DestroyTrackObjects(t);
            foreach (CamTrack c in Clip.Cameras)
                if (c.Model != null) { Destroy(c.Model); c.Model = null; }
        }
        Clip = null;
        Playing = false;
        Time = 0;
    }

    private void Capture()
    {
        double now = UnityEngine.Time.realtimeSinceStartupAsDouble - _recStartReal;
        int target = (int)(now * Clip.Rate);

        // Fill every frame up to now (duplicates if the game is running slower than the replay rate),
        // so every gorilla's frames stay lined up with the replay clock.

        while (_recFrame <= target)
        {
            foreach (Binding b in _bindings)
            {
                if (b.Closed) continue;
                if (!Valid(b)) { Close(b); continue; }
                b.Track.AppendFrame(b.Rig.transform, b.Parts);
            }

            _recFrame++;
        }
        Clip.FrameCount = _recFrame;
    }

    private void UpdateRecording()
    {
        if (UnityEngine.Time.unscaledTime >= _nextScan)
            ScanForRecording();

        DrainAudio();
    }

    private void ScanForRecording()
    {
        _nextScan = UnityEngine.Time.unscaledTime + 0.5f;

        foreach (VRRig rig in LiveRigs())
        {
            if (_failedRigs.Contains(rig)) continue;

            string key = KeyOf(rig);
            Binding open = _bindings.FirstOrDefault(b => !b.Closed && b.Key == key);
            if (open != null)
            {
                if (open.Rig == rig)
                {
                    if (open.Track.Name == "Gorilla")
                        open.Track.Name = PlayerName(rig);
                    if (open.Tap == null)
                        AttachTap(open);   // their voice chat may connect a little after they appear
                    continue;
                }
                Close(open);
            }

            // The same rig now belongs to someone else: close the old player's track.
            foreach (Binding b in _bindings)
                if (!b.Closed && b.Rig == rig && b.Key != key)
                    Close(b);

            StartTrack(rig, key);
        }
    }

    private void StartTrack(VRRig rig, string key)
    {
        try
        {
            ReplayTrack track = new ReplayTrack
            {
                Key = key,
                Name = PlayerName(rig),
                IsLocal = rig.isOfflineVRRig,
                Color = rig.playerColor,
                StartFrame = _recFrame,
                AudioRate = AudioSettings.outputSampleRate,
            };

            PuppetBuilder.BuildFromLive(track, rig, out Transform[] parts);

            Binding b = new Binding { Rig = rig, Key = key, Track = track, Parts = parts };

            AttachTap(b);

            Clip.Tracks.Add(track);
            _bindings.Add(b);
        }
        catch (Exception ex)
        {
            _failedRigs.Add(rig);
            Console.WriteLine($"[MonkeFrames::Replay] Couldn't record {PlayerName(rig)}: {ex}");
        }
    }

    public static string PlayerName(VRRig rig)
    {
        if (rig == null) return "nobody";
        if (rig.isOfflineVRRig) return "You";

        string name = null;
        try { name = rig.Creator?.SanitizedNickName; } catch { }
        if (string.IsNullOrEmpty(name)) name = rig.playerNameVisible;
        return string.IsNullOrEmpty(name) ? "Gorilla" : name;
    }

    private void AttachTap(Binding b)
    {
        VRRig rig = b.Rig;
        if (!RecordVoices || rig == null || rig.isOfflineVRRig || rig.voiceAudio == null)
            return;

        VoiceTap tap = rig.voiceAudio.GetComponent<VoiceTap>();
        if (tap == null) tap = rig.voiceAudio.gameObject.AddComponent<VoiceTap>();
        tap.Clear();
        tap.Capturing = true;
        b.Tap = tap;
    }

    private bool Valid(Binding b) =>
        b.Rig != null && b.Rig.isActiveAndEnabled && KeyOf(b.Rig) == b.Key;

    private void Close(Binding b)
    {
        if (b.Closed) return;
        if (b.Tap != null)
        {
            DrainTap(b);
            b.Tap.Capturing = false;
        }
        b.Closed = true;
    }

    private void DrainAudio()
    {
        foreach (Binding b in _bindings)
            if (!b.Closed && b.Tap != null)
                DrainTap(b);

        if (RecordMyMic)
            ReadMic();
    }

    private void DrainTap(Binding b)
    {
        int n = b.Tap.Drain(ref _audioTmp, out double startDsp);
        if (n > 0)
            b.Track.AddAudio(_audioTmp, n, startDsp - _recStartDsp, AudioSettings.outputSampleRate);
    }

    // ---- Your own microphone (read from the game's voice-chat mic) ----

    private void ReadMic()
    {
        Binding local = _bindings.FirstOrDefault(b => !b.Closed && b.Track.IsLocal);
        if (local == null)
            return;

        // While we're using our own mic, keep looking for the game's (e.g. after joining a room).
        if ((_mic == null || _ownMic) && UnityEngine.Time.unscaledTime >= _nextMicSearch)
        {
            _nextMicSearch = UnityEngine.Time.unscaledTime + 3f;
            AudioClip ownClip = _ownMic ? _mic : null;
            string ownDevice = _micDevice;

            if (FindMic())
            {
                if (ownClip != null)
                    ReleaseOwnMic(ownClip, ownDevice, endDevice: ownDevice != _micDevice);
                ResetMicClock();
            }
            else if (ownClip != null)
            {
                _mic = ownClip;         // keep using ours
                _micDevice = ownDevice;
            }
            else if (++_micSearches >= 2)
            {
                StartOwnMic();
                ResetMicClock();
            }
        }
        if (_mic == null)
            return;

        bool recording;
        try { recording = Microphone.IsRecording(_micDevice); }
        catch { recording = false; }
        if (!recording)
        {
            _mic = null;
            _ownMic = false;
            _nextMicSearch = 0f;
            return;
        }

        int pos = Microphone.GetPosition(_micDevice);
        int total = _mic.samples;
        if (total <= 0) { _mic = null; return; }
        if (_micPos < 0) { _micPos = pos; return; }

        int n = (pos - _micPos + total) % total;
        if (n <= 0) return;

        int ch = Mathf.Max(1, _mic.channels);
        float[] buf = new float[n * ch];
        if (!_mic.GetData(buf, _micPos)) return;
        _micPos = pos;

        if (ch > 1)
            for (int i = 0; i < n; i++)
            {
                float s = 0f;
                for (int c = 0; c < ch; c++) s += buf[i * ch + c];
                buf[i] = s / ch;
            }

        // Timestamp by counting samples (no gaps / overlaps between reads); resync if the
        // mic clock drifts away from real time.
        int freq = Mathf.Max(1, _mic.frequency);
        double now = UnityEngine.Time.realtimeSinceStartupAsDouble - _recStartReal;
        double byClock = now - n / (double)freq;
        double t = _micAnchor + _micSamples / (double)freq;
        if (_micSamples < 0 || Math.Abs(t - byClock) > 0.25)
        {
            _micAnchor = byClock;
            _micSamples = 0;
            t = byClock;
        }
        _micSamples += n;

        local.Track.AddAudio(buf, n, t, freq);
    }

    private double _micAnchor;
    private long _micSamples = -1;

    private void ResetMicClock()
    {
        _micPos = -1;
        _micSamples = -1;
    }

    private void StartOwnMic()
    {
        try
        {
            if (Microphone.devices == null || Microphone.devices.Length == 0)
                return;

            // Never take over a device someone else (the game) is already recording from.
            string device = Microphone.devices[0];
            if (Microphone.IsRecording(device) || Microphone.IsRecording(null))
                return;

            _micDevice = device;
            _mic = Microphone.Start(device, true, 2, 48000);
            _ownMic = _mic != null;
        }
        catch (Exception ex)
        {
            Debug.Log($"[MonkeFrames::Replay] Couldn't open the microphone: {ex.Message}");
            _mic = null;
            _ownMic = false;
        }
    }

    private void StopOwnMic()
    {
        if (_ownMic && _mic != null)
            ReleaseOwnMic(_mic, _micDevice, endDevice: true);
        _ownMic = false;
        _mic = null;
    }

    private void ReleaseOwnMic(AudioClip clip, string device, bool endDevice)
    {
        try { if (endDevice) Microphone.End(device); } catch { }
        if (clip != null && clip != _mic) Destroy(clip);
    }

    private bool FindMic()
    {
        _mic = null;
        _ownMic = false;
        try
        {
            foreach (Recorder rec in FindObjectsByType<Recorder>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                object src = rec.GetType().GetProperty("InputSource", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(rec);
                if (src is not MicWrapper mw || mw.Mic == null)
                    continue;

                _mic = mw.Mic;
                _micDevice = typeof(MicWrapper).GetField("device", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(mw) as string;
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.Log($"[MonkeFrames::Replay] Mic lookup failed: {ex.Message}");
        }
        return false;
    }

    private void RestoreLive()
    {
        foreach (Renderer r in _hiddenLive)
            if (r != null)
                r.forceRenderingOff = false;
        _hiddenLive.Clear();
    }

    private void RestoreListener()
    {
        if (_savedListenerVolume >= 0f)
        {
            AudioListener.volume = _savedListenerVolume;
            _savedListenerVolume = -1f;
        }
    }

    // =====================================================================
    //  Library: save / load / delete
    // =====================================================================

    private bool _saveQueued;

    public void Save()
    {
        if (Clip == null || Recording) return;
        if (_busy) { _saveQueued = true; return; }

        ReplayClip clip = Clip;
        string old = clip.FilePath;
        string path = UniquePath(clip.Name, old);

        clip.Dirty = false;          // edits made while saving mark it dirty again
        clip.FilePath = path;
        _saveQueued = false;
        _busy = true;
        BusyText = "Saving replay...";
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Folder);
                clip.Save(path);
                if (!string.IsNullOrEmpty(old) && !SamePath(old, path) && File.Exists(old))
                    File.Delete(old);
            }
            catch (Exception ex)
            {
                Debug.Log($"[MonkeFrames::Replay] Save failed: {ex}");
                clip.Dirty = true;
                clip.FilePath = old;
            }
            finally
            {
                _busy = false;
            }
        });
    }

    /// <summary>File path for a name; never overwrites a different replay that already has that name.</summary>
    private static string UniquePath(string name, string current)
    {
        string baseName = SafeFileName(name);
        string path = Path.Combine(Folder, baseName + Extension);
        for (int i = 2; File.Exists(path) && !SamePath(path, current); i++)
            path = Path.Combine(Folder, $"{baseName} ({i}){Extension}");
        return path;
    }

    private static bool SamePath(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public void RefreshLibrary()
    {
        Library.Clear();
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (string f in Directory.GetFiles(Folder, "*" + Extension))
            {
                try { Library.Add(ReplayClip.Load(f, headerOnly: true)); }
                catch (Exception ex) { Console.WriteLine($"[MonkeFrames::Replay] Skipping {f}: {ex.Message}"); }
            }
            Library.Sort((a, b) => b.Created.CompareTo(a.Created));
        }
        catch (Exception ex)
        {
            Debug.Log($"[MonkeFrames::Replay] Library scan failed: {ex.Message}");
        }
    }

    private static void DestroyTrackObjects(ReplayTrack t)
    {
        if (t.Puppet != null)
        {
            t.Puppet.ReleaseOwned();
            Destroy(t.Puppet.gameObject);
        }
        if (t.Voice != null) Destroy(t.Voice.gameObject);
        t.Puppet = null;
        t.Voice = null;
    }

    // =====================================================================
    //  Frame loop
    // =====================================================================

    private void Update()
    {
        if (_saveQueued && !_busy)
            Save();

        if (Recording)
            UpdateRecording();
    }

    private void OnDestroy()
    {
        RestoreLive();
        RestoreListener();
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static IEnumerable<VRRig> LiveRigs()
    {
        VRRig local = CameraModes.LocalRig();
        if (local != null && local.isActiveAndEnabled)
            yield return local;

        foreach (VRRig rig in FindObjectsByType<VRRig>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (rig == null || !rig.isActiveAndEnabled || rig.isOfflineVRRig)
                continue;

            NetPlayer owner = null;
            try { owner = rig.Creator; } catch { }
            if (owner == null || owner.IsLocal)
                continue;

            yield return rig;
        }
    }

    private static string KeyOf(VRRig rig)
    {
        if (rig.isOfflineVRRig) return "local";
        try
        {
            NetPlayer p = rig.Creator;
            if (p != null)
            {
                if (!string.IsNullOrEmpty(p.UserId)) return p.UserId;
                return "actor" + p.ActorNumber;
            }
        }
        catch { }
        return "rig" + rig.GetInstanceID();
    }

    public static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "Replay";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return name.Trim();
    }
}

/// <summary>Records / poses late in the frame when Application.onBeforeRender isn't available.</summary>
[DefaultExecutionOrder(8900)]
public class ReplayManagerLate : MonoBehaviour
{
    private void LateUpdate() => ReplayManager.Instance?.LateFallback();
}