using System;
using System.IO;
using System.Linq;
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

    private static void Log(string message)
    {
        // No console when hosted inside RobotStudio's process, so write to a plain log file
        // instead -- this is how we'll confirm the add-in actually ran.
        File.AppendAllText(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
    }
}
