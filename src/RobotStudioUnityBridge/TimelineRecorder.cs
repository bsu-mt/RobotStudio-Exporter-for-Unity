using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
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
    private static readonly MemoryStream LineBuffer = new();
    private static readonly Utf8JsonWriter JsonLineWriter = new(LineBuffer);

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

            WriteRecord(w =>
            {
                w.WriteString("type", "start");
                w.WritePropertyName("t");
                w.WriteRawValue("0", skipInputValidation: true);
            });
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

            var elapsed = ElapsedSeconds().ToString("F3", CultureInfo.InvariantCulture);
            WriteRecord(w =>
            {
                w.WriteString("type", "stop");
                w.WritePropertyName("t");
                w.WriteRawValue(elapsed, skipInputValidation: true);
            });

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
        var elapsed = ElapsedSeconds().ToString("F3", CultureInfo.InvariantCulture);
        WriteRecord(w =>
        {
            w.WriteString("type", "joint");
            w.WritePropertyName("t");
            w.WriteRawValue(elapsed, skipInputValidation: true);
            w.WriteString("mechanism", mechanism.DisplayName);
            w.WriteStartArray("values");
            foreach (var v in jointValues)
            {
                w.WriteRawValue(v.ToString("F6", CultureInfo.InvariantCulture), skipInputValidation: true);
            }
            w.WriteEndArray();
        });
    }

    private static void OnIOSignalValueChanged(object? sender, IOSignalChangedEventArgs e)
    {
        var signal = e.Signal;
        var elapsed = ElapsedSeconds().ToString("F3", CultureInfo.InvariantCulture);
        var formattedValue = FormatValue(signal.Value);
        WriteRecord(w =>
        {
            w.WriteString("type", "signal");
            w.WritePropertyName("t");
            w.WriteRawValue(elapsed, skipInputValidation: true);
            w.WriteString("name", signal.Name);
            w.WritePropertyName("value");
            if (formattedValue == "null")
            {
                w.WriteNullValue();
            }
            else
            {
                w.WriteRawValue(formattedValue, skipInputValidation: true);
            }
        });
    }

    private static double ElapsedSeconds() => (DateTime.UtcNow - _startedAt).TotalSeconds;

    private static string FormatValue(object value) => value switch
    {
        double d => d.ToString("F6", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
    };

    private static void WriteRecord(Action<Utf8JsonWriter> writeFields)
    {
        lock (Sync)
        {
            if (_writer is null)
            {
                return;
            }

            LineBuffer.SetLength(0);
            JsonLineWriter.Reset(LineBuffer);
            JsonLineWriter.WriteStartObject();
            writeFields(JsonLineWriter);
            JsonLineWriter.WriteEndObject();
            JsonLineWriter.Flush();

            _writer.WriteLine(Encoding.UTF8.GetString(LineBuffer.GetBuffer(), 0, (int)LineBuffer.Length));
        }
    }
}
