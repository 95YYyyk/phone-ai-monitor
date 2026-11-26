using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

sealed record HardwareReading(Dictionary<string, double?> Values, string? GpuName);

sealed class HardwareMetrics
{
    private readonly string temperatureFile;
    private static readonly string SystemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private readonly string? nvidiaSmi = new[]
    {
        Path.Combine(SystemDirectory, "nvidia-smi.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
    }.FirstOrDefault(File.Exists);
    private readonly SemaphoreSlim gate = new(1, 1);
    private CpuTimes previousCpu = ReadCpu();
    private long sampledAt;
    private long sensorsSampledAt;
    private long sensorsProbeAt;
    private Sensor[] sensors = [];
    private HardwareReading? cached;

    public HardwareMetrics(string dataPath)
    {
        temperatureFile = Path.Combine(dataPath, CpuTemperatureHelper.FileName);
    }

    public async Task<HardwareReading> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (cached != null && Stopwatch.GetElapsedTime(sampledAt) < TimeSpan.FromSeconds(2)) return cached;
            var gpu = ReadNvidia(token);
            var sensorsTask = ReadSensors(token);
            var reading = new Dictionary<string, double?> { ["cpu.usage"] = CpuUsage() };
            var memory = new MemoryStatus { Size = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical > 0)
            {
                var used = memory.TotalPhysical - memory.AvailablePhysical;
                reading["ram.total"] = memory.TotalPhysical / 1073741824d;
                reading["ram.used"] = used / 1073741824d;
                reading["ram.usage"] = 100d * used / memory.TotalPhysical;
            }
            var vendorSensors = await sensorsTask;
            reading["cpu.temperature"] = vendorSensors
                .Where(value => IsCpuSensor(value.Identifier) &&
                    ((value.Identifier ?? "").Contains("/temperature/", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(value => value.Name == "CPU Package")
                .ThenByDescending(value => value.Value)
                .FirstOrDefault()?.Value ?? ReadHelperTemperature();
            var (name, nv) = await gpu;
            var gpuRoot = vendorSensors.Select(value => value.Identifier?.Split('/'))
                .FirstOrDefault(parts => parts is { Length: >= 5 } &&
                    parts[1].StartsWith("gpu", StringComparison.OrdinalIgnoreCase));
            var fallback = gpuRoot == null ? [] : vendorSensors.Where(sensor =>
                (sensor.Identifier ?? "").StartsWith($"/{gpuRoot[1]}/{gpuRoot[2]}/", StringComparison.OrdinalIgnoreCase)).ToArray();
            reading["gpu.usage"] = nv.ElementAtOrDefault(0) ?? Find(fallback, "load", "GPU Core", "GPU D3D 3D");
            reading["gpu.temperature"] = nv.ElementAtOrDefault(1) ?? Find(fallback, "temperature", "GPU Core");
            reading["gpu.power"] = nv.ElementAtOrDefault(2) ?? Find(fallback, "power", "GPU Package", "GPU Power");
            cached = new HardwareReading(reading, name);
            sampledAt = Stopwatch.GetTimestamp();
            return cached;
        }
        finally { gate.Release(); }
    }

    private double? CpuUsage()
    {
        var before = previousCpu;
        previousCpu = ReadCpu();
        var idle = previousCpu.Idle - before.Idle;
        // GetSystemTimes includes idle time in kernel time.
        var total = previousCpu.Kernel - before.Kernel + previousCpu.User - before.User;
        return before.Kernel == 0 || total <= 0 ? null : Math.Clamp(100d * (1 - (double)idle / total), 0, 100);
    }

    private async Task<(string? Name, double?[] Values)> ReadNvidia(CancellationToken token)
    {
        if (nvidiaSmi == null) return (null, []);
        var output = await Run(nvidiaSmi, token,
            "--id=0", "--query-gpu=name,utilization.gpu,temperature.gpu,power.draw",
            "--format=csv,noheader,nounits");
        var first = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var fields = first?.Split(',', StringSplitOptions.TrimEntries);
        return fields is { Length: 4 } ? (fields[0], fields.Skip(1).Select(Parse).ToArray()) : (null, []);
    }

    private async Task<Sensor[]> ReadSensors(CancellationToken token)
    {
        var lifetime = sensors.Length == 0 ? 30 : 3;
        if (sensorsSampledAt != 0 && Stopwatch.GetElapsedTime(sensorsSampledAt) < TimeSpan.FromSeconds(lifetime))
            return sensors;
        // LibreHardwareMonitor may not be installed or running. Avoid spawning
        // PowerShell every few seconds just to discover an absent WMI namespace.
        if (sensors.Length == 0 && sensorsProbeAt != 0 &&
            Stopwatch.GetElapsedTime(sensorsProbeAt) < TimeSpan.FromSeconds(15))
            return sensors;
        sensorsProbeAt = Stopwatch.GetTimestamp();
        if (!LibreHardwareMonitorRunning())
        {
            if (sensors.Length != 0)
            {
                sensors = [];
                sensorsSampledAt = Stopwatch.GetTimestamp();
            }
            return sensors;
        }
        const string query = """
            $rows = @(Get-CimInstance -Namespace root/LibreHardwareMonitor -ClassName Sensor -ErrorAction SilentlyContinue |
              Where-Object {
                ($_.SensorType -eq 'Temperature' -and ($_.Identifier -like '/cpu*' -or $_.Identifier -like '/amdcpu*' -or $_.Identifier -like '/intelcpu*' -or $_.Identifier -like '/gpu*')) -or
                ($_.SensorType -in 'Load','Power' -and $_.Identifier -like '/gpu*')
              } | Select-Object Identifier,Name,Value)
            ConvertTo-Json -InputObject $rows -Compress
            """;
        var output = await Run(Path.Combine(SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            token, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", query);
        try { sensors = output == null ? [] : JsonSerializer.Deserialize<Sensor[]>(output) ?? []; }
        catch (JsonException) { sensors = []; }
        sensorsSampledAt = Stopwatch.GetTimestamp();
        return sensors;
    }

    private static bool LibreHardwareMonitorRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("LibreHardwareMonitor");
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static double? Find(Sensor[] sensors, string kind, params string[] names) => sensors
        .FirstOrDefault(sensor => (sensor.Identifier ?? "").Contains($"/{kind}/", StringComparison.OrdinalIgnoreCase) &&
            names.Contains(sensor.Name, StringComparer.OrdinalIgnoreCase))?.Value;

    private static bool IsCpuSensor(string? identifier) => identifier != null &&
        (identifier.StartsWith("/cpu", StringComparison.OrdinalIgnoreCase) ||
         identifier.StartsWith("/amdcpu", StringComparison.OrdinalIgnoreCase) ||
         identifier.StartsWith("/intelcpu", StringComparison.OrdinalIgnoreCase));

    private static double? Parse(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
        double.IsFinite(number) && number >= 0 ? number : null;

    private static async Task<string?> Run(string executable, CancellationToken token, params string[] args)
    {
        var launch = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        };
        foreach (var arg in args) launch.ArgumentList.Add(arg);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var process = Process.Start(launch);
            if (process == null) return null;
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(limit.Token);
                await process.WaitForExitAsync(limit.Token);
                return process.ExitCode == 0 ? output : null;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                return null;
            }
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private static CpuTimes ReadCpu() => GetSystemTimes(out var idle, out var kernel, out var user) ?
        new(idle, kernel, user) : default;

    private double? ReadHelperTemperature()
    {
        try
        {
            if (!File.Exists(temperatureFile) ||
                DateTime.UtcNow - File.GetLastWriteTimeUtc(temperatureFile) > TimeSpan.FromSeconds(6)) return null;
            var text = File.ReadAllText(temperatureFile).Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                value is > 0 and < 150 ? value : null;
        }
        catch (IOException) { return null; }
    }

    private readonly record struct CpuTimes(long Idle, long Kernel, long User);
    private sealed record Sensor(string? Identifier, string? Name, double? Value);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Size, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
