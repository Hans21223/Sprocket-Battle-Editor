using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Startup;
using Sprocket.SceneManagement;
using NativeCancellation = Il2CppSystem.Threading.CancellationToken;

namespace SprocketMaps;

/// Avoid Harmony's boxed-value trampoline for Load: it treats the token word as a memory address.
internal static class NativeSceneLoadHook
{
    static IDetour? detour;
    static NativeSceneLoadAbi.Load? original;
    static readonly NativeSceneLoadAbi.Load callback = Loading;

    internal static unsafe void Install()
    {
        if (detour != null) return;
        uint alignment = 0;
        int size = IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore<NativeCancellation>.NativeClassPtr, ref alignment);
        if (IntPtr.Size != 8 || size != 8)
            throw new NotSupportedException("This scene loader requires the supported Windows x64 token layout.");
        var field = typeof(MainSceneManager).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(f => f.Name.StartsWith("NativeMethodInfoPtr_Load_Public_", StringComparison.Ordinal)
                && f.Name.Contains("_Task_String_Il2CppReferenceArray_1_Object_SceneLoadOptions_CancellationToken_", StringComparison.Ordinal));
        var pointer = (IntPtr)field.GetValue(null)!;
        var method = UnityVersionHandler.Wrap((Il2CppMethodInfo*)pointer);
        if (method.MethodPointer == IntPtr.Zero) throw new InvalidOperationException("The native scene loader is missing.");
        var pending = Il2CppInteropRuntime.Instance.DetourProvider.Create(method.MethodPointer, callback);
        try
        {
            original = pending.GenerateTrampoline<NativeSceneLoadAbi.Load>();
            pending.Apply();
            detour = pending;
        }
        catch { pending.Dispose(); original = null; throw; }
        Plugin.ModLog.LogInfo("Custom scene loader attached with the native x64 cancellation-token layout.");
    }

    static IntPtr Loading(IntPtr manager, IntPtr sceneName, IntPtr arguments, int options,
        IntPtr cancellationSource, IntPtr methodInfo)
    {
        var forwardedName = sceneName;
        try
        {
            var name = IL2CPP.Il2CppStringToManaged(sceneName);
            if (name != null)
            {
                // Native scene requests pass through with all original values. Only an actual custom
                // request needs its native cancellation token boxed and retained by the session.
                NativeCancellation? cancellation = null;
                if (CustomMapBridge.LoadName(name) != null && cancellationSource != IntPtr.Zero)
                    cancellation = new NativeCancellation(NativeSceneLoadAbi.BoxTokenWord(cancellationSource,
                        address => IL2CPP.il2cpp_value_box(Il2CppClassPointerStore<NativeCancellation>.NativeClassPtr, address)));
                string requested = name;
                CustomMapBridge.Loading(new MainSceneManager(manager), ref requested, cancellation);
                if (requested != name) forwardedName = IL2CPP.ManagedStringToIl2Cpp(requested);
            }
        }
        catch (Exception ex)
        {
            // Never let a managed exception escape a reverse P/Invoke or prevent a native scene load.
            // Cleanup may itself encounter a failed native task. Contain those errors too: no failure
            // from diagnostics or recovery is allowed to cross this unmanaged callback boundary.
            try { CustomMapBridge.Disable("Custom maps could not start. Choose a built-in map and restart the game."); }
            catch { }
            try { Plugin.ModLog.LogError($"Preparing custom scene request failed: {ex}"); }
            catch { }
        }
        return original!(manager, forwardedName, arguments, options, cancellationSource, methodInfo);
    }
}
