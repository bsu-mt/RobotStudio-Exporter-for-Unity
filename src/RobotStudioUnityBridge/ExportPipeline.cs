using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ABB.Robotics.Math;
using ABB.Robotics.RobotStudio.Stations;
using RobotStudio.API.Internal;

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

        var (mechanisms, staticComponents) = FindExportableComponents(station);

        foreach (var mechanism in mechanisms)
        {
            var dir = Path.Combine(GeometryRoot, SanitizeFileName(mechanism.DisplayName));
            ExportMechanismGeometry(mechanism, dir);
        }

        foreach (var component in staticComponents)
        {
            var dir = Path.Combine(GeometryRoot, SanitizeFileName(component.DisplayName));
            ExportStaticComponentGeometry(component, dir);
        }
    }

    /// <summary>
    /// Walks the whole station tree (not just top-level GraphicComponents) looking for every
    /// Mechanism, at any nesting depth -- needed because a mechanism can sit inside a SmartComponent
    /// (e.g. Smart_Gripper_Servo_Fingers lives inside SmartComponent_1, not directly on the
    /// station), so a top-level-only scan silently misses it. Everything else -- every top-level
    /// station child that isn't itself a Mechanism and doesn't contain one -- is exported as one
    /// grouped static component (via FindParts, same as before), so a top-level assembly like
    /// "assembly_table" still yields a single manifest even if it has multiple part children,
    /// instead of exploding into one manifest per leaf part.
    /// </summary>
    private static (List<Mechanism> Mechanisms, List<ABB.Robotics.RobotStudio.Stations.GraphicComponent> StaticComponents) FindExportableComponents(Station station)
    {
        var mechanisms = new List<Mechanism>();
        var staticComponents = new List<ABB.Robotics.RobotStudio.Stations.GraphicComponent>();

        bool CollectMechanisms(ABB.Robotics.RobotStudio.Stations.GraphicComponent component)
        {
            if (component is Mechanism mechanism)
            {
                mechanisms.Add(mechanism);
                return true;
            }

            var containsMechanism = false;
            foreach (var child in component.ChildInstances)
            {
                if (CollectMechanisms(child))
                {
                    containsMechanism = true;
                }
            }

            return containsMechanism;
        }

        foreach (var component in (IEnumerable<ABB.Robotics.RobotStudio.Stations.GraphicComponent>)station.GraphicComponents)
        {
            if (!CollectMechanisms(component))
            {
                staticComponents.Add(component);
            }
        }

        return (mechanisms, staticComponents);
    }

    /// <summary>
    /// Static (non-mechanism) components of the active station -- e.g. the wooden sticks that get
    /// picked up by the gripper -- for TimelineRecorder to poll while recording, since their
    /// motion comes from re-parenting/attachment rather than a joint value change. Null if there's
    /// no active station.
    /// </summary>
    public static List<ABB.Robotics.RobotStudio.Stations.GraphicComponent>? GetStaticComponents()
    {
        var station = Station.ActiveStation;
        return station is null ? null : FindExportableComponents(station).StaticComponents;
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

        var (mechanisms, staticComponents) = FindExportableComponents(station);
        var mountsByMechanism = FindMechanismMounts(mechanisms);

        var mechanismEntries = new List<(string Name, string GeometryFolder, MountInfo? Mount)>();
        foreach (var mechanism in mechanisms)
        {
            var name = SanitizeFileName(mechanism.DisplayName);
            var dir = Path.Combine(geometryDir, name);
            ExportMechanismGeometry(mechanism, dir);
            mountsByMechanism.TryGetValue(mechanism, out var mount);
            mechanismEntries.Add((mechanism.DisplayName, $"geometry/{name}", mount));
        }

        foreach (var component in staticComponents)
        {
            var name = SanitizeFileName(component.DisplayName);
            var dir = Path.Combine(geometryDir, name);
            ExportStaticComponentGeometry(component, dir);
            mechanismEntries.Add((component.DisplayName, $"geometry/{name}", null));
        }

        var timelineIncluded = false;
        if (File.Exists(TimelineRecorder.OutputPath))
        {
            File.Copy(TimelineRecorder.OutputPath, Path.Combine(packageDir, "motion_timeline.jsonl"), overwrite: true);
            ExportLinkTimeline(mechanisms, staticComponents, TimelineRecorder.OutputPath, Path.Combine(packageDir, "link_timeline.jsonl"));
            timelineIncluded = true;
        }
        else
        {
            Log("ExportPackage: no recorded timeline found -- click 'Record Motion + I/O' before exporting to include one.");
        }

        using (var stream = File.Create(Path.Combine(packageDir, "package.json")))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("exportedAt", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("station", station.Name);
            writer.WriteBoolean("timelineIncluded", timelineIncluded);
            if (timelineIncluded)
            {
                writer.WriteString("timelineFile", "motion_timeline.jsonl");
                writer.WriteString("linkTimelineFile", "link_timeline.jsonl");
            }
            else
            {
                writer.WriteNull("timelineFile");
                writer.WriteNull("linkTimelineFile");
            }

            var inv = CultureInfo.InvariantCulture;
            writer.WriteStartArray("mechanisms");
            foreach (var entry in mechanismEntries)
            {
                writer.WriteStartObject();
                writer.WriteString("name", entry.Name);
                writer.WriteString("geometryFolder", entry.GeometryFolder);
                if (entry.Mount is { } mount)
                {
                    writer.WriteStartObject("mountedOn");
                    writer.WriteString("mechanism", mount.ParentMechanismName);
                    writer.WriteNumber("linkIndex", mount.ParentLinkIndex);
                    writer.WriteStartArray("localPosition");
                    writer.WriteRawValue(mount.Px.ToString("F6", inv), skipInputValidation: true);
                    writer.WriteRawValue(mount.Py.ToString("F6", inv), skipInputValidation: true);
                    writer.WriteRawValue(mount.Pz.ToString("F6", inv), skipInputValidation: true);
                    writer.WriteEndArray();
                    writer.WriteStartArray("localRotation");
                    writer.WriteRawValue(mount.Qx.ToString("F6", inv), skipInputValidation: true);
                    writer.WriteRawValue(mount.Qy.ToString("F6", inv), skipInputValidation: true);
                    writer.WriteRawValue(mount.Qz.ToString("F6", inv), skipInputValidation: true);
                    writer.WriteRawValue(mount.Qw.ToString("F6", inv), skipInputValidation: true);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        Log($"ExportPackage: wrote package to '{packageDir}' (timeline included: {timelineIncluded}).");
    }

    private readonly record struct MountInfo(
        string ParentMechanismName, int ParentLinkIndex,
        double Px, double Py, double Pz, double Qx, double Qy, double Qz, double Qw);

    /// <summary>
    /// Detects mechanisms mounted on another mechanism's flange -- e.g. a gripper mounted on the
    /// robot's tool0 flange, physically moving with the arm even though it's its own top-level
    /// Mechanism with no joints of its own connecting it to the arm. RobotStudio has no
    /// attach/re-parent *event* (see TimelineRecorder's static-component polling), but the
    /// attachment itself is queryable directly: AttachmentHelper.GetAttachedMechanism(mechanism)
    /// walks the mechanism's flanges and returns whatever Mechanism is attached to the flange's
    /// link, via the station's real Attachments list (AttachmentHelper.GetAttachedChildren) --
    /// not a heuristic. GetFlanges() throws for MechanismType.Tool (a gripper has no flange of its
    /// own to mount things on), so that's skipped rather than caught as an error.
    /// </summary>
    private static Dictionary<Mechanism, MountInfo> FindMechanismMounts(List<Mechanism> mechanisms)
    {
        var mounts = new Dictionary<Mechanism, MountInfo>();

        foreach (var mechanism in mechanisms)
        {
            if (mechanism.MechanismType == MechanismType.Tool)
            {
                continue;
            }

            var flanges = mechanism.GetFlanges();
            if (flanges.Length == 0)
            {
                continue;
            }

            var attached = AttachmentHelper.GetAttachedMechanism(mechanism);
            if (attached is null)
            {
                continue;
            }

            var flangeLink = flanges[0].Link;
            var linkIndex = -1;
            var components = mechanism.GraphicComponents;
            for (var i = 0; i < components.Count; i++)
            {
                if (ReferenceEquals(components[i], flangeLink))
                {
                    linkIndex = i;
                    break;
                }
            }

            if (linkIndex < 0)
            {
                continue;
            }

            var relativeMatrix = attached.Transform.GetRelativeTransform(flangeLink);
            var (px, py, pz, qx, qy, qz, qw) = ConvertTransformToUnity(relativeMatrix);
            mounts[attached] = new MountInfo(mechanism.DisplayName, linkIndex, px, py, pz, qx, qy, qz, qw);
        }

        return mounts;
    }

    /// <summary>
    /// For every link (in GraphicComponents order, i.e. link index), resolves which link index is
    /// its parent per the mechanism's joint tree (-1 for the base link) and which joint index
    /// drives it (-1 for the base link, which no joint drives directly). Shared by the rest-pose
    /// geometry export and the per-sample link timeline export -- both need the same parent/joint
    /// relationship, just applied to a different Transform/Matrix4 snapshot.
    /// </summary>
    private static (int[] ParentLinkIndices, int[] JointIndices) GetParentLinkIndices(Mechanism mechanism)
    {
        var components = mechanism.GraphicComponents;
        var linkIndexByComponent = new Dictionary<ABB.Robotics.RobotStudio.Stations.GraphicComponent, int>();
        for (var i = 0; i < components.Count; i++)
        {
            linkIndexByComponent[components[i]] = i;
        }

        var parentLinkIndices = new int[components.Count];
        var jointIndices = new int[components.Count];
        for (var i = 0; i < components.Count; i++)
        {
            parentLinkIndices[i] = -1;
            jointIndices[i] = -1;
            if (mechanism.GetParentJoint(components[i], out var jointIndex))
            {
                jointIndices[i] = jointIndex;
                if (mechanism.GetParentLink(jointIndex, out var parentLinkComponent)
                    && linkIndexByComponent.TryGetValue(parentLinkComponent, out var resolvedIndex))
                {
                    parentLinkIndices[i] = resolvedIndex;
                }
            }
        }

        return (parentLinkIndices, jointIndices);
    }

    /// <summary>
    /// Exports a non-mechanism station component (a fixture, table, or wood stick with no joints)
    /// as a single-link manifest.json -- same shape ExportMechanismGeometry writes for a one-link
    /// mechanism, so RobotPackageLoader on the Unity side needs no separate code path. The link's
    /// local transform is identity since there's no parent link to be relative to; Unity positions
    /// the whole component via the station's own placement, not per-part offsets here.
    /// </summary>
    private static void ExportStaticComponentGeometry(ABB.Robotics.RobotStudio.Stations.GraphicComponent component, string dir)
    {
        Directory.CreateDirectory(dir);

        var files = new List<string>();
        foreach (var part in FindParts(component))
        {
            var fileName = $"link0_{SanitizeFileName(part.DisplayName)}.obj";
            var path = Path.Combine(dir, fileName);
            try
            {
                part.SaveAs(path);
                files.Add(fileName);
            }
            catch (Exception ex)
            {
                Log($"ExportGeometry: failed to export part '{part.DisplayName}' of component '{component.DisplayName}': {ex.Message}");
            }
        }

        using (var stream = File.Create(Path.Combine(dir, "manifest.json")))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("mechanism", component.DisplayName);
            writer.WriteStartArray("links");
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);
            writer.WriteString("name", component.DisplayName);
            writer.WriteNumber("parentJoint", -1);
            writer.WriteNumber("parentLink", -1);
            writer.WriteStartArray("localPosition");
            writer.WriteNumberValue(0.0);
            writer.WriteNumberValue(0.0);
            writer.WriteNumberValue(0.0);
            writer.WriteEndArray();
            writer.WriteStartArray("localRotation");
            writer.WriteNumberValue(0.0);
            writer.WriteNumberValue(0.0);
            writer.WriteNumberValue(0.0);
            writer.WriteNumberValue(1.0);
            writer.WriteEndArray();
            writer.WriteStartArray("files");
            foreach (var f in files)
            {
                writer.WriteStringValue(f);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Log($"ExportGeometry: exported static component '{component.DisplayName}' to '{dir}'.");
    }

    private static void ExportMechanismGeometry(Mechanism mechanism, string dir)
    {
        Directory.CreateDirectory(dir);

        var components = mechanism.GraphicComponents;
        var (parentLinkIndices, jointIndices) = GetParentLinkIndices(mechanism);

        var linkEntries = new List<LinkEntry>();
        var linkIndex = 0;

        // Cast to IEnumerable<GraphicComponent> explicitly: RobotStudio 2025's
        // GraphicComponentCollection declares a public non-generic GetEnumerator() directly
        // (foreach binds to that over the interface's, per the C# spec), while 2026's only
        // implements IEnumerable<GraphicComponent>.GetEnumerator() explicitly -- this cast
        // gives `object` vs. `GraphicComponent` foreach elements depending on host version
        // unless forced. Both SDKs implement the generic interface, so this works on either.
        foreach (var link in (IEnumerable<ABB.Robotics.RobotStudio.Stations.GraphicComponent>)components)
        {
            var jointIndex = jointIndices[linkIndex];
            var hasParentJoint = jointIndex >= 0;
            var parentLinkIndex = parentLinkIndices[linkIndex];
            var parentLinkComponent = parentLinkIndex >= 0 ? components[parentLinkIndex] : null;

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

            // Local position/rotation relative to parentLink (or to the mechanism root when
            // parentLink is -1), already converted to Unity's Y-up/left-handed convention --
            // same (x, z, -y) axis remap RobotStudio's own OBJ exporter uses for vertices, see
            // ConvertTransformToUnity. Same raw units as the OBJ vertices (unverified which --
            // whatever it is, it's consistent between the two, since neither path rescales).
            // Fixed-point (not "R"/general format) so this never emits scientific notation --
            // JSON allows it, but keeping it out avoids relying on the Unity-side JSON parser
            // handling it correctly.
            linkEntries.Add(new LinkEntry(
                linkIndex, link.DisplayName, hasParentJoint ? jointIndex : -1, parentLinkIndex,
                px, py, pz, qx, qy, qz, qw, files));

            linkIndex++;
        }

        using (var stream = File.Create(Path.Combine(dir, "manifest.json")))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            var inv = CultureInfo.InvariantCulture;
            writer.WriteStartObject();
            writer.WriteString("mechanism", mechanism.DisplayName);
            writer.WriteStartArray("links");
            foreach (var entry in linkEntries)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", entry.Index);
                writer.WriteString("name", entry.Name);
                writer.WriteNumber("parentJoint", entry.ParentJoint);
                writer.WriteNumber("parentLink", entry.ParentLink);
                writer.WriteStartArray("localPosition");
                writer.WriteRawValue(entry.Px.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(entry.Py.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(entry.Pz.ToString("F6", inv), skipInputValidation: true);
                writer.WriteEndArray();
                writer.WriteStartArray("localRotation");
                writer.WriteRawValue(entry.Qx.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(entry.Qy.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(entry.Qz.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(entry.Qw.ToString("F6", inv), skipInputValidation: true);
                writer.WriteEndArray();
                writer.WriteStartArray("files");
                foreach (var f in entry.Files)
                {
                    writer.WriteStringValue(f);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Log($"ExportGeometry: exported {linkIndex} link(s) for mechanism '{mechanism.DisplayName}' to '{dir}'.");
    }

    /// <summary>
    /// Reads the raw joint-value samples recorded by TimelineRecorder and, for each one, resolves
    /// every link's Unity-space local position/rotation via Mechanism.GetJointTransform -- exact
    /// forward kinematics from RobotStudio itself, not a re-implementation -- so the Unity side
    /// only has to interpolate between keyframes, the same way it already applies manifest.json's
    /// rest-pose localPosition/localRotation. GetJointTransform doesn't mutate the live station (it
    /// takes jointValues as a parameter), so this is safe to run after recording without disturbing
    /// whatever pose the station is currently showing.
    /// </summary>
    private static void ExportLinkTimeline(
        List<Mechanism> mechanisms,
        List<ABB.Robotics.RobotStudio.Stations.GraphicComponent> staticComponents,
        string rawTimelinePath, string outPath)
    {
        var mechanismsByName = mechanisms.ToDictionary(m => m.DisplayName);
        var staticNames = staticComponents.Select(c => c.DisplayName).ToHashSet();
        var linkInfoByMechanism = new Dictionary<string, (int[] ParentLinkIndices, int[] JointIndices)>();

        using var outWriter = new StreamWriter(outPath, append: false, Encoding.UTF8);
        using var lineBuffer = new MemoryStream();
        var writer = new Utf8JsonWriter(lineBuffer);
        var inv = CultureInfo.InvariantCulture;

        foreach (var line in File.ReadLines(rawTimelinePath))
        {
            if (line.Length == 0)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var recordType = root.GetProperty("type").GetString();

            if (recordType == "staticTransform")
            {
                var componentName = root.GetProperty("component").GetString();
                if (componentName is null || !staticNames.Contains(componentName))
                {
                    continue;
                }

                var pos = root.GetProperty("position").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                var rot = root.GetProperty("rotation").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                // Recorded in RobotStudio space (position xyz / rotation wxyz) -- reconstruct a
                // Matrix4 via the same Matrix3(rotation) + Vector3(translation) constructor the
                // joint path already uses (Matrix4(Matrix3, Vector3), proven at line ~644 below),
                // so this goes through the identical Unity-space conversion (ConvertTransformToUnity)
                // instead of a second hand-rolled remap. Built from the quaternion via the standard
                // quaternion-to-rotation-matrix formula, since no Matrix4(Quaternion, Vector3)
                // overload is confirmed to exist on this SDK type.
                var rsMatrix = new Matrix4(QuaternionToMatrix3(rot[0], rot[1], rot[2], rot[3]), new Vector3(pos[0], pos[1], pos[2]));
                var (px, py, pz, qx, qy, qz, qw) = ConvertTransformToUnity(rsMatrix);

                lineBuffer.SetLength(0);
                writer.Reset(lineBuffer);
                writer.WriteStartObject();
                writer.WriteString("type", "link");
                writer.WritePropertyName("t");
                writer.WriteRawValue(root.GetProperty("t").GetRawText(), skipInputValidation: true);
                writer.WriteString("mechanism", componentName);
                writer.WriteStartArray("links");
                writer.WriteStartObject();
                writer.WriteNumber("index", 0);
                writer.WriteStartArray("localPosition");
                writer.WriteRawValue(px.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(py.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(pz.ToString("F6", inv), skipInputValidation: true);
                writer.WriteEndArray();
                writer.WriteStartArray("localRotation");
                writer.WriteRawValue(qx.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(qy.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(qz.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(qw.ToString("F6", inv), skipInputValidation: true);
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
                outWriter.WriteLine(Encoding.UTF8.GetString(lineBuffer.GetBuffer(), 0, (int)lineBuffer.Length));
                continue;
            }

            if (recordType != "joint")
            {
                continue;
            }

            var mechanismName = root.GetProperty("mechanism").GetString();
            if (mechanismName is null || !mechanismsByName.TryGetValue(mechanismName, out var mechanism))
            {
                continue;
            }

            var jointValues = root.GetProperty("values").EnumerateArray().Select(v => v.GetDouble()).ToArray();

            if (!linkInfoByMechanism.TryGetValue(mechanismName, out var linkInfo))
            {
                linkInfo = GetParentLinkIndices(mechanism);
                linkInfoByMechanism[mechanismName] = linkInfo;
            }
            var (parentLinkIndices, jointIndices) = linkInfo;

            var components = mechanism.GraphicComponents;
            var linkMatrices = new Matrix4[components.Count];
            var allResolved = true;
            for (var i = 0; i < components.Count; i++)
            {
                // GetJointTransform takes a *joint* index, not a link index -- the base link
                // (jointIndices[i] == -1, nothing drives it directly) keeps its fixed rest-pose
                // matrix instead, since it never moves as other joints are driven.
                if (jointIndices[i] < 0)
                {
                    linkMatrices[i] = components[i].Transform.Matrix;
                }
                else if (!mechanism.GetJointTransform(jointIndices[i], jointValues, out var rawMatrix))
                {
                    allResolved = false;
                    break;
                }
                else
                {
                    // GetJointTransform returns the solver's raw output, not the final placement --
                    // RobotStudio itself multiplies by the link's CorrectionTransform before storing
                    // it as Transform.Matrix (see IMechanismLink.SetLinkTransform, found by
                    // decompiling: "_transformMat = mat * _correctionTransform"). Skipping this was
                    // the original bug: links looked plausible individually but didn't line up with
                    // each other, since only the base link (read straight from Transform.Matrix)
                    // had the correction baked in and the rest didn't.
                    var correction = ((IMechanismLink)components[i]).CorrectionTransform;
                    linkMatrices[i] = rawMatrix.Multiply(correction);
                }
            }

            if (!allResolved)
            {
                Log($"ExportLinkTimeline: failed to resolve link transforms for mechanism '{mechanismName}' at t={root.GetProperty("t").GetRawText()}, skipping sample.");
                continue;
            }

            lineBuffer.SetLength(0);
            writer.Reset(lineBuffer);
            writer.WriteStartObject();
            writer.WriteString("type", "link");
            writer.WritePropertyName("t");
            writer.WriteRawValue(root.GetProperty("t").GetRawText(), skipInputValidation: true);
            writer.WriteString("mechanism", mechanismName);
            writer.WriteStartArray("links");
            for (var i = 0; i < components.Count; i++)
            {
                var relativeMatrix = parentLinkIndices[i] >= 0
                    ? RelativeTransform(linkMatrices[i], linkMatrices[parentLinkIndices[i]])
                    : linkMatrices[i];
                var (px, py, pz, qx, qy, qz, qw) = ConvertTransformToUnity(relativeMatrix);

                writer.WriteStartObject();
                writer.WriteNumber("index", i);
                writer.WriteStartArray("localPosition");
                writer.WriteRawValue(px.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(py.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(pz.ToString("F6", inv), skipInputValidation: true);
                writer.WriteEndArray();
                writer.WriteStartArray("localRotation");
                writer.WriteRawValue(qx.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(qy.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(qz.ToString("F6", inv), skipInputValidation: true);
                writer.WriteRawValue(qw.ToString("F6", inv), skipInputValidation: true);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            outWriter.WriteLine(Encoding.UTF8.GetString(lineBuffer.GetBuffer(), 0, (int)lineBuffer.Length));
        }

        Log($"ExportLinkTimeline: wrote '{outPath}'.");
    }

    /// <summary>
    /// child's transform relative to parent, both already in the same space (mechanism-local
    /// Matrix, not GlobalMatrix) -- same formula Transform.GetRelativeTransform uses internally
    /// (Inverse(parent) * child), reimplemented here because GetJointTransform returns raw Matrix4
    /// values with no Transform object to call GetRelativeTransform on.
    /// </summary>
    private static Matrix4 RelativeTransform(Matrix4 child, Matrix4 parent)
    {
        var parentInverse = parent;
        parentInverse.InvertRigid();
        return parentInverse.Multiply(child);
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
    /// Standard scalar-first (w, x, y, z) quaternion-to-rotation-matrix formula, row-major to match
    /// Matrix3's constructor order (same order ConvertTransformToUnity's own Matrix3 literals use
    /// above). Needed because TimelineRecorder's polled static-component samples are recorded as a
    /// quaternion (component.Transform.Matrix.Quaternion), not a Matrix3/Matrix4, and no
    /// Matrix4(Quaternion, Vector3) overload is confirmed to exist on this SDK type.
    /// </summary>
    private static Matrix3 QuaternionToMatrix3(double w, double x, double y, double z)
    {
        return new Matrix3(
            1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
            2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
            2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y));
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

    private readonly record struct LinkEntry(
        int Index, string Name, int ParentJoint, int ParentLink,
        double Px, double Py, double Pz, double Qx, double Qy, double Qz, double Qw,
        List<string> Files);

    private static void Log(string message)
    {
        // No console when hosted inside RobotStudio's process, so write to a plain log file
        // instead -- this is how we'll confirm the add-in actually ran.
        File.AppendAllText(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
    }
}
