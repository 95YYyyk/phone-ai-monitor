using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

sealed record AdbPowerStatus(
    string State,
    bool Dimmed,
    int? Brightness,
    int IdleSeconds,
    bool AiRunning,
    string? LastAction);

sealed class AdbPowerLink(
    AdbPowerSettings settings,
    UsbBridge usb,
    CodexActivity codex,
    AntigravityActivity antigravity,
    MusicPlayers music,
    ILogger<AdbPowerLink> logger) : BackgroundService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object statusGate = new();
    private AdbPowerStatus status = new("waiting", false, null, 0, false, null);
    private DateTimeOffset resumeGraceUntil;
    private bool dimmed;
    private bool recoveryChecked;

    public AdbPowerStatus ReadStatus()
    {
        lock (statusGate) return status;
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    await TickAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    logger.LogDebug(error, "ADB power link tick failed");
                    SetStatus(state: "error", action: "电源联动检查失败");
                }
            }
        }
        finally
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            await RestoreOnShutdownAsync();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Suspend)
        {
            // Keep this short. Windows may suspend the machine as soon as the event returns.
            try { SuspendAsync().GetAwaiter().GetResult(); }
            catch (Exception error) { logger.LogDebug(error, "Could not turn off the phone before suspend"); }
        }
        else if (args.Mode == PowerModes.Resume)
        {
            _ = Task.Run(ResumeAsync);
        }
    }

    private async Task SuspendAsync()
    {
        var preferences = settings.Read();
        if (!preferences.SleepScreenOff || !await gate.WaitAsync(TimeSpan.FromSeconds(2)))
            return;
        try
        {
            if (usb.Status != "ready") return;
            var result = await usb.ShellAsync(CommandTimeout, CancellationToken.None, "input", "keyevent", "223");
            SetStatus(action: result.Ok ? "睡眠：已关闭手机屏幕" : "睡眠：手机屏幕命令失败");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ResumeAsync()
    {
        resumeGraceUntil = DateTimeOffset.UtcNow.AddSeconds(30);
        SetStatus(state: "resuming", action: "电脑已唤醒，正在恢复手机");
        try
        {
            if (!await WaitForAdbAsync(TimeSpan.FromSeconds(15))) return;
            var preferences = settings.Read();
            if (dimmed) await RestoreBrightnessAsync(CancellationToken.None);
            if (!preferences.WakeOnResume) return;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (usb.Status != "ready")
                {
                    await Task.Delay(1000);
                    continue;
                }
                var wake = await usb.ShellAsync(CommandTimeout, CancellationToken.None, "input", "keyevent", "224");
                if (!wake.Ok)
                {
                    await Task.Delay(1000);
                    continue;
                }
                var dismiss = await usb.ShellAsync(CommandTimeout, CancellationToken.None, "wm", "dismiss-keyguard");
                if (!dismiss.Ok)
                    await usb.ShellAsync(CommandTimeout, CancellationToken.None, "input", "keyevent", "82");
                SetStatus(action: "唤醒：已亮屏并收起非安全锁屏");
                return;
            }
            SetStatus(action: "唤醒：手机屏幕命令失败");
        }
        catch (Exception error)
        {
            logger.LogDebug(error, "Could not wake the phone after resume");
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        var preferences = settings.Read();
        var idleSeconds = PcIdleSeconds();
        var aiRunning = HasRunningTasks();
        if (usb.Status != "ready")
        {
            SetStatus(state: "waiting", idleSeconds: idleSeconds, aiRunning: aiRunning);
            return;
        }

        if (!recoveryChecked)
        {
            if (!await RecoverStateAsync(token)) return;
            recoveryChecked = true;
        }

        if (!preferences.DimWhenIdle)
        {
            if (dimmed) await RestoreBrightnessAsync(token);
            SetStatus(state: "disabled", idleSeconds: idleSeconds, aiRunning: aiRunning);
            return;
        }

        if (DateTimeOffset.UtcNow < resumeGraceUntil)
        {
            if (dimmed) await RestoreBrightnessAsync(token);
            SetStatus(state: "active", idleSeconds: idleSeconds, aiRunning: aiRunning);
            return;
        }

        var mediaPlaying = preferences.KeepBrightnessWhileMedia && IsMediaPlaying();
        var shouldDim = idleSeconds >= preferences.IdleMinutes * 60 && !aiRunning && !mediaPlaying;
        if (shouldDim)
        {
            if (!dimmed) await DimAsync(preferences, token);
            SetStatus(state: dimmed ? "dimmed" : "active", idleSeconds: idleSeconds, aiRunning: aiRunning);
        }
        else
        {
            if (dimmed) await RestoreBrightnessAsync(token);
            SetStatus(state: "active", idleSeconds: idleSeconds, aiRunning: aiRunning);
        }
    }

    private async Task<bool> RecoverStateAsync(CancellationToken token)
    {
        if (settings.ReadState() is not { Dimmed: true } state)
        {
            dimmed = false;
            return true;
        }

        var current = await ReadIntAsync("screen_brightness", token);
        if (current is null) return false;
        dimmed = true;
        if (current != state.DimmedBrightness)
        {
            // The user changed brightness after a restart; do not override their choice.
            settings.ClearState();
            dimmed = false;
            return true;
        }
        await RestoreBrightnessAsync(token);
        return true;
    }

    private async Task DimAsync(AdbPowerPreferences preferences, CancellationToken token)
    {
        var original = await ReadIntAsync("screen_brightness", token);
        var mode = await ReadIntAsync("screen_brightness_mode", token);
        // Do not make an already-dark screen nearly black.
        if (original is null or <= 16 || mode is null) return;
        var target = Math.Clamp((int)Math.Round(original.Value * preferences.DimPercent / 100d), 1, original.Value - 1);
        if (target >= original.Value) return;

        settings.SaveState(new AdbPowerRuntimeState(true, original.Value, mode.Value, target));
        dimmed = true;
        var manual = await usb.ShellAsync(CommandTimeout, token,
            "settings", "put", "system", "screen_brightness_mode", "0");
        var brightness = await usb.ShellAsync(CommandTimeout, token,
            "settings", "put", "system", "screen_brightness",
            target.ToString(CultureInfo.InvariantCulture));
        if (!manual.Ok || !brightness.Ok)
        {
            await RestoreBrightnessAsync(token);
            return;
        }
        SetStatus(state: "dimmed", dimmed: true, brightness: target,
            action: "电脑空闲：已降低手机亮度");
    }

    private async Task RestoreBrightnessAsync(CancellationToken token)
    {
        var state = settings.ReadState();
        if (state is null)
        {
            dimmed = false;
            return;
        }
        var brightness = await usb.ShellAsync(CommandTimeout, token,
            "settings", "put", "system", "screen_brightness",
            state.OriginalBrightness.ToString(CultureInfo.InvariantCulture));
        var mode = await usb.ShellAsync(CommandTimeout, token,
            "settings", "put", "system", "screen_brightness_mode",
            state.OriginalMode.ToString(CultureInfo.InvariantCulture));
        if (!brightness.Ok || !mode.Ok) return;
        settings.ClearState();
        dimmed = false;
        SetStatus(state: "active", dimmed: false, brightness: state.OriginalBrightness,
            action: "已恢复手机亮度");
    }

    private async Task RestoreOnShutdownAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (await gate.WaitAsync(TimeSpan.FromSeconds(3), timeout.Token))
            {
                try { await RestoreBrightnessAsync(timeout.Token); }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { logger.LogDebug(error, "Could not restore phone brightness on shutdown"); }
    }

    private async Task<int?> ReadIntAsync(string key, CancellationToken token)
    {
        var result = await usb.ShellAsync(CommandTimeout, token, "settings", "get", "system", key);
        if (!result.Ok) return null;
        return int.TryParse(result.Output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : null;
    }

    private bool HasRunningTasks() =>
        codex.Read().Tasks.Any(task => task.Status == "running") ||
        antigravity.Read().Tasks.Any(task => task.Status == "running");

    private bool IsMediaPlaying()
    {
        var playback = music.Read();
        return playback.Apple.Playing || playback.Netease.Playing || playback.Lx.Playing;
    }

    private async Task<bool> WaitForAdbAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (usb.Status == "ready") return true;
            await Task.Delay(500);
        }
        return usb.Status == "ready";
    }

    private void SetStatus(string? state = null, bool? dimmed = null, int? brightness = null,
        int? idleSeconds = null, bool? aiRunning = null, string? action = null)
    {
        lock (statusGate)
        {
            status = status with
            {
                State = state ?? status.State,
                Dimmed = dimmed ?? status.Dimmed,
                Brightness = brightness ?? status.Brightness,
                IdleSeconds = idleSeconds ?? status.IdleSeconds,
                AiRunning = aiRunning ?? status.AiRunning,
                LastAction = action ?? status.LastAction
            };
        }
    }

    private static int PcIdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        var idle = (uint)GetTickCount64() - info.Tick;
        return (int)Math.Clamp(idle / 1000, 0, 86_400);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Tick;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}
