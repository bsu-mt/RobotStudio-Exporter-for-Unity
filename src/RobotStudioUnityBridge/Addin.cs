using ABB.Robotics.RobotStudio;

namespace RobotStudioUnityBridge;

/// <summary>
/// RobotStudio add-in entrypoint. RobotStudio finds this by convention for a "General" AddInType
/// with no explicit &lt;Entrypoint&gt; in the .rsaddin manifest: a public static (or singleton
/// instance) method named AddinMain() on a type named Addin, confirmed by decompiling the
/// UsdConverter and IOConfigurator add-ins shipped with RobotStudio 2026.
/// </summary>
public static class Addin
{
    public static void AddinMain()
    {
        ExportPipeline.LogActiveStationSummary();

        // AddinMain runs once, at load time (see AddinManager.TryLoad in RobotStudio.dll) --
        // it does not re-run when a station is later opened. Project.ActiveProjectChanged is the
        // public event for that, so hook it to see the summary for stations opened after load.
        Project.ActiveProjectChanged += (sender, e) => ExportPipeline.LogActiveStationSummary();
    }
}
