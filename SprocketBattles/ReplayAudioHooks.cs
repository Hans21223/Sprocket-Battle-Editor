using HarmonyLib;
using UnityEngine;

namespace SprocketBattles;

// Reference/float arguments only. Observes the native helper reached by both PlayOneShot overloads;
// does not replace audio, run DSP callbacks, or alter the original call.
[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShotHelper))]
internal static class ReplayAudioHooks
{
    internal static bool Attached;
    [HarmonyPrefix]
    static void Before(AudioSource __0, AudioClip __1, float __2)
    {
        try { BattleEditor.RecordSound(__0, __1, __2); }
        catch (Exception ex) { Trace.Write("replay sound event: " + ex.Message); }
    }
}

[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayHelper))]
internal static class ReplayAudioStartHooks
{
    [HarmonyPostfix]
    static void After(AudioSource __0)
    {
        try { if (__0?.clip is { } clip) BattleEditor.RecordSound(__0, clip, 1, false); }
        catch (Exception ex) { Trace.Write("replay sound start: " + ex.Message); }
    }
}
