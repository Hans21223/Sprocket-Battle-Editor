using System.Runtime.InteropServices;

namespace SprocketMaps;

/// The Windows x64 IL2CPP loader takes an eight-byte CancellationToken by value, not by pointer.
internal static class NativeSceneLoadAbi
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr Load(IntPtr manager, IntPtr sceneName, IntPtr arguments, int options,
        IntPtr cancellationSource, IntPtr methodInfo);

    internal static unsafe IntPtr BoxTokenWord(IntPtr word, Func<IntPtr, IntPtr> box)
    {
        // Even an empty token needs ADDRESSABLE storage when passed to il2cpp_value_box.
        // Its value (normally zero) is not the address of a native struct.
        var storage = word;
        return box((IntPtr)(&storage));
    }
}
