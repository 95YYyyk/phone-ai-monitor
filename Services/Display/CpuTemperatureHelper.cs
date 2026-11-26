using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using LibreHardwareMonitor.Hardware;

sealed class CpuTemperatureHelper
{
    public const string FileName = "cpu-temperature.txt";
    private const string MutexName = @"Local\DeskSideDisplay.CpuTemperature";

    public static async Task RunAsync(string output)
    {
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created) return;

        var computer = new Computer { IsCpuEnabled = true };
        try
        {
            computer.Open();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                if (Read(computer) is double temperature)
                    PanelSettings.Save(output, Encoding.UTF8.GetBytes(
                        temperature.ToString("0.0", CultureInfo.InvariantCulture)));
            } while (await timer.WaitForNextTickAsync());
        }
        catch (Exception error)
        {
            try { File.WriteAllText(output + ".error", error.ToString()); } catch { }
        }
        finally
        {
            computer.Close();
        }
    }

    public static void TryStart(string dataPath, ILogger? logger)
    {
        var output = Path.Combine(dataPath, FileName);
        if (File.Exists(output) && DateTime.UtcNow - File.GetLastWriteTimeUtc(output) < TimeSpan.FromSeconds(6))
            return;

        var exe = Path.Combine(AppContext.BaseDirectory, "DeskSideDisplay.exe");
        ProcessStartInfo start;
        if (File.Exists(exe))
            start = new ProcessStartInfo(exe)
            {
                Arguments = $"--cpu-temp-helper --temperature-file=\"{output}\""
            };
        else if (Assembly.GetEntryAssembly()?.Location is { Length: > 0 } assembly)
            start = new ProcessStartInfo("dotnet")
            {
                Arguments = $"\"{assembly}\" --cpu-temp-helper --temperature-file=\"{output}\""
            };
        else return;

        start.UseShellExecute = true;
        start.Verb = "runas";
        start.WindowStyle = ProcessWindowStyle.Hidden;
        start.WorkingDirectory = AppContext.BaseDirectory;
        try
        {
            using var process = Process.Start(start);
            logger?.LogInformation("Started the CPU temperature helper");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException
            or FileNotFoundException)
        {
            logger?.LogInformation("CPU temperature helper was not started: {Message}", error.Message);
        }
    }

    private static double? Read(Computer computer)
    {
        foreach (var hardware in computer.Hardware)
        {
            hardware.Update();
            foreach (var name in new[] { "CPU Package", "Core Average", "Core Max" })
            {
                var sensor = hardware.Sensors.FirstOrDefault(item => item.SensorType == SensorType.Temperature &&
                    string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                if (sensor?.Value is float value && value is > 0 and < 150) return value;
            }
            foreach (var child in hardware.SubHardware)
            {
                child.Update();
                var sensor = child.Sensors.FirstOrDefault(item => item.SensorType == SensorType.Temperature &&
                    item.Name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase));
                if (sensor?.Value is float value && value is > 0 and < 150) return value;
            }
        }
        return null;
    }
}
