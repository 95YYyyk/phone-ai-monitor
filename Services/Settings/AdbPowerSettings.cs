using System.Text.Json;

sealed record AdbPowerPreferences(
    bool DimWhenIdle = true,
    int IdleMinutes = 5,
    int DimPercent = 25,
    bool KeepBrightnessWhileMedia = true)
{
    public bool IsValid() =>
        IdleMinutes is >= 1 and <= 120 &&
        DimPercent is >= 5 and <= 80;
}

sealed record AdbPowerRuntimeState(
    bool Dimmed,
    int OriginalBrightness,
    int OriginalMode,
    int DimmedBrightness)
{
    public bool IsValid() =>
        OriginalBrightness >= 0 &&
        DimmedBrightness >= 0 &&
        OriginalMode is 0 or 1;
}

sealed class AdbPowerSettings
{
    private readonly object gate = new();
    private readonly string preferenceFile;
    private readonly string stateFile;
    private AdbPowerPreferences preferences = new();
    private AdbPowerRuntimeState? runtimeState;

    public AdbPowerSettings(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        preferenceFile = Path.Combine(dataPath, "adb-power.json");
        stateFile = Path.Combine(dataPath, "adb-power-state.json");
        if (File.Exists(preferenceFile))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<AdbPowerPreferences>(File.ReadAllText(preferenceFile));
                if (saved is { } value && value.IsValid()) preferences = value;
            }
            catch (JsonException) { }
        }
        if (File.Exists(stateFile))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<AdbPowerRuntimeState>(File.ReadAllText(stateFile));
                if (saved is { } value && value.IsValid()) runtimeState = value;
            }
            catch (JsonException) { }
        }
    }

    public AdbPowerPreferences Read()
    {
        lock (gate) return preferences;
    }

    public bool Update(AdbPowerPreferences next)
    {
        if (!next.IsValid()) return false;
        lock (gate)
        {
            Save(preferenceFile, JsonSerializer.SerializeToUtf8Bytes(next));
            preferences = next;
        }
        return true;
    }

    public AdbPowerRuntimeState? ReadState()
    {
        lock (gate) return runtimeState;
    }

    public void SaveState(AdbPowerRuntimeState state)
    {
        lock (gate)
        {
            Save(stateFile, JsonSerializer.SerializeToUtf8Bytes(state));
            runtimeState = state;
        }
    }

    public void ClearState()
    {
        lock (gate)
        {
            runtimeState = null;
            try { File.Delete(stateFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Save(string destination, byte[] bytes)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
