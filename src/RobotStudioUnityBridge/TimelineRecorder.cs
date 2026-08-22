using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ABB.Robotics.Math;
using ABB.Robotics.RobotStudio.Stations;

namespace RobotStudioUnityBridge;

/// <summary>
/// Records joint values (all mechanisms, station-wide) and I/O signal value changes to a
/// JSON-lines timeline file while recording is active. Intended to be running while a task/RAPID
/// script actually drives the robot, since RobotStudio doesn't expose pre-resolved joint
/// trajectories anywhere -- this is how we capture what the robot really did.
///
/// Also polls non-mechanism ("static") components' transforms on every joint-change tick, since
/// RobotStudio has no event for "a Smart Component re-parented/moved this part" (e.g. the gripper
/// picking up a stick) -- AnyJointValuesChanged is the only per-tick signal available, so it
/// doubles as the polling clock for pick-up motion too. A static component is only written when
/// its transform actually changed since the last tick, to avoid flooding the file with an
/// unchanging matrix for every part on every joint tick.
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
    private static readonly Dictionary<GraphicComponent, Matrix4> TrackedStaticComponents = new();

    public static void Start(IEnumerable<GraphicComponent> staticComponents)
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

            TrackedStaticComponents.Clear();
            foreach (var component in staticComponents)
            {
                TrackedStaticComponents[component] = component.Transform.Matrix;
            }

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
            TrackedStaticComponents.Clear();
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

        PollStaticComponents(elapsed);
    }

    private static void PollStaticComponents(string elapsed)
    {
        foreach (var component in TrackedStaticComponents.Keys.ToList())
        {
            var matrix = component.Transform.Matrix;
            var t = matrix.Translation;
            var q = matrix.Quaternion; // ABB Quaternion is scalar-first: q1=w, q2=x, q3=y, q4=z.

            var prev = TrackedStaticComponents[component];
            var prevT = prev.Translation;
            var prevQ = prev.Quaternion;
            var unchanged = t.x == prevT.x && t.y == prevT.y && t.z == prevT.z
                && q.q1 == prevQ.q1 && q.q2 == prevQ.q2 && q.q3 == prevQ.q3 && q.q4 == prevQ.q4;
            if (unchanged)
            {
                continue;
            }

            TrackedStaticComponents[component] = matrix;
            var name = component.DisplayName;
            WriteRecord(w =>
            {
                w.WriteString("type", "staticTransform");
                w.WritePropertyName("t");
                w.WriteRawValue(elapsed, skipInputValidation: true);
                w.WriteString("component", name);
                w.WriteStartArray("position");
                w.WriteRawValue(t.x.ToString("F6", CultureInfo.InvariantCulture), skipInputValidation: true);
                w.WriteRawValue(t.y.ToString("F6", CultureInfo.InvariantCulture), skipInputValidation: true);
                w.WriteRawValue(t.z.ToString("F6", CultureInfo.InvariantCulture), skipInputValidation: true);
                w.WriteEndArray();
                w.WriteStartArray("rotation");
                w.WriteRawValue(q.q1.ToString("F6", CultureInfo.InvariantCulture), skipInputValidation: true);
                w.WriteRawValue(q.q2.ToString("F6", CultureInfo.InvariantCulture), skipInputValidation: true);
                w.WriteRawValue(q.q3.ToString("F6", CultureInfo.InvariantCulture), skipInputValidation: true);
                w.WriteRawValue(q.q4.ToString("F6", CultureInfo.InvariantCulture), skipInputValidation: true);
                w.WriteEndArray();
            });
        }
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
