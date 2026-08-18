using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ABB.Robotics.Math;
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
    private static readonly string PackageRoot = Path.Combine(Path.GetTempPath(), "RobotStudioUnityBridge", "export");

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
            var dir = Path.Combine(GeometryRoot, SanitizeFileName(mechanism.DisplayName));
            ExportMechanismGeometry(mechanism, dir);
        }
    }

    /// <summary>
    /// Where a package export lands if the user doesn't override it: next to the station file
    /// (so the export travels with the project), or under %TEMP% as a fallback for an unsaved
    /// station.
    /// </summary>
    public static string GetDefaultExportRoot()
    {
        var station = Station.ActiveStation;
        var stationDir = station?.FileInfo?.DirectoryName;
        return stationDir is not null ? Path.Combine(stationDir, "UnityExport") : PackageRoot;
    }

    /// <summary>
    /// Bundles a full hand-off package for one station: per-mechanism geometry (same as
    /// ExportGeometry, but written under the package folder) plus a copy of whatever's currently
    /// in the motion/I-O timeline file, tied together by a top-level package.json. This is the
    /// "one export action" the project's design settled on -- record a timeline first (separate
    /// ribbon toggle, since that has to run live while the robot moves), then call this to bundle
    /// everything recorded so far with a fresh geometry export.
    /// </summary>
    /// <param name="exportRoot">
    /// Destination root; each call creates its own timestamped subfolder underneath. Defaults to
    /// <see cref="GetDefaultExportRoot"/> when null (the caller/UI is expected to let the user
    /// override this).
    /// </param>
    public static void ExportPackage(string? exportRoot = null)
    {
        var station = Station.ActiveStation;
        if (station is null)
        {
            Log("ExportPackage: no active station.");
            return;
        }

        var root = exportRoot ?? GetDefaultExportRoot();
        var packageDir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        var geometryDir = Path.Combine(packageDir, "geometry");
        Directory.CreateDirectory(geometryDir);

        var mechanismEntries = new List<string>();
        foreach (var mechanism in station.GraphicComponents.OfType<Mechanism>())
        {
            var name = SanitizeFileName(mechanism.DisplayName);
            var dir = Path.Combine(geometryDir, name);
            ExportMechanismGeometry(mechanism, dir);
            mechanismEntries.Add($"{{\"name\":\"{JsonEscape(mechanism.DisplayName)}\",\"geometryFolder\":\"geometry/{JsonEscape(name)}\"}}");
        }

        var timelineIncluded = false;
        if (File.Exists(TimelineRecorder.OutputPath))
        {
            File.Copy(TimelineRecorder.OutputPath, Path.Combine(packageDir, "motion_timeline.jsonl"), overwrite: true);
            timelineIncluded = true;
        }
        else
        {
            Log("ExportPackage: no recorded timeline found -- click 'Record Motion + I/O' before exporting to include one.");
        }

        var package = new StringBuilder();
        package.AppendLine("{");
        package.AppendLine("  \"schemaVersion\":1,");
        package.AppendLine($"  \"exportedAt\":\"{DateTime.Now:O}\",");
        package.AppendLine($"  \"station\":\"{JsonEscape(station.Name)}\",");
        package.AppendLine($"  \"timelineIncluded\":{(timelineIncluded ? "true" : "false")},");
        package.AppendLine($"  \"timelineFile\":{(timelineIncluded ? "\"motion_timeline.jsonl\"" : "null")},");
        package.AppendLine("  \"mechanisms\":[" + string.Join(",", mechanismEntries) + "]");
        package.AppendLine("}");
        File.WriteAllText(Path.Combine(packageDir, "package.json"), package.ToString());

        Log($"ExportPackage: wrote package to '{packageDir}' (timeline included: {timelineIncluded}).");
    }

    private static void ExportMechanismGeometry(Mechanism mechanism, string dir)
    {
        Directory.CreateDirectory(dir);

        // Index every link up front so parent-link references below can resolve to a plain int
        // index instead of forcing the Unity side to re-derive it (and so branching mechanisms --
        // e.g. a gripper with more than one finger -- aren't silently assumed to be a simple
        // serial chain, which "parent = index - 1" would get wrong).
        var linkIndexByComponent = new Dictionary<ABB.Robotics.RobotStudio.Stations.GraphicComponent, int>();
        var linkIndex = 0;
        foreach (var link in mechanism.GraphicComponents)
        {
            linkIndexByComponent[link] = linkIndex++;
        }

        var linkEntries = new List<string>();
        linkIndex = 0;

        foreach (var link in mechanism.GraphicComponents)
        {
            var hasParentJoint = mechanism.GetParentJoint(link, out var jointIndex);
            var parentLinkIndex = -1;
            ABB.Robotics.RobotStudio.Stations.GraphicComponent? parentLinkComponent = null;
            if (hasParentJoint && mechanism.GetParentLink(jointIndex, out parentLinkComponent)
                && linkIndexByComponent.TryGetValue(parentLinkComponent, out var resolvedIndex))
            {
                parentLinkIndex = resolvedIndex;
            }

            // Relative to the parent link (or to the mechanism itself for the base link) --
            // NOT baked from whatever pose the joints happen to be in right now. Link.Mesh
            // geometry is static/local (see FindParts below); only Transform moves with the
            // joints, so this relative transform is exactly the fixed rest-pose offset Unity
            // needs to place each link under its parent in a matching hierarchy.
            var relativeMatrix = parentLinkIndex >= 0 && parentLinkComponent is not null
                ? link.Transform.GetRelativeTransform(parentLinkComponent)
                : link.Transform.Matrix;
            var (px, py, pz, qx, qy, qz, qw) = ConvertTransformToUnity(relativeMatrix);

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

            var inv = CultureInfo.InvariantCulture;
            linkEntries.Add(
                "    {" +
                $"\"index\":{linkIndex},\"name\":\"{JsonEscape(link.DisplayName)}\"," +
                $"\"parentJoint\":{(hasParentJoint ? jointIndex.ToString(inv) : "-1")}," +
                $"\"parentLink\":{parentLinkIndex.ToString(inv)}," +
                // Local position/rotation relative to parentLink (or to the mechanism root when
                // parentLink is -1), already converted to Unity's Y-up/left-handed convention --
                // same (x, z, -y) axis remap RobotStudio's own OBJ exporter uses for vertices, see
                // ConvertTransformToUnity. Same raw units as the OBJ vertices (unverified which --
                // whatever it is, it's consistent between the two, since neither path rescales).
                // Fixed-point (not "R"/general format) so this never emits scientific notation --
                // JSON allows it, but keeping it out avoids relying on the Unity-side JSON parser
                // handling it correctly.
                $"\"localPosition\":[{px.ToString("F6", inv)},{py.ToString("F6", inv)},{pz.ToString("F6", inv)}]," +
                $"\"localRotation\":[{qx.ToString("F6", inv)},{qy.ToString("F6", inv)},{qz.ToString("F6", inv)},{qw.ToString("F6", inv)}]," +
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
    /// Converts a RobotStudio-space rigid transform (right-handed, Z-up) to Unity's convention
    /// (left-handed, Y-up), using the exact same per-axis remap RobotStudio's own OBJ exporter
    /// applies to vertices: (x, y, z) -&gt; (x, z, -y). For a rigid transform (not just a point) the
    /// same change-of-basis has to be applied to the rotation too, via similarity transform
    /// C * R * C^T (C is the remap as a 3x3 matrix) -- this is standard change-of-basis math, not
    /// a guess, and was hand-verified against a concrete 90-degree case before relying on it here.
    /// </summary>
    private static (double x, double y, double z, double qx, double qy, double qz, double qw) ConvertTransformToUnity(Matrix4 rsRelative)
    {
        var c = new Matrix3(
            1, 0, 0,
            0, 0, 1,
            0, -1, 0);
        var cTranspose = new Matrix3(
            1, 0, 0,
            0, 0, -1,
            0, 1, 0);

        var rotationUnity = c * new Matrix3(rsRelative) * cTranspose;
        var t = rsRelative.Translation;
        var translationUnity = new Vector3(t.x, t.z, -t.y);

        var q = new Matrix4(rotationUnity, translationUnity).Quaternion;
        // ABB's Quaternion is scalar-first (q1=w, q2=x, q3=y, q4=z); Unity's is (x, y, z, w).
        return (translationUnity.x, translationUnity.y, translationUnity.z, q.q2, q.q3, q.q4, q.q1);
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
