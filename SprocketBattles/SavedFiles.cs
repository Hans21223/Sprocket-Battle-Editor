using System.Text;

namespace SprocketBattles;

/// Complete a save beside its destination before replacing it. An interrupted write leaves the previous save intact.
internal static class SavedFiles
{
    internal static void Write(string path, string text, bool overwrite = true)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, full, overwrite);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }
}
