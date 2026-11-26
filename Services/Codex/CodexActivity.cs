using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

sealed record CodexTaskSnapshot(string Id, string Title, string Status, long StartedAtMs,
    long CompletedAtMs, long DurationMs, long ElapsedMs, long? FirstTokenMs);

sealed record CodexSnapshot(long Now, CodexTaskSnapshot[] Tasks);

sealed class CodexActivity : BackgroundService
{
    private const int InitialTailBytes = 1024 * 1024;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32 };
    private readonly object gate = new();
    private readonly Dictionary<string, TrackedRollout> tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex", "sessions");
    private readonly string indexFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex", "session_index.jsonl");
    private Dictionary<string, string> titles = new(StringComparer.Ordinal);
    private CodexSnapshot snapshot = new(0, []);
    private long titlesReadAt;
    private long lastScanAt;
    private FileSystemWatcher? watcher;

    public CodexSnapshot Read()
    {
        lock (gate) return snapshot;
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        RefreshTitles();
        Scan();
        StartWatcher();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                if (titlesReadAt == 0 || Stopwatch.GetElapsedTime(titlesReadAt) > TimeSpan.FromSeconds(60))
                    RefreshTitles();
                if (lastScanAt == 0 || Stopwatch.GetElapsedTime(lastScanAt) > TimeSpan.FromSeconds(15))
                    Scan();
                foreach (var path in dirty.Keys)
                {
                    if (dirty.TryRemove(path, out _)) Process(path);
                }
                UpdateSnapshot();
            }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                // Codex remains the source of truth; a missing or temporarily locked file is not fatal.
            }
        } while (await timer.WaitForNextTickAsync(token));
    }

    private void StartWatcher()
    {
        if (!Directory.Exists(root)) return;
        try
        {
            watcher = new FileSystemWatcher(root, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            watcher.Changed += (_, item) => dirty[item.FullPath] = 0;
            watcher.Created += (_, item) => dirty[item.FullPath] = 0;
            watcher.Renamed += (_, item) => dirty[item.FullPath] = 0;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            watcher?.Dispose();
            watcher = null;
        }
    }

    private void Scan()
    {
        lastScanAt = Stopwatch.GetTimestamp();
        if (!Directory.Exists(root)) return;
        var cutoff = DateTime.UtcNow.AddHours(-2);
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                TrackedRollout? file;
                lock (gate) tracked.TryGetValue(path, out file);
                if (file == null)
                {
                    if (info.LastWriteTimeUtc < cutoff) continue;
                    file = new TrackedRollout(path, ThreadId(path));
                    lock (gate) tracked[path] = file;
                    ReadInitial(file);
                }
                else file.LastWriteMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            }
            var removed = new List<string>();
            lock (gate)
            {
                foreach (var pair in tracked)
                    if (!File.Exists(pair.Key)) removed.Add(pair.Key);
                foreach (var path in removed) tracked.Remove(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static string ThreadId(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length > 36 ? name[^36..] : name;
    }

    private void ReadInitial(TrackedRollout file)
    {
        Process(file, initial: true);
        if (file.Status == "idle") RecoverTaskState(file);
    }

    private static void RecoverTaskState(TrackedRollout file)
    {
        const int chunkSize = 4 * 1024 * 1024;
        try
        {
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var end = length;
            while (end > 0)
            {
                var start = Math.Max(0, end - chunkSize);
                var count = (int)(end - start);
                var buffer = new byte[count];
                stream.Position = start;
                var read = 0;
                while (read < count)
                {
                    var amount = stream.Read(buffer, read, count - read);
                    if (amount == 0) break;
                    read += amount;
                }
                if (read == 0) return;
                var lines = Encoding.UTF8.GetString(buffer, 0, read).Split('\n');
                var first = start > 0 ? 1 : 0;
                var last = lines.Length - 1;
                if (end < length) last--;
                else if (read > 0 && buffer[read - 1] != (byte)'\n') last--;
                for (var index = last; index >= first; index--)
                {
                    var line = lines[index].TrimEnd('\r');
                    if (line.Length == 0) continue;
                    if (!line.Contains("\"task_started\"", StringComparison.Ordinal) &&
                        !line.Contains("\"task_complete\"", StringComparison.Ordinal)) continue;
                    ApplyLine(file, line);
                    return;
                }
                if (start == 0) return;
                end = start;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private void Process(string path)
    {
        TrackedRollout? file;
        lock (gate) tracked.TryGetValue(path, out file);
        if (file == null || !File.Exists(path)) return;
        Process(file, initial: false);
    }

    private void Process(TrackedRollout file, bool initial)
    {
        try
        {
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            if (length < file.Offset)
            {
                file.Offset = 0;
                file.Status = "idle";
            }
            var start = file.Offset;
            if (initial && start == 0)
                start = Math.Max(0, length - InitialTailBytes);
            if (length <= start) return;

            var count = (int)Math.Min(length - start, 4 * 1024 * 1024);
            var buffer = new byte[count];
            stream.Position = start;
            var read = 0;
            while (read < count)
            {
                var amount = stream.Read(buffer, read, count - read);
                if (amount == 0) break;
                read += amount;
            }
            if (read == 0) return;
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (lastNewline < 0) return;
            var textStart = 0;
            if (start > 0 && initial)
            {
                var firstNewline = Array.IndexOf(buffer, (byte)'\n', 0, read);
                if (firstNewline < 0) return;
                textStart = firstNewline + 1;
            }
            var text = Encoding.UTF8.GetString(buffer, textStart, lastNewline + 1 - textStart);
            foreach (var line in text.Split('\n'))
            {
                if (line.Length != 0) ApplyLine(file, line);
            }
            file.Offset = start + lastNewline + 1;
            file.LastWriteMs = new DateTimeOffset(File.GetLastWriteTimeUtc(file.Path)).ToUnixTimeMilliseconds();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static void ApplyLine(TrackedRollout file, string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line, JsonOptions);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var top) || top.GetString() != "event_msg" ||
                !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("type", out var kind)) return;
            var type = kind.GetString();
            if (type == "task_started")
            {
                file.Status = "running";
                file.StartedAtMs = ReadSeconds(payload, "started_at");
                file.CompletedAtMs = 0;
                file.DurationMs = 0;
                file.FirstTokenMs = null;
            }
            else if (type == "task_complete")
            {
                file.Status = "completed";
                file.CompletedAtMs = ReadSeconds(payload, "completed_at");
                file.DurationMs = ReadLong(payload, "duration_ms");
                file.FirstTokenMs = ReadNullableLong(payload, "time_to_first_token_ms");
                if (file.StartedAtMs == 0) file.StartedAtMs = ReadSeconds(payload, "started_at");
            }
            else if (type is "item_completed" or "token_count")
            {
                file.LastWriteMs = Math.Max(file.LastWriteMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }
        catch (JsonException) { }
    }

    private static long ReadSeconds(JsonElement payload, string name) =>
        ReadNullableLong(payload, name) is long value ? value * 1000 : 0;

    private static long ReadLong(JsonElement payload, string name) =>
        ReadNullableLong(payload, name) ?? 0;

    private static long? ReadNullableLong(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value)) return null;
        if (value.TryGetInt64(out var integer)) return integer;
        return value.TryGetDouble(out var number) && double.IsFinite(number) ? (long)number : null;
    }

    private void RefreshTitles()
    {
        titlesReadAt = Stopwatch.GetTimestamp();
        if (!File.Exists(indexFile)) return;
        try
        {
            using var stream = new FileStream(indexFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var next = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.ReadLine() is { } line)
            {
                using var document = JsonDocument.Parse(line, JsonOptions);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id) || !root.TryGetProperty("thread_name", out var name)) continue;
                var key = id.GetString();
                var value = name.GetString();
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value)) next[key] = value;
            }
            lock (gate) titles = next;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void UpdateSnapshot()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tasks = new List<CodexTaskSnapshot>();
        lock (gate)
        {
            foreach (var file in tracked.Values)
            {
                if (file.Status == "running" && file.LastWriteMs != 0 &&
                    now - file.LastWriteMs > 10 * 60 * 1000)
                    file.Status = "interrupted";
                if (file.Status == "running")
                {
                    tasks.Add(new CodexTaskSnapshot(file.ThreadId, ShortTitle(file.ThreadId), "running",
                        file.StartedAtMs, 0, 0, file.StartedAtMs == 0 ? 0 : Math.Max(0, now - file.StartedAtMs), null));
                }
                else if (file.Status == "completed" && file.CompletedAtMs != 0 &&
                    now - file.CompletedAtMs <= 120_000)
                {
                    tasks.Add(new CodexTaskSnapshot(file.ThreadId, ShortTitle(file.ThreadId), "completed",
                        file.StartedAtMs, file.CompletedAtMs, file.DurationMs,
                        file.DurationMs > 0 ? file.DurationMs : Math.Max(0, file.CompletedAtMs - file.StartedAtMs),
                        file.FirstTokenMs));
                }
            }
            tasks.Sort((left, right) =>
            {
                var order = (left.Status == "running" ? 0 : 1).CompareTo(right.Status == "running" ? 0 : 1);
                return order != 0 ? order : right.CompletedAtMs.CompareTo(left.CompletedAtMs);
            });
            snapshot = new CodexSnapshot(now, tasks.Take(8).ToArray());
        }
    }

    private string ShortTitle(string id)
    {
        if (!titles.TryGetValue(id, out var title) || string.IsNullOrWhiteSpace(title)) return "Codex " + id[..Math.Min(8, id.Length)];
        var line = title.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var split = line.Length > 3 && char.IsLetter(line[0]) && line[1] == ':' && line[2] == '\\'
            ? line.IndexOf(' ', 3) : -1;
        if (split > 3)
        {
            var name = Path.GetFileName(line[..split].TrimEnd('\\')).Trim();
            var rest = line[(split + 1)..].Trim();
            line = string.IsNullOrWhiteSpace(name) ? rest :
                string.IsNullOrWhiteSpace(rest) ? name : name + " · " + rest;
        }
        return line.Length > 32 ? line[..32] + "…" : line;
    }

    public override void Dispose()
    {
        watcher?.Dispose();
        base.Dispose();
    }

    private sealed class TrackedRollout(string path, string threadId)
    {
        public string Path { get; } = path;
        public string ThreadId { get; } = threadId;
        public long Offset = 0;
        public long LastWriteMs = 0;
        public string Status = "idle";
        public long StartedAtMs = 0;
        public long CompletedAtMs = 0;
        public long DurationMs = 0;
        public long? FirstTokenMs;
    }
}
