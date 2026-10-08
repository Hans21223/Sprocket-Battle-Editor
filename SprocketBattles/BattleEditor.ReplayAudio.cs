using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SprocketBattles;

public sealed partial class BattleEditor
{
    sealed class CapturedSound
    {
        internal AudioSource Source = null!;
        internal AudioClip Clip = null!;
        internal ReplaySound Data = new();
        internal bool Shot, Ended;
        internal float Started, Next, Multiplier = 1;
    }
    readonly System.Collections.Concurrent.ConcurrentQueue<(AudioSource Source, AudioClip Clip, float Volume, float Time, bool Shot)> audioEvents = new();
    readonly List<(AudioSource Source, bool Mute)> mutedNativeAudio = new();
    readonly List<(AudioListener Listener, bool Enabled)> replayListeners = new();
    readonly List<AudioClip?> playbackClips = new();
    readonly List<AudioClip> ownedPlaybackClips = new();
    readonly List<(ReplaySound Data, AudioSource Source)> playbackSounds = new();
    GameObject? replayAudioRoot;
    AudioListener? replayListener;

    internal static void RecordSound(AudioSource source, AudioClip clip, float multiplier, bool shot = true)
    {
        if (instance?.recordingReplay is not { } c || source == null || clip == null || !float.IsFinite(multiplier)) return;
        if (instance.audioEvents.Count < 1024)
            instance.audioEvents.Enqueue((source, clip, multiplier, Math.Clamp(Time.time - c.Start, 0, ReplayData.MaxSeconds), shot));
        else c.Data.AudioIncomplete = true;
    }

    static bool BattleSoundSource(AudioSource source) => source != null && source.gameObject.activeInHierarchy
        && !source.gameObject.name.StartsWith("Replay sound", StringComparison.Ordinal)
        && !(source.clip?.name.Contains("music", StringComparison.OrdinalIgnoreCase) ?? false);

    int CaptureSoundClip(Capture c, AudioClip clip)
    {
        if (c.AudioClips.TryGetValue(clip.Pointer, out int known)) return known;
        int index = -1;
        try
        {
            var data = new ReplaySoundClip { Name = clip.name, Samples = clip.samples, Channels = clip.channels, Frequency = clip.frequency };
            long count = (long)data.Samples * data.Channels;
            if (data.Samples < 1 || data.Channels < 1 || data.Channels > 8 || data.Frequency < 8000 || data.Frequency > 192000
                || data.Samples / (double)data.Frequency > 180 || c.Data.SoundClips.Count >= 256) return -1;
            // Some Unity clips are compressed/streamed. Preserve their identity for the game's loaded-clip resolver.
            if (count * 2 <= 32 * 1024 * 1024 && c.AudioDecodedBytes + count * 2 <= 128 * 1024 * 1024 && c.AudioBytes < ReplayMedia.MaxAudioBytes)
                try
                {
                    var samples = new Il2CppStructArray<float>(count);
                    if (clip.GetData(samples, 0))
                    {
                        var pcm = new byte[count * 2];
                        for (int i = 0; i < count; i++)
                        {
                            float v = samples[i]; short value = (short)Math.Round(Math.Clamp(float.IsFinite(v) ? v : 0, -1, 1) * 32767);
                            pcm[i * 2] = (byte)value; pcm[i * 2 + 1] = (byte)(value >> 8);
                        }
                        var packed = ReplayMedia.Pack(pcm);
                        if (c.AudioBytes + packed.Length <= ReplayMedia.MaxAudioBytes)
                        { data.Pcm = packed; c.AudioBytes += packed.Length; c.AudioDecodedBytes += count * 2; }
                    }
                }
                catch (Exception ex) { Trace.Write($"replay audio clip '{data.Name}': using game asset ({ex.Message})"); }
            index = c.Data.SoundClips.Count; c.Data.SoundClips.Add(data);
            Trace.Write($"replay audio clip '{data.Name}': {data.Samples} samples, {data.Channels} channels, {(data.Pcm.Length == 0 ? "game asset" : "embedded PCM")}");
        }
        catch (Exception ex) { c.Data.AudioIncomplete = true; Trace.Write("replay audio clip: " + ex.Message); }
        finally { c.AudioClips[clip.Pointer] = index; }
        return index;
    }
    CapturedSound? NewSound(Capture c, AudioSource source, AudioClip clip, float at, bool shot, float multiplier = 1)
    {
        int index = CaptureSoundClip(c, clip);
        if (c.Data.Sounds.Count >= 1024 || c.AudioKeys >= 100000 || index < 0)
        { c.Data.AudioIncomplete = true; return null; }
        var sound = new CapturedSound { Source = source, Clip = clip, Shot = shot, Started = at, Multiplier = multiplier,
            Data = new ReplaySound { Clip = index, Loop = !shot && source.loop, SpatialBlend = source.spatialBlend,
                MinDistance = source.minDistance, MaxDistance = Math.Max(source.minDistance, source.maxDistance), Rolloff = (int)source.rolloffMode } };
        CaptureSoundKey(c, sound, at, true);
        if (sound.Data.Keys.Count == 0) return null;
        c.Data.Sounds.Add(sound.Data); c.AudioTracks.Add(sound);
        return sound;
    }
    void CaptureSoundKey(Capture c, CapturedSound sound, float at, bool playing)
    {
        var before = sound.Data.Keys.LastOrDefault(); var source = sound.Source;
        if (c.AudioKeys >= 100000) { c.Data.AudioIncomplete = true; return; }
        int sample = before?.Sample ?? 0;
        var clip = c.Data.SoundClips[sound.Data.Clip];
        if (source != null)
        {
            sample = sound.Shot ? (int)Math.Clamp((at - sound.Started) / c.Speed * clip.Frequency * Math.Abs(source.pitch), 0, clip.Samples - 1)
                : Math.Clamp(source.timeSamples, 0, clip.Samples - 1);
        }
        var key = new ReplaySoundKey { Time = at, Playing = playing, Sample = sample,
            Position = source != null ? Files.Array(source.transform.position) : before?.Position ?? new float[3],
            Volume = source != null ? Math.Clamp(source.mute ? 0 : source.volume * sound.Multiplier, 0, 4) : before?.Volume ?? 0,
            Pitch = source != null ? Math.Clamp(source.pitch, -3, 3) : before?.Pitch ?? 1 };
        if (key.Position.Any(v => !float.IsFinite(v)) || !float.IsFinite(key.Volume) || !float.IsFinite(key.Pitch))
        { c.Data.AudioIncomplete = true; return; }
        if (before != null && before.Time == at) sound.Data.Keys[^1] = key;
        else { sound.Data.Keys.Add(key); c.AudioKeys++; }
        sound.Next = at + .1f;
    }
    void CaptureAudio(Capture c, float at)
    {
        c.Data.Duration = Math.Max(c.Data.Duration, at);
        while (audioEvents.TryDequeue(out var e))
            if (BattleSoundSource(e.Source))
            {
                if (!e.Shot && c.RegularSounds.TryGetValue(e.Source.Pointer, out var previous) && !previous.Ended)
                { CaptureSoundKey(c, previous, Math.Min(at, e.Time), false); previous.Ended = true; }
                if (NewSound(c, e.Source, e.Clip, Math.Min(at, e.Time), e.Shot, e.Volume) is { } sound && !e.Shot)
                    c.RegularSounds[e.Source.Pointer] = sound;
            }
        if (at >= c.NextAudioDiscovery)
        { c.AudioSources = FindObjectsOfType<AudioSource>().Where(BattleSoundSource).ToArray(); c.NextAudioDiscovery = at + .25f; }
        foreach (var source in c.AudioSources)
        {
            if (source == null || !source.isPlaying || source.clip is not { } clip) continue;
            if (!c.RegularSounds.TryGetValue(source.Pointer, out var active) || active.Ended || active.Clip.Pointer != clip.Pointer)
            {
                if (active != null && !active.Ended) { CaptureSoundKey(c, active, at, false); active.Ended = true; }
                if (NewSound(c, source, clip, at, false) is { } started) c.RegularSounds[source.Pointer] = started;
            }
            else if (!source.loop && active.Data.Keys.Count > 0 && source.timeSamples + clip.frequency / 10 < active.Data.Keys[^1].Sample)
            {
                CaptureSoundKey(c, active, at, false); active.Ended = true;
                if (NewSound(c, source, clip, at, false) is { } started) c.RegularSounds[source.Pointer] = started;
            }
        }
        foreach (var sound in c.AudioTracks)
        {
            if (sound.Ended) continue;
            if (sound.Clip == null) { sound.Ended = true; CaptureSoundKey(c, sound, at, false); continue; }
            bool alive = sound.Source != null;
            bool playing = sound.Shot ? (at - sound.Started) / c.Speed < sound.Clip.length / Math.Max(.01f, Math.Abs(sound.Data.Keys[^1].Pitch))
                : alive && sound.Source!.isPlaying && sound.Source.clip?.Pointer == sound.Clip.Pointer;
            if (!playing || at >= sound.Next) CaptureSoundKey(c, sound, at, playing);
            if (!playing) sound.Ended = true;
        }
    }
    void MuteNativeAudio()
    {
        RestoreNativeAudio();
        foreach (var source in FindObjectsOfType<AudioSource>())
            if (source != null) { mutedNativeAudio.Add((source, source.mute)); source.mute = true; }
    }
    void RestoreNativeAudio()
    {
        foreach (var (source, mute) in mutedNativeAudio) if (source != null) source.mute = mute;
        mutedNativeAudio.Clear();
    }
    void StartReplayAudio(float time)
    {
        StopReplayAudio();
        if (C.Replay is not { } replay || replay.Sounds.Count == 0 || view == null) return;
        var gameClips = Resources.FindObjectsOfTypeAll<AudioClip>(); int missing = 0;
        foreach (var data in replay.SoundClips)
        {
            AudioClip? clip = null;
            try
            {
                if (data.Pcm.Length > 0)
                {
                    var pcm = ReplayMedia.Unpack(data.Pcm, checked(data.Samples * data.Channels * 2));
                    var samples = new Il2CppStructArray<float>(pcm.Length / 2);
                    for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
                    clip = AudioClip.Create("Replay sound " + data.Name, data.Samples, data.Channels, data.Frequency, false);
                    ownedPlaybackClips.Add(clip);
                    if (!clip.SetData(samples, 0)) throw new InvalidDataException("Could not upload recorded PCM.");
                }
                else clip = gameClips.FirstOrDefault(c => c != null && c.name == data.Name && c.samples == data.Samples && c.channels == data.Channels && c.frequency == data.Frequency);
            }
            catch (Exception ex) { clip = null; Trace.Write($"replay sound '{data.Name}': {ex.Message}"); }
            if (clip == null) missing++;
            playbackClips.Add(clip);
        }
        replayAudioRoot = new GameObject("Replay sound root");
        foreach (var data in replay.Sounds)
        {
            if (playbackClips[data.Clip] is not { } clip) continue;
            var source = new GameObject("Replay sound").AddComponent<AudioSource>(); source.transform.SetParent(replayAudioRoot.transform, false);
            source.playOnAwake = false; source.clip = clip; source.loop = data.Loop; source.spatialBlend = data.SpatialBlend;
            source.minDistance = data.MinDistance; source.maxDistance = data.MaxDistance; source.rolloffMode = (AudioRolloffMode)data.Rolloff;
            source.dopplerLevel = 0; source.ignoreListenerPause = true; playbackSounds.Add((data, source));
        }
        foreach (var listener in FindObjectsOfType<AudioListener>())
        { replayListeners.Add((listener, listener.enabled)); listener.enabled = false; }
        replayListener = view.gameObject.AddComponent<AudioListener>();
        ReplayAudioAt(time);
        Trace.Write($"replay audio playback: {playbackSounds.Count} sound events, {missing} unresolved clips");
        if (missing > 0) Say($"{missing} sounds couldn't load. Record again with this build to embed available sounds.");
    }
    void ReplayAudioAt(float time)
    {
        if (C.Replay is not { } replay) return;
        foreach (var (sound, source) in playbackSounds)
        {
            int index = ReplayMedia.SoundKeyAt(sound.Keys, time);
            if (index < 0 || !sound.Keys[index].Playing) { if (source.isPlaying) source.Stop(); continue; }
            var key = sound.Keys[index]; var next = sound.Keys[Math.Min(index + 1, sound.Keys.Count - 1)];
            float t = next.Time > key.Time && next.Playing ? Math.Clamp((time - key.Time) / (next.Time - key.Time), 0, 1) : 0;
            source.transform.position = Vector3.Lerp(Files.Vector(key.Position), Files.Vector(next.Position), t);
            source.volume = key.Volume + (next.Volume - key.Volume) * t;
            float pitch = key.Pitch + (next.Pitch - key.Pitch) * t;
            source.pitch = Math.Clamp(pitch * Math.Max(.05f, C.Speed) / replay.CaptureSpeed, -3, 3);
            var clip = replay.SoundClips[sound.Clip];
            double expected = key.Sample + (time - key.Time) / replay.CaptureSpeed * clip.Frequency * Math.Abs(pitch);
            if (sound.Loop) expected %= clip.Samples;
            else if (expected >= clip.Samples) { if (source.isPlaying) source.Stop(); continue; }
            int sample = (int)Math.Clamp(expected, 0, clip.Samples - 1);
            if (!source.isPlaying) { source.timeSamples = sample; source.Play(); }
            else
            {
                int delta = Math.Abs(source.timeSamples - sample);
                if (sound.Loop) delta = Math.Min(delta, clip.Samples - delta);
                if (delta > clip.Frequency * .15f) source.timeSamples = sample;
            }
        }
    }
    void StopReplayAudio()
    {
        foreach (var (_, source) in playbackSounds) if (source != null) source.Stop();
        playbackSounds.Clear(); playbackClips.Clear();
        if (replayAudioRoot != null) Destroy(replayAudioRoot); replayAudioRoot = null;
        if (replayListener != null) { replayListener.enabled = false; Destroy(replayListener); } replayListener = null;
        foreach (var (listener, enabled) in replayListeners) if (listener != null) listener.enabled = enabled;
        replayListeners.Clear();
        foreach (var clip in ownedPlaybackClips) if (clip != null) Destroy(clip);
        ownedPlaybackClips.Clear();
    }
}
