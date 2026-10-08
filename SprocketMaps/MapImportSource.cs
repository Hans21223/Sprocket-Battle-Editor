using System.Security.Cryptography;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace SprocketMaps;

/// Copies scenery into an isolated editor project; never imports executable package contents.
internal static class MapImportSource
{
    internal const string Recipe = "0.4.0-auto-import-2";
    internal const string UnityVersion = "6000.3.21f1";
    internal const long MaxBytes = 2L * 1024 * 1024 * 1024;
    internal const int MaxFiles = 20000, MaxDepth = 6;
    static readonly HashSet<string> Assets = new(StringComparer.OrdinalIgnoreCase)
    { ".obj", ".fbx", ".mtl", ".unity", ".prefab", ".mat", ".asset", ".meta", ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tif", ".tiff", ".psd", ".exr", ".hdr", ".dds" };
    internal static bool Archive(string path) => (Path.GetExtension(path).ToLowerInvariant() is ".zip" or ".7z" or ".unitypackage") && !Directory.Exists(path);
    internal static bool Model(string path) => (Path.GetExtension(path).ToLowerInvariant() is ".obj" or ".fbx") && !Directory.Exists(path);
    internal static bool Supported(string path) => Archive(path) || Model(path);
    internal static bool AllowedAsset(string path) => Assets.Contains(Path.GetExtension(path));

    internal static string SafeRelative(string value)
    {
        value = value.Replace('\\', '/');
        var parts = value.Split('/');
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.StartsWith('/') || parts.Any(p => p is ".." or "."
            || p.Length is 0 or > 255 || p.Any(c => c < 32) || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(new[] { ':', '<', '>', '"', '|', '?', '*' }) >= 0))
            throw new InvalidOperationException("An archive path is not a safe relative asset name.");
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))
                throw new InvalidOperationException("An archive uses a reserved file name.");
        }
        return string.Join('/', parts);
    }
    internal static string Destination(string root, string relative)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root, SafeRelative(relative)));
        if (!result.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Asset path escaped its import directory.");
        return result;
    }
    internal static IEnumerable<string> Files(string directory, CancellationToken token = default)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked source directories are not supported.");
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((directory, 0));
        int visited = 0;
        while (pending.Count != 0)
        {
            var folder = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder.Path))
            {
                token.ThrowIfCancellationRequested();
                if (++visited > MaxFiles * 2) throw new InvalidOperationException("The map source has too many files or folders.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) == 0) yield return entry;
                else
                {
                    if (folder.Depth >= 64) throw new InvalidOperationException("The map source folder nesting is too deep.");
                    pending.Push((entry, folder.Depth + 1));
                }
            }
        }
    }
    internal static string[] SourceFiles(string source, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked map sources are not supported.");
        if (Archive(source)) return new[] { source };
        var folder = Directory.Exists(source) ? source : Path.GetDirectoryName(Path.GetFullPath(source))!;
        bool selectedModel = !Directory.Exists(source) && Model(source);
        if (!Directory.Exists(source) && !selectedModel) throw new InvalidOperationException("Choose an OBJ, FBX, archive or map folder.");
        var assets = FolderAssets(folder,token);
        return selectedModel ? SelectModelAssets(source,assets,token) : assets;
    }
    internal static string[] FolderAssets(string folder, CancellationToken token = default)
        => Files(folder,token).Where(p => AllowedAsset(p) || Archive(p)).OrderBy(p => p,StringComparer.OrdinalIgnoreCase).ToArray();
    internal static string[] SelectModelAssets(string source, IEnumerable<string> assets, CancellationToken token = default)
    {
        var selected = Path.GetFullPath(source);
        return assets.Where(p =>
        {
            token.ThrowIfCancellationRequested();
            return string.Equals(Path.GetFullPath(p),selected,StringComparison.OrdinalIgnoreCase)
                || AllowedAsset(p) && !Model(p) && Path.GetExtension(p).ToLowerInvariant() is not (".unity" or ".prefab");
        }).ToArray();
    }
    internal static string SceneName(string source)
    {
        var name = Directory.Exists(source) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(source)) : Path.GetFileNameWithoutExtension(source);
        name = new string(name.Take(48).Select(c => c < 128 && char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant())))[..8].ToLowerInvariant();
        return "import_" + (string.IsNullOrEmpty(name) ? "map" : name) + "_" + id;
    }
    internal static string Fingerprint(string source, CancellationToken token = default)
    {
        var directory = Directory.Exists(source) ? source : Path.GetDirectoryName(Path.GetFullPath(source))!;
        var files = SourceFiles(source, token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(Recipe + UnityVersion));
        long total = 0; int count = 0;
        var bytes = new byte[65536];
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (++count > MaxFiles) throw new InvalidOperationException("Map source exceeds the import file limit.");
            var name = Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, file).Replace('\\', '/'));
            hash.AppendData(BitConverter.GetBytes(name.Length)); hash.AppendData(name);
            using var stream = File.OpenRead(file);
            using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int n;
            while ((n = stream.Read(bytes, 0, bytes.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if ((total += n) > MaxBytes) throw new InvalidOperationException("Map source exceeds the import size limit.");
                content.AppendData(bytes.AsSpan(0,n));
            }
            hash.AppendData(content.GetHashAndReset());
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    internal sealed class Budget
    {
        internal long Bytes;
        internal int Count;
        internal int Entries;
        internal void Visit() { if (++Entries > MaxFiles * 2) throw new InvalidOperationException("The map archive has too many entries."); }
        internal void Copy(Stream input, string output, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (++Count > MaxFiles) throw new InvalidOperationException("The map archive has too many files.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var writer = File.Create(output);
            var buffer = new byte[65536]; int n;
            while ((n = input.Read(buffer,0,buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if ((Bytes += n) > MaxBytes) throw new InvalidOperationException("The expanded map exceeds the import size limit.");
                writer.Write(buffer,0,n);
            }
        }
    }
    internal static void Stage(string source, string input, CancellationToken token = default)
    {
        var files = SourceFiles(source, token);
        if (Directory.Exists(input) && ((File.GetAttributes(input) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(input).Any()))
            throw new InvalidOperationException("The isolated map staging directory must be empty and unlinked.");
        if (!Archive(source))
        {
            var sourceFolder = Directory.Exists(source) ? source : Path.GetDirectoryName(Path.GetFullPath(source))!;
            var boundary = Path.GetFullPath(sourceFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if ((Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Map staging must be outside the supplied source folder.");
        }
        Directory.CreateDirectory(input);
        var budget = new Budget();
        var archiveQueue = new Queue<(string File, int Depth)>();
        var queuedArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = Directory.Exists(source) ? source : Path.GetDirectoryName(Path.GetFullPath(source))!;
        foreach (var file in files)
        {
            var target = Destination(input, Path.GetRelativePath(directory, file));
            using var stream = File.OpenRead(file); budget.Copy(stream, target, token);
            if (Archive(target) && queuedArchives.Add(target)) archiveQueue.Enqueue((target,0));
        }
        int archiveId = 0;
        while (archiveQueue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (archive, depth) = archiveQueue.Dequeue();
            if (depth >= MaxDepth) throw new InvalidOperationException("The archive nesting is too deep.");
            var folder = Destination(input, "Unpacked" + (++archiveId));
            Directory.CreateDirectory(folder);
            if (Path.GetExtension(archive).Equals(".unitypackage", StringComparison.OrdinalIgnoreCase))
            {
                UnpackUnityPackage(archive, input, folder, budget, token);
                continue;
            }
            using var opened = ArchiveFactory.OpenArchive(archive);
            foreach (var entry in opened.Entries)
            {
                token.ThrowIfCancellationRequested(); budget.Visit();
                if (entry.IsDirectory) continue;
                var relative = SafeRelative(entry.Key ?? throw new InvalidOperationException("Archive entry has no name."));
                // A link is written only as plain bytes, never as a filesystem link; unsupported contents are skipped.
                if (!AllowedAsset(relative) && !Archive(relative)) continue;
                var target = Destination(folder,relative);
                using var stream = entry.OpenEntryStream(); budget.Copy(stream,target,token);
                if (Archive(target) && queuedArchives.Add(target)) archiveQueue.Enqueue((target,depth+1));
            }
        }
        // Archives are not Unity assets. Their extracted scenery is the only input to the editor.
        foreach (var path in Files(input).Where(Archive).ToArray()) File.Delete(path);
        if (!Files(input).Any(p => Model(p) || Path.GetExtension(p).Equals(".unity", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".prefab", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("No OBJ, FBX, Unity scene or prefab was found in this map.");
    }
    static void UnpackUnityPackage(string archive, string input, string temporary, Budget budget, CancellationToken token)
    {
        using (var stream = File.OpenRead(archive))
        using (var reader = ReaderFactory.OpenReader(stream))
            while (reader.MoveToNextEntry())
            {
                token.ThrowIfCancellationRequested(); budget.Visit();
                if (reader.Entry.IsDirectory) continue;
                var relative = SafeRelative(reader.Entry.Key ?? "");
                var parts = relative.Split('/');
                if (parts.Length != 2 || parts[0].Length != 32 || !parts[0].All(Uri.IsHexDigit) || parts[1] is not ("asset" or "asset.meta" or "pathname")) continue;
                using var content = reader.OpenEntryStream(); budget.Copy(content,Destination(temporary,relative),token);
            }
        foreach (var pathname in Files(temporary).Where(p => Path.GetFileName(p) == "pathname"))
        {
            if (new FileInfo(pathname).Length > 2048) throw new InvalidOperationException("Package asset path is too long.");
            var name = SafeRelative(File.ReadAllText(pathname).TrimEnd('\n','\r','\0'));
            if (!name.StartsWith("Assets/",StringComparison.Ordinal) || !AllowedAsset(name)) continue;
            var raw = Path.Combine(Path.GetDirectoryName(pathname)!,"asset");
            if (!File.Exists(raw)) continue;
            var target = Destination(input,name[7..]);
            using (var stream = File.OpenRead(raw)) budget.Copy(stream,target,token);
            var meta = raw + ".meta";
            if (File.Exists(meta)) using (var stream = File.OpenRead(meta)) budget.Copy(stream,target + ".meta",token);
        }
        // Raw package GUID files are not passed to Unity, only the sanitized Assets tree with preserved GUIDs.
        Directory.Delete(temporary,true);
    }
}
