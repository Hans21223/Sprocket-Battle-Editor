using System.Security.Cryptography;
using System.Text;

namespace SprocketMaps;

/// Filesystem-only discovery. It runs on a worker, never on Unity's rendering thread.
internal static class MapImportDiscovery
{
    internal const int MaxSources = 512;
    internal const int MaxFailures = 32;
    internal sealed record Candidate(string Source, string Stamp);
    internal sealed record Failure(string Source, string Message);
    internal sealed record Snapshot(IReadOnlyList<Candidate> Candidates, IReadOnlyList<Failure> Failures);

    static bool Scene(string path) => Path.GetExtension(path).ToLowerInvariant() is ".unity" or ".prefab";
    internal static Snapshot Scan(IEnumerable<string> roots, string additional, CancellationToken token)
    {
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<Failure>();
        int suppressed = 0;
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(root)) continue;
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Linked map folders are not supported.");
                var entries = Directory.EnumerateFileSystemEntries(root).Take(MapImportSource.MaxFiles + 1).OrderBy(p => p,StringComparer.OrdinalIgnoreCase).ToArray();
                if (entries.Length > MapImportSource.MaxFiles) throw new InvalidOperationException("The map folder has too many entries.");
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (MapImportSource.Files(entry,token).Any(p => MapImportSource.Supported(p) || Scene(p))) Add(entry);
                        }
                        else if (MapImportSource.Supported(entry)) Add(entry);
                        else if (Scene(entry)) Add(root);
                    }
                    catch (Exception ex) when (FileError(ex)) { Failed(entry,ex.Message); }
                }
            }
            catch (Exception ex) when (FileError(ex)) { Failed(root,ex.Message); }
        }
        if (!string.IsNullOrWhiteSpace(additional))
            try
            {
                if (Directory.Exists(additional) || File.Exists(additional)) Add(additional);
                else Failed(additional,"The additional map source was not found.");
            }
            catch (Exception ex) when (FileError(ex)) { Failed(additional,ex.Message); }
        var candidates = new List<Candidate>();
        var shared = new ScanCache();
        // Process loose models with the same sidecar folder together, so a large library cannot
        // evict their shared listing between models. The returned snapshot remains source-sorted.
        foreach (var source in sources.OrderBy(Group,StringComparer.OrdinalIgnoreCase).ThenBy(p => p,StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try { candidates.Add(new(source,Stamp(source,token,shared))); }
            catch (Exception ex) when (FileError(ex)) { Failed(source,ex.Message); }
        }
        if (suppressed != 0) failures.Add(new("Map library",$"{suppressed} additional map sources were skipped. Check the source count and folder permissions."));
        return new(candidates.OrderBy(c => c.Source,StringComparer.OrdinalIgnoreCase).ToArray(),failures);

        static string Group(string source) => Directory.Exists(source) ? source
            : MapImportSource.Model(source) ? Path.GetDirectoryName(source)! : source;

        void Failed(string path, string message)
        {
            if (failures.Count < MaxFailures) failures.Add(new(path,message));
            else suppressed++;
        }

        void Add(string path)
        {
            var absolute = Path.GetFullPath(path);
            if (sources.Count >= MaxSources && !sources.Contains(absolute)) throw new InvalidOperationException("The map library has too many sources.");
            sources.Add(absolute);
        }
    }
    static bool FileError(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException;
    internal static string Stamp(string source, CancellationToken token = default)
        => Stamp(source,token,new ScanCache());
    static string Stamp(string source, CancellationToken token, ScanCache shared)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in shared.SourceFiles(source,token))
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(shared.Frame(path));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    sealed class ScanCache
    {
        const int Limit = MapImportSource.MaxFiles * 2;
        const int FrameBytesLimit = 16 * 1024 * 1024;
        // Ordinal cache keys preserve the exact path casing used in the existing stamp frames.
        readonly Dictionary<string,string[]> folders = new(StringComparer.Ordinal);
        readonly Dictionary<string,byte[]> frames = new(StringComparer.Ordinal);
        int pathCount, frameBytes;
        internal string[] SourceFiles(string source, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(source);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked map sources are not supported.");
            if ((attributes & FileAttributes.Directory) != 0) return Assets(source,token);
            if (MapImportSource.Archive(source)) return new[] {source};
            if (!MapImportSource.Model(source)) throw new InvalidOperationException("Choose an OBJ, FBX, archive or map folder.");
            return MapImportSource.SelectModelAssets(source,Assets(Path.GetDirectoryName(Path.GetFullPath(source))!,token),token);
        }
        string[] Assets(string folder, CancellationToken token)
        {
            if (folders.TryGetValue(folder,out var existing)) return existing;
            var files = MapImportSource.FolderAssets(folder,token);
            if (pathCount + files.Length > Limit) { folders.Clear(); pathCount = 0; }
            folders[folder] = files; pathCount += files.Length;
            return files;
        }
        internal byte[] Frame(string path)
        {
            var full = Path.GetFullPath(path);
            if (frames.TryGetValue(full,out var frame)) return frame;
            var file = new FileInfo(path);
            int length = Encoding.UTF8.GetByteCount(full);
            frame = new byte[length + 20];
            BitConverter.TryWriteBytes(frame.AsSpan(0,4),length);
            Encoding.UTF8.GetBytes(full.AsSpan(),frame.AsSpan(4,length));
            BitConverter.TryWriteBytes(frame.AsSpan(length + 4,8),file.Length);
            BitConverter.TryWriteBytes(frame.AsSpan(length + 12,8),file.LastWriteTimeUtc.Ticks);
            if (frames.Count >= Limit || frameBytes + frame.Length > FrameBytesLimit)
            { frames.Clear(); frameBytes = 0; }
            frames[full] = frame;
            frameBytes += frame.Length;
            return frame;
        }
    }
}

/// A changed source replaces its queued version; deleted sources and obsolete results cannot reappear.
internal sealed class MapImportQueue
{
    readonly Dictionary<string,string> latest = new(StringComparer.OrdinalIgnoreCase), attempted = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,MapImportDiscovery.Candidate> pending = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<string> order = new();
    internal int Count => pending.Count;
    internal void Observe(MapImportDiscovery.Snapshot snapshot)
    {
        latest.Clear();
        foreach (var candidate in snapshot.Candidates) latest[candidate.Source] = candidate.Stamp;
        foreach (var key in pending.Keys.Where(k => !latest.ContainsKey(k)).ToArray()) pending.Remove(key);
        foreach (var key in attempted.Keys.Where(k => !latest.ContainsKey(k)).ToArray()) attempted.Remove(key);
        foreach (var candidate in snapshot.Candidates)
        {
            if (attempted.TryGetValue(candidate.Source,out var stamp) && stamp == candidate.Stamp)
            { pending.Remove(candidate.Source); continue; }
            if (!pending.ContainsKey(candidate.Source)) order.Enqueue(candidate.Source);
            pending[candidate.Source] = candidate;
        }
        // Repeated removal/readdition must not grow stale queue keys for the lifetime of the game.
        if (order.Count > MapImportDiscovery.MaxSources * 2)
        { order.Clear(); foreach (var key in pending.Keys.OrderBy(p => p,StringComparer.OrdinalIgnoreCase)) order.Enqueue(key); }
    }
    internal MapImportDiscovery.Candidate? Next()
    {
        while (order.Count != 0)
            if (pending.Remove(order.Dequeue(),out var candidate))
            { attempted[candidate.Source] = candidate.Stamp; return candidate; }
        return null;
    }
    internal bool Current(MapImportDiscovery.Candidate candidate) => latest.TryGetValue(candidate.Source,out var stamp) && stamp == candidate.Stamp;
    internal void Retry(MapImportDiscovery.Candidate candidate)
    {
        if (attempted.TryGetValue(candidate.Source,out var stamp) && stamp == candidate.Stamp) attempted.Remove(candidate.Source);
    }
    internal void Clear() { latest.Clear(); attempted.Clear(); pending.Clear(); order.Clear(); }
}
