using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

sealed record AntigravityTaskSnapshot(string Id, string Source, string Title, string Status,
    long StartedAtMs, long CompletedAtMs, long DurationMs, long ElapsedMs,
    int Done, int Total, string? CurrentStep);

sealed record AntigravitySnapshot(long Now, AntigravityTaskSnapshot[] Tasks);

sealed class AntigravityActivity(ILogger<AntigravityActivity> logger) : BackgroundService
{
    private const int RetentionMs = 120_000;
    private const int MaxTasks = 8;
    private const int MaxProgressBytes = 256 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly object gate = new();
    private readonly string summariesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity",
        "conversation_summaries.db");
    private readonly string conversationsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity",
        "conversations");
    private readonly Dictionary<string, ProgressCache> progressCache = new(StringComparer.Ordinal);
    private AntigravitySnapshot snapshot = new(0, []);
    private long lastWarningAt;

    public AntigravitySnapshot Read()
    {
        lock (gate) return snapshot;
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        Poll();
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try { Poll(); }
            catch (Exception error) when (!token.IsCancellationRequested) { Warn(error); }
        } while (await timer.WaitForNextTickAsync(token));
    }

    private void Poll()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tasks = ReadSummaries(now);
        tasks.Sort((left, right) =>
        {
            var order = (left.Status == "running" ? 0 : 1).CompareTo(right.Status == "running" ? 0 : 1);
            if (order != 0) return order;
            return left.Status == "running"
                ? right.StartedAtMs.CompareTo(left.StartedAtMs)
                : right.CompletedAtMs.CompareTo(left.CompletedAtMs);
        });
        lock (gate) snapshot = new AntigravitySnapshot(now, tasks.Take(MaxTasks).ToArray());
    }

    private List<AntigravityTaskSnapshot> ReadSummaries(long now)
    {
        var result = new List<AntigravityTaskSnapshot>();
        if (!File.Exists(summariesPath)) return result;
        List<SummaryRow>? rows = null;
        try { rows = QuerySummaries(FullSummaryQuery); }
        catch (Exception error) when (IsReadError(error)) { }
        if (rows == null)
        {
            try { rows = QuerySummaries(MinimalSummaryQuery); }
            catch (Exception error) when (IsReadError(error)) { Warn(error); return result; }
        }
        if (rows == null) return result;
        var candidates = new List<Candidate>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Id) || row.Id.Length > 80 ||
                !string.IsNullOrWhiteSpace(row.ParentId) || row.NestingDepth > 0) continue;
            var active = IsActive(row.Status) || row.NotFullyIdle != 0;
            var modified = ParseTime(row.LastModified);
            var completedAt = active ? 0 : modified?.ToUnixTimeMilliseconds() ?? 0;
            if (!active && (completedAt == 0 || now - completedAt > RetentionMs)) continue;
            if (row.StepCount <= 0 && string.IsNullOrWhiteSpace(row.Title)) continue;
            var started = ParseTime(row.LastUserInput) ?? modified;
            var startedAt = started?.ToUnixTimeMilliseconds() ?? (active ? now : completedAt);
            var duration = active || completedAt <= startedAt ? 0 : completedAt - startedAt;
            candidates.Add(new Candidate(row, active, startedAt, completedAt, duration,
                active ? Math.Max(0, now - startedAt) : duration));
        }
        candidates.Sort((left, right) =>
        {
            var order = (left.Active ? 0 : 1).CompareTo(right.Active ? 0 : 1);
            if (order != 0) return order;
            return left.Active
                ? right.StartedAt.CompareTo(left.StartedAt)
                : right.CompletedAt.CompareTo(left.CompletedAt);
        });
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates.Take(MaxTasks))
        {
            var row = candidate.Row;
            var progress = ReadProgressCached(row.Id, row.StepCount, row.Status);
            // A new user turn may not have produced a fresh update_steps yet;
            // do not show the previous turn's checklist as current progress.
            if (progress != null && progress.StepIndex < row.LastUserInputStep) progress = null;
            live.Add(row.Id);
            result.Add(new AntigravityTaskSnapshot(
                "antigravity:" + row.Id, "antigravity", ShortTitle(row.Title, row.Id), candidate.Active ? "running" : "completed",
                candidate.StartedAt, candidate.CompletedAt, candidate.Duration, candidate.Elapsed,
                progress?.Done ?? 0, progress?.Total ?? 0, progress?.Current));
        }
        foreach (var id in progressCache.Keys.Where(id => !live.Contains(id)).ToArray())
            progressCache.Remove(id);
        return result;
    }

    private List<SummaryRow>? QuerySummaries(string sql)
    {
        using var connection = OpenReadOnly(summariesPath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<SummaryRow>();
        while (reader.Read())
        {
            rows.Add(new SummaryRow(
                Text(reader, 0), Text(reader, 1), Text(reader, 2), Integer(reader, 3),
                Text(reader, 4), Text(reader, 5), Integer(reader, 6),
                Text(reader, 7), Integer(reader, 8), Integer(reader, 9)));
        }
        return rows;
    }

    private TaskProgress? ReadProgressCached(string id, int stepCount, string status)
    {
        if (progressCache.TryGetValue(id, out var cached) && cached.StepCount == stepCount &&
            string.Equals(cached.Status, status, StringComparison.Ordinal))
            return cached.Progress;
        TaskProgress? progress = null;
        try { progress = ReadProgress(id); }
        catch (Exception error) when (IsReadError(error)) { Warn(error); }
        progressCache[id] = new ProgressCache(stepCount, status, progress);
        return progress;
    }

    private TaskProgress? ReadProgress(string id)
    {
        var path = Path.Combine(conversationsPath, id + ".db");
        if (!File.Exists(path)) return null;
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT idx, step_payload, metadata FROM steps WHERE step_type = 132 ORDER BY idx DESC LIMIT 12";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var stepIndex = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            for (var index = 1; index <= 2; index++)
            {
                if (reader.IsDBNull(index)) continue;
                var value = reader.GetValue(index);
                var bytes = value switch
                {
                    byte[] blob => blob,
                    string text => Encoding.UTF8.GetBytes(text),
                    _ => null
                };
                if (bytes == null || bytes.Length == 0) continue;
                if (bytes.Length > MaxProgressBytes) bytes = bytes[..MaxProgressBytes];
                var progress = ParseProgress(bytes);
                if (progress != null) return progress with { StepIndex = stepIndex };
            }
        }
        return null;
    }

    private static TaskProgress? ParseProgress(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var search = 0;
        while (search < text.Length)
        {
            var marker = text.IndexOf("\"steps\"", search, StringComparison.Ordinal);
            if (marker < 0) break;
            var start = text.LastIndexOf('{', marker);
            if (start >= 0 && marker - start <= 200)
            {
                var end = JsonObjectEnd(text, start);
                if (end > start && end - start <= 64 * 1024)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(text[start..(end + 1)],
                            new JsonDocumentOptions { MaxDepth = 16 });
                        if (document.RootElement.TryGetProperty("steps", out var steps) &&
                            steps.ValueKind == JsonValueKind.Array)
                        {
                            var total = 0;
                            var done = 0;
                            string? inProgress = null;
                            string? pending = null;
                            foreach (var step in steps.EnumerateArray())
                            {
                                if (step.ValueKind != JsonValueKind.Object ||
                                    !step.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String)
                                    continue;
                                var name = title.GetString()?.Trim();
                                if (string.IsNullOrWhiteSpace(name)) continue;
                                total++;
                                var status = step.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String
                                    ? value.GetString() : null;
                                if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)) done++;
                                else if (string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase) && inProgress == null)
                                    inProgress = name;
                                else if (string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase) && pending == null)
                                    pending = name;
                            }
                            if (total > 0) return new TaskProgress(done, total, inProgress ?? pending, 0);
                        }
                    }
                    catch (JsonException) { }
                }
            }
            search = marker + 7;
        }
        return null;
    }

    private static int JsonObjectEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var index = start; index < text.Length; index++)
        {
            var ch = text[index];
            if (inString)
            {
                if (escape) escape = false;
                else if (ch == '\\') escape = true;
                else if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') inString = true;
            else if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return index;
        }
        return -1;
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = 1; PRAGMA busy_timeout = 800;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static bool IsActive(string status) =>
        status.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("BUSY", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("CANCELING", StringComparison.OrdinalIgnoreCase);

    private static bool IsReadError(Exception error) => error is SqliteException or IOException or
        UnauthorizedAccessException or InvalidOperationException or ArgumentException or FormatException or
        InvalidCastException or OverflowException or IndexOutOfRangeException;

    private static string Text(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? "" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "";

    private static int Integer(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? 0 : Convert.ToInt32(reader.GetValue(index), CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTime(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;

    private static string ShortTitle(string title, string id)
    {
        var line = string.IsNullOrWhiteSpace(title) ? "Antigravity " + id[..Math.Min(8, id.Length)] :
            string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 72 ? line[..72] + "…" : line;
    }

    private void Warn(Exception error)
    {
        var now = Stopwatch.GetTimestamp();
        if (lastWarningAt != 0 && Stopwatch.GetElapsedTime(lastWarningAt) < TimeSpan.FromSeconds(60)) return;
        lastWarningAt = now;
        logger.LogWarning(error, "Could not read Antigravity conversations; keeping the last snapshot");
    }

    private const string FullSummaryQuery = """
        SELECT conversation_id, title, status, step_count, last_modified_time, last_user_input_time,
               not_fully_idle, parent_conversation_id, nesting_depth, last_user_input_step_index
        FROM conversation_summaries
        ORDER BY last_modified_time DESC
        LIMIT 50
        """;

    private const string MinimalSummaryQuery = """
        SELECT conversation_id, title, status, step_count, last_modified_time, last_user_input_time,
               0 AS not_fully_idle,
               '' AS parent_conversation_id, 0 AS nesting_depth, -1 AS last_user_input_step_index
        FROM conversation_summaries
        ORDER BY last_modified_time DESC
        LIMIT 50
        """;

    private sealed record SummaryRow(string Id, string Title, string Status, int StepCount,
        string LastModified, string LastUserInput, int NotFullyIdle,
        string ParentId, int NestingDepth, int LastUserInputStep);

    private sealed record Candidate(SummaryRow Row, bool Active, long StartedAt, long CompletedAt,
        long Duration, long Elapsed);

    private sealed record ProgressCache(int StepCount, string Status, TaskProgress? Progress);
    private sealed record TaskProgress(int Done, int Total, string? Current, int StepIndex);
}
