using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Environment;

namespace RobotStudioUnityBridge;

/// <summary>
/// RobotStudio add-in entrypoint. RobotStudio finds this by convention for a "General" AddInType
/// with no explicit &lt;Entrypoint&gt; in the .rsaddin manifest: a public static (or singleton
/// instance) method named AddinMain() on a type named Addin, confirmed by decompiling the
/// UsdConverter and IOConfigurator add-ins shipped with RobotStudio 2026.
/// </summary>
public static class Addin
{
    private const string RecordButtonId = "RobotStudioUnityBridge.ToggleRecording";
    private const string ExportGeometryButtonId = "RobotStudioUnityBridge.ExportGeometry";

    public static void AddinMain()
    {
        ExportPipeline.LogActiveStationSummary();

        // AddinMain runs once, at load time (see AddinManager.TryLoad in RobotStudio.dll) --
        // it does not re-run when a station is later opened. Project.ActiveProjectChanged is the
        // public event for that, so hook it to see the summary for stations opened after load.
        Project.ActiveProjectChanged += (sender, e) => ExportPipeline.LogActiveStationSummary();

        RegisterRibbon();
    }

    private static void RegisterRibbon()
    {
        var tab = new RibbonTab("RobotStudioUnityBridge.Tab", "Unity Bridge");
        var group = new RibbonGroup("RobotStudioUnityBridge.Group", "Export");

        var recordButton = new CommandBarButton(
            RecordButtonId,
            "Record Motion + I/O",
            onUpdate: args =>
            {
                args.Enabled = true;
                args.Checked = TimelineRecorder.IsRecording;
                args.Caption = TimelineRecorder.IsRecording ? "Stop Recording" : "Record Motion + I/O";
            },
            onExecute: args =>
            {
                if (TimelineRecorder.IsRecording)
                {
                    TimelineRecorder.Stop();
                }
                else
                {
                    TimelineRecorder.Start();
                }
            })
        {
            DisplayAsCheckBox = true,
            DefaultEnabled = true,
        };

        var exportGeometryButton = new CommandBarButton(
            ExportGeometryButtonId,
            "Export Geometry",
            onUpdate: args =>
            {
                args.Enabled = true;
            },
            onExecute: args =>
            {
                ExportPipeline.ExportGeometry();
            })
        {
            DefaultEnabled = true,
        };

        group.Controls.Add(recordButton);
        group.Controls.Add(exportGeometryButton);
        tab.Groups.Add(group);
        UIEnvironment.RibbonTabs.Add(tab);
    }
}
