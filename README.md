# RobotStudio Unity Bridge

A tool that transfers a robot arm assembly simulation authored in ABB RobotStudio — geometry, motion path, and I/O signal timeline — into a format a Unity application can consume for interactive playback.

This is the export/transfer side of a two-part pipeline. The consumption/playback side (a Unity application targeting Meta Quest 2) lives in a separate project, [RobotStudio_Unity_Quest](https://github.com/bsu-mt/RobotStudio_Unity_Quest). That project should be able to ingest the output of any RobotStudio station processed by this tool, not just one hardcoded demo.

## Goal

Given a RobotStudio station, produce:

- Per-link robot geometry in a Unity-importable format (e.g. FBX/glTF)
- A joint motion timeline (joint angles over time, or per-segment waypoints)
- A signal event timeline (I/O signal name, value, and timestamp)

so that any RobotStudio project can be pointed at Quest 2 playback without hand-authoring Unity-side data.

## Status

Not yet started. Two open design decisions before implementation begins:

- **Delivery mechanism**: a RobotStudio Add-in (in-process, using the RobotStudio API) vs. a standalone application (reads a saved/Pack&Go station file). RobotStudio 2026 (installed at `C:\Program Files (x86)\ABB\RobotStudio 2026`) ships both a modern `Bin\ABB.Robotics.*.dll` set and a legacy `Bin-net48\ABB.Robotics.*.dll` set — which one applies depends on the delivery mechanism chosen.
- **Export data format**: exact schema for the joint timeline and signal event data, designed to match what the Unity-side ingestion code expects.

## Project Layout

```
src/RobotStudioUnityBridge/   .NET class library skeleton (currently netstandard2.0 placeholder;
                               target framework will be finalized once the delivery mechanism is chosen)
```
