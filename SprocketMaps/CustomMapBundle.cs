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
