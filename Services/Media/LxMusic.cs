using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

sealed record LxStatus(string? Status, string? Name, string? Singer, string? AlbumName, string? PicUrl,
    double Progress, double Duration, double PlaybackRate);

sealed record LxLyrics(string? Lyric, string? Tlyric, string? Rlyric, string? Lxlyric);

sealed record LxSnapshot(MusicState State, Lyrics Lyrics, bool Connected);

sealed class LxMusic : BackgroundService
{
    public const string Source = "lx";
    private const string BaseUrl = "http://127.0.0.1:23330";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(2) };
    private readonly HttpClient artworkHttp = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly object gate = new();
    private MusicState state = new("unavailable");
    private Lyrics lyrics = new("waiting", []);
    private bool connected;
    private string? songKey;
    private string? artworkUrl;
    private ImageBytes? artwork;
    private bool lyricsLoading;
    private string? lyricsKey;
    private long lyricsAt;

    public LxSnapshot Read()
    {
        lock (gate) return new(state, lyrics, connected);
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
        do
        {
            try { await PollAsync(token); }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                lock (gate)
                {
                    connected = false;
                    state = new MusicState("unavailable");
                    lyrics = new Lyrics("waiting", []);
                    songKey = null;
                }
            }
        } while (await timer.WaitForNextTickAsync(token));
    }

    private async Task PollAsync(CancellationToken token)
    {
        var status = await http.GetFromJsonAsync<LxStatus>(
            $"{BaseUrl}/status?filter=status,name,singer,albumName,picUrl,progress,duration,playbackRate", Json, token);
        if (status == null || string.IsNullOrWhiteSpace(status.Name))
        {
            lock (gate)
            {
                connected = true;
                state = new MusicState("no-track");
            }
            return;
        }

        var song = Song.Create(status.Name, status.Singer ?? "", status.AlbumName ?? "");
        var playing = string.Equals(status.Status, "playing", StringComparison.OrdinalIgnoreCase);
        var paused = string.Equals(status.Status, "paused", StringComparison.OrdinalIgnoreCase);
        var key = song.Id;
        MusicState previous;
        bool changed;
        lock (gate)
        {
            previous = state;
            changed = songKey != key;
            if (changed)
            {
                songKey = key;
                lyrics = new Lyrics("searching", []);
                lyricsAt = 0;
            }
            connected = true;
        }

        if (status.PicUrl != artworkUrl)
        {
            artworkUrl = status.PicUrl;
            artwork = null;
            _ = LoadArtworkAsync(status.PicUrl, token);
        }
        if (changed || (!lyricsLoading && lyrics.Lines.Length == 0 &&
            (lyricsAt == 0 || Stopwatch.GetElapsedTime(lyricsAt) > TimeSpan.FromSeconds(10))))
            _ = LoadLyricsAsync(key, status.Duration * 1000, token);

        var duration = status.Duration > 0 ? status.Duration * 1000 : (double?)null;
        var position = status.Progress >= 0 ? status.Progress * 1000 : (double?)null;
        var lastPlaying = playing ? DateTimeOffset.UtcNow
            : previous.Song == song ? previous.LastPlaying : DateTimeOffset.UtcNow;
        var next = new MusicState(
            "ready", song, playing, paused, lastPlaying, Stopwatch.GetTimestamp(), position, duration,
            status.PlaybackRate is > 0 and <= 8 ? status.PlaybackRate : 1,
            CanToggle: true, CanPrevious: true, CanNext: true, Artwork: artwork);
        lock (gate)
        {
            if (songKey == key) state = next;
        }
    }

    private async Task LoadLyricsAsync(string key, double durationMs, CancellationToken token)
    {
        lock (gate)
        {
            if (lyricsLoading && lyricsKey == key) return;
            lyricsLoading = true;
            lyricsKey = key;
        }
        try
        {
            var data = await http.GetFromJsonAsync<LxLyrics>($"{BaseUrl}/lyric-all", Json, token);
            var parsed = LyricsCatalog.ParseLx(data?.Lxlyric, data?.Lyric, durationMs > 0 ? durationMs : null);
            lock (gate)
            {
                if (songKey == key)
                {
                    lyrics = parsed;
                    lyricsAt = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException
            or InvalidOperationException)
        {
            lock (gate)
            {
                if (songKey == key)
                {
                    lyrics = new Lyrics("missing", [], null, durationMs > 0 ? durationMs : null);
                    lyricsAt = Stopwatch.GetTimestamp();
                }
            }
        }
        finally
        {
            lock (gate)
            {
                if (lyricsKey == key) lyricsLoading = false;
            }
        }
    }

    private async Task LoadArtworkAsync(string? url, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            var bytes = await artworkHttp.GetByteArrayAsync(url, token);
            if (bytes.Length is 0 or > 8 * 1024 * 1024) return;
            var image = ImageBytes.Create(bytes);
            lock (gate)
            {
                if (artworkUrl == url) artwork = image;
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
    }

    public async Task<string?> ControlAsync(string action, CancellationToken token)
    {
        bool playing;
        lock (gate) playing = state.Playing;
        var path = action switch
        {
            "toggle" => playing ? "/pause" : "/play",
            "previous" => "/skip-prev",
            "next" => "/skip-next",
            _ => null
        };
        if (path == null) return "不支持这个播放操作。";
        try
        {
            using var response = await http.GetAsync(BaseUrl + path, token);
            return response.IsSuccessStatusCode ? null : "洛雪音乐没有响应。";
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return "请先打开洛雪音乐的开放 API，并确认端口 23330 可用。";
        }
    }

    public override void Dispose()
    {
        http.Dispose();
        artworkHttp.Dispose();
        base.Dispose();
    }
}
