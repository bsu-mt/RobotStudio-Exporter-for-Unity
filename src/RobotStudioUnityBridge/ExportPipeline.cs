using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ABB.Robotics.RobotStudio.Stations;

namespace RobotStudioUnityBridge;

/// <summary>
/// The export pipeline itself. Currently just a read-only prototype (step 3 of the README's
/// "What to Do in This Session"): confirm the add-in loads and can read the active station's
/// mechanisms and joint values before attempting any real export logic.
/// </summary>
public static class ExportPipeline
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "RobotStudioUnityBridge.log");
    private static readonly string GeometryRoot = Path.Combine(Path.GetTempPath(), "RobotStudioUnityBridge", "geometry");

    public static void LogActiveStationSummary()
    {
        Log("Add-in loaded.");

        var station = Station.ActiveStation;
        if (station is null)
        {
            Log("No active station.");
            return;
        }

        Log($"Active station: '{station.Name}'");

        foreach (var mechanism in station.GraphicComponents.OfType<Mechanism>())
        {
            var jointValues = mechanism.GetJointValues();
            Log($"  Mechanism '{mechanism.DisplayName}' ({mechanism.MechanismType}): " +
                $"{mechanism.NumActiveJoints} active joints, values = [{string.Join(", ", jointValues)}]");
        }
    }

    /// <summary>
    /// Exports every link's geometry (OBJ+MTL, via RobotStudio's own built-in exporter -- see
    /// ObjGraphicConverter/ObjExporter in RobotStudio.Services.GraphicConverters.dll, found by
    /// decompiling; no external CAD conversion tool needed) plus a manifest.json describing the
    /// link/joint hierarchy, for every mechanism in the active station.
    /// </summary>
    public static void ExportGeometry()
    {
        var station = Station.ActiveStation;
        if (station is null)
        {
            Log("ExportGeometry: no active station.");
            return;
        }

        foreach (var mechanism in station.GraphicComponents.OfType<Mechanism>())
        {
            ExportMechanismGeometry(mechanism);
        }
    }

    private static void ExportMechanismGeometry(Mechanism mechanism)
    {
        var dir = Path.Combine(GeometryRoot, SanitizeFileName(mechanism.DisplayName));
        Directory.CreateDirectory(dir);

        var linkEntries = new List<string>();
        var linkIndex = 0;

        foreach (var link in mechanism.GraphicComponents)
        {
            var hasParentJoint = mechanism.GetParentJoint(link, out var jointIndex);
            var files = new List<string>();

            foreach (var part in FindParts(link))
            {
                var fileName = $"link{linkIndex}_{SanitizeFileName(part.DisplayName)}.obj";
                var path = Path.Combine(dir, fileName);
                try
                {
                    part.SaveAs(path);
                    files.Add(fileName);
                }
                catch (Exception ex)
                {
                    Log($"ExportGeometry: failed to export part '{part.DisplayName}' of link {linkIndex}: {ex.Message}");
                }
            }

            linkEntries.Add(
                "    {" +
                $"\"index\":{linkIndex},\"name\":\"{JsonEscape(link.DisplayName)}\"," +
                $"\"parentJoint\":{(hasParentJoint ? jointIndex.ToString(CultureInfo.InvariantCulture) : "null")}," +
                $"\"files\":[{string.Join(",", files.Select(f => $"\"{JsonEscape(f)}\""))}]" +
                "}");

            linkIndex++;
        }

        var manifest = new StringBuilder();
        manifest.AppendLine("{");
        manifest.AppendLine($"  \"mechanism\":\"{JsonEscape(mechanism.DisplayName)}\",");
        manifest.AppendLine("  \"links\":[");
        manifest.AppendLine(string.Join(",\n", linkEntries));
        manifest.AppendLine("  ]");
        manifest.AppendLine("}");
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest.ToString());

        Log($"ExportGeometry: exported {linkIndex} link(s) for mechanism '{mechanism.DisplayName}' to '{dir}'.");
    }

    /// <summary>
    /// A link's geometry is a Part, but not always directly -- it can also sit on nested
    /// ChildInstances -- so walk the whole subtree under the link looking for Parts that actually
    /// carry geometry.
    /// </summary>
    private static IEnumerable<Part> FindParts(ABB.Robotics.RobotStudio.Stations.GraphicComponent component)
    {
        // Part.HasGeometry checks for a live modeling-kernel handle (GeoPart), which isn't what
        // determines whether there's actually mesh to export -- RobotStudio's own OBJ exporter
        // guards on triangle count instead (ConverterUtils.HasTrianglesRec, found by decompiling
        // RobotStudio.Services.GraphicConverters.dll), so match that here.
        if (component is Part part && part.Mesh.GetInfo().NumberOfTriangles > 0)
        {
            yield return part;
        }

        foreach (var child in component.ChildInstances)
        {
            foreach (var found in FindParts(child))
            {
                yield return found;
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void Log(string message)
    {
        // No console when hosted inside RobotStudio's process, so write to a plain log file
        // instead -- this is how we'll confirm the add-in actually ran.
        File.AppendAllText(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
    }
}
