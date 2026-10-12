using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SprocketMaps;

/// Unity 6's injected loader returns a GC handle. It must be unmarshaled before wrapping an AssetBundle.
internal static class CustomMapBundle
{
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct StringSpan
    {
        public char* Begin;
        public int Length;
    }
    delegate IntPtr LoadFile(ref StringSpan path, uint crc, ulong offset);
    static LoadFile? load;
    delegate IntPtr LoadAssets(IntPtr bundle, ref StringSpan name, IntPtr type);
    static LoadAssets? loadAssets;

    /// Every asset of this type in the bundle. The interop's LoadAllAssets references a span method this runtime
    /// lacks, so even its empty-name branch fails to compile; this calls the same native function with an empty name.
    internal static unsafe UnityEngine.Object[] All<T>(AssetBundle bundle) where T : UnityEngine.Object
    {
        loadAssets ??= IL2CPP.ResolveICall<LoadAssets>("UnityEngine.AssetBundle::LoadAssetWithSubAssets_Internal_Injected");
        var empty = new StringSpan { Begin = (char*)1, Length = 0 }; // Unity's marshalling of "" (null is 0)
        var array = loadAssets(UnityEngine.Object.MarshalledUnityObject.MarshalNotNull(bundle), ref empty, Il2CppType.Of<T>().Pointer);
        return array == IntPtr.Zero ? Array.Empty<UnityEngine.Object>()
            : new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Object>(array).ToArray();
    }

    internal static unsafe AssetBundle Open(string file)
    {
        load ??= IL2CPP.ResolveICall<LoadFile>("UnityEngine.AssetBundle::LoadFromFile_Internal_Injected");
        fixed (char* text = file)
        {
            var span = new StringSpan { Begin = text, Length = file.Length };
            var handle = load(ref span, 0, 0);
            var bundle = handle == IntPtr.Zero ? null : UnityEngine.Bindings.Unmarshal.UnmarshalUnityObject<AssetBundle>(handle);
            return bundle ?? throw new InvalidOperationException("Unity could not load the map file.");
        }
    }
}
