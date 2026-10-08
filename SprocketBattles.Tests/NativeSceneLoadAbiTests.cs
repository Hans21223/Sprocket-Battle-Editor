using System.Runtime.InteropServices;
using SprocketMaps;

static class NativeSceneLoadAbiTests
{
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    internal static unsafe void Run()
    {
        if (IntPtr.Size != 8) throw new Exception("The supported game build is Windows x64.");
        foreach (var word in new[] { IntPtr.Zero, new IntPtr(0x123456789ABC) })
        {
            var copied = NativeSceneLoadAbi.BoxTokenWord(word, address =>
            {
                Check(address != IntPtr.Zero && address != word, "boxing must receive storage, never the token value");
                return Marshal.ReadIntPtr(address);
            });
            Check(copied == word, "empty and nonempty token words retain their exact contents");
        }

        IntPtr expectedToken = IntPtr.Zero;
        int calls = 0;
        NativeSceneLoadAbi.Load callback = (manager, name, args, options, token, info) =>
        {
            Check(manager == new IntPtr(0x1111) && name == new IntPtr(0x2222) && args == new IntPtr(0x3333),
                "loader object and array arguments retain their pointer values");
            Check(options == unchecked((int)0x87654321) && token == expectedToken && info == new IntPtr(0x6666),
                "the register and stack arguments use the exact native sizes and order");
            calls++;
            return new IntPtr(0x7777);
        };
        var address = Marshal.GetFunctionPointerForDelegate(callback);
        var invoke = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, IntPtr, IntPtr, IntPtr>)address;
        foreach (var token in new[] { IntPtr.Zero, new IntPtr(0x123456789ABC) })
        {
            expectedToken = token;
            Check(invoke(new IntPtr(0x1111), new IntPtr(0x2222), new IntPtr(0x3333), unchecked((int)0x87654321), token,
                new IntPtr(0x6666)) == new IntPtr(0x7777), "the native task pointer is returned unchanged");
        }
        GC.KeepAlive(callback);
        Check(calls == 2, "both default and cancelable native requests cross the unmanaged callback exactly once");
        Console.WriteLine("NATIVE_SCENE_LOAD_ABI_TESTS_OK: native callback ABI, empty token storage, nonempty token, argument and return preservation");
    }
}
