using System;
using System.Globalization;
using System.IO;
using System.Text;
using ABB.Robotics.RobotStudio.Stations;

namespace RobotStudioUnityBridge;

/// <summary>
/// Records joint values (all mechanisms, station-wide) and I/O signal value changes to a
/// JSON-lines timeline file while recording is active. Intended to be running while a task/RAPID
/// script actually drives the robot, since RobotStudio doesn't expose pre-resolved joint
/// trajectories anywhere -- this is how we capture what the robot really did.
/// </summary>
public static class TimelineRecorder
{
    public static string OutputPath { get; } = Path.Combine(Path.GetTempPath(), "RobotStudioUnityBridge.timeline.jsonl");

    public static bool IsRecording { get; private set; }

    private static readonly object Sync = new();
    private static DateTime _startedAt;
    private static StreamWriter? _writer;

    public static void Start()
    {
        lock (Sync)
        {
            if (IsRecording)
            {
                return;
            }

            _writer = new StreamWriter(OutputPath, append: false, Encoding.UTF8) { AutoFlush = true };
            _startedAt = DateTime.UtcNow;
            IsRecording = true;

            Mechanism.AnyJointValuesChanged += OnAnyJointValuesChanged;

            if (Station.ActiveStation is { } station)
            {
                station.IOSignalValueChanged += OnIOSignalValueChanged;
            }

            WriteLine("{\"type\":\"start\",\"t\":0}");
        }
    }

    public static void Stop()
    {
        lock (Sync)
        {
            if (!IsRecording)
            {
                return;
            }

            Mechanism.AnyJointValuesChanged -= OnAnyJointValuesChanged;

            if (Station.ActiveStation is { } station)
            {
                station.IOSignalValueChanged -= OnIOSignalValueChanged;
            }

            WriteLine($"{{\"type\":\"stop\",\"t\":{ElapsedSeconds().ToString("F3", CultureInfo.InvariantCulture)}}}");

            _writer?.Dispose();
            _writer = null;
            IsRecording = false;
        }
    }

    private static void OnAnyJointValuesChanged(object? sender, EventArgs e)
    {
        if (sender is not Mechanism mechanism)
        {
            return;
        }

        var jointValues = mechanism.GetJointValues();
        var values = string.Join(",", Array.ConvertAll(jointValues, v => v.ToString("F6", CultureInfo.InvariantCulture)));
        WriteLine($"{{\"type\":\"joint\",\"t\":{ElapsedSeconds().ToString("F3", CultureInfo.InvariantCulture)}," +
                  $"\"mechanism\":\"{Escape(mechanism.DisplayName)}\",\"values\":[{values}]}}");
    }

    private static void OnIOSignalValueChanged(object? sender, IOSignalChangedEventArgs e)
    {
        var signal = e.Signal;
        WriteLine($"{{\"type\":\"signal\",\"t\":{ElapsedSeconds().ToString("F3", CultureInfo.InvariantCulture)}," +
                  $"\"name\":\"{Escape(signal.Name)}\",\"value\":{FormatValue(signal.Value)}}}");
    }

    private static double ElapsedSeconds() => (DateTime.UtcNow - _startedAt).TotalSeconds;

    private static string FormatValue(object value) => value switch
    {
        double d => d.ToString("F6", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
    };

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void WriteLine(string json)
    {
        lock (Sync)
        {
            _writer?.WriteLine(json);
        }
    }
}
