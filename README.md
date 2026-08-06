# RobotStudio Unity Bridge

A tool that transfers a robot arm assembly simulation authored in ABB RobotStudio — geometry, motion path, and I/O signal timeline — into a format a Unity application can consume for interactive playback on Meta Quest 2.

This is the export/transfer side of a two-part pipeline:

- **This project** — reads a RobotStudio station and produces Unity-consumable data. This is the actual product being built; the goal is for it to work with *any* RobotStudio station, not just one hardcoded demo.
- **[RobotStudio_Unity_Quest](https://github.com/bsu-mt/RobotStudio_Unity_Quest)** — a Unity application that consumes that data and plays it back interactively on Quest 2 (play/pause/step, live signal inspection). It doesn't talk to RobotStudio directly; it only knows about the exported data format this project defines.

If you're picking this project back up in a new session: the goal right now is **research and prototyping**, not a finished tool. See "What to do in this session" below.

## Goal

Given a RobotStudio station, produce:

- Per-link robot geometry in a Unity-importable format (e.g. FBX/glTF)
- A joint motion timeline (joint angles over time, or per-segment waypoints)
- A signal event timeline (I/O signal name, value, and timestamp)

so that any RobotStudio project can be pointed at Quest 2 playback without hand-authoring Unity-side data.

## Open Design Decisions

Nothing below is decided yet — these are the questions the next session should work through, ideally by reading RobotStudio API docs and experimenting, not by guessing.

### 1. Delivery mechanism: Add-in vs. standalone application

- **RobotStudio Add-in**: runs in-process inside RobotStudio, using the RobotStudio API to read the currently open station directly (live access to the `Station` object model, robot mechanisms, signals, etc.). Likely the more natural fit for automating "export the station I have open right now."
- **Standalone application**: runs outside RobotStudio, opens a saved station file (or a Pack & Go `.rspag`) independently. Would need to determine whether the RobotStudio API supports headless/out-of-process station loading, or whether this route effectively requires driving RobotStudio via its API from an external process (e.g. through `ABB.Robotics.RobotStudio.Controllers`).

This decision should come from reading the actual API capabilities, not be assumed.

### 2. Export data format

The exact schema for the joint timeline and signal event data needs to be designed to match what the Unity-side ingestion code expects (see `joints.csv` / `events.csv` references in the RobotStudio_Unity_Quest project). Open questions carried over from that project:

- Sampling rate/density needed for the joint timeline once full-path export (not just start/end per segment) is possible
- What data format a future MoveIt integration on the motion-planning side would output, so the ingestion interface accommodates it without rework
- RobotStudio's exact coordinate/axis convention (WorkObject orientation) for a station, so the Unity-side coordinate transform is derived precisely rather than guessed by trial and error

## What to Do in This Session

Roughly in this order:

1. **Read the RobotStudio API documentation.** RobotStudio 2026 is installed locally (see "Local Environment" below), but the in-app Help is user-facing documentation, not the developer/API reference — that lives on ABB's Developer Center (search for "RobotStudio SDK" / "RobotStudio API"). Look specifically for: how Add-ins are packaged and loaded, the `Station` / `Mechanism` / `SimulationController` object model, and how to enumerate/read I/O signals and their values over time.
2. **Figure out which `ABB.Robotics.*` DLL set an Add-in actually needs to reference** — see the note on `Bin` vs `Bin-net48` below, since this determines the target framework for `src/RobotStudioUnityBridge/RobotStudioUnityBridge.csproj` (currently a `netstandard2.0` placeholder with no real code).
3. **Decide Add-in vs. standalone** based on what's actually feasible with the API, not preference.
4. **Prototype small**: get a minimal Add-in (or standalone app) that can open/read the demo station (see "Reference Station Data" below) and print out something simple — e.g. list of mechanisms and their joint values — before attempting any real export logic.
5. Only after that works, start designing the actual export format and geometry conversion step.

## Local Environment

- **RobotStudio 2026** is installed at `C:\Program Files (x86)\ABB\RobotStudio 2026`.
- It ships two separate sets of `ABB.Robotics.*.dll` assemblies:
  - `Bin\` (modern) — includes `ABB.Robotics.RobotStudio.Stations.dll` and `ABB.Robotics.RobotStudio.Stations.Forms.dll`, which the `Bin-net48\` set does **not** have. This looks like it may be the set needed for Station-object-model access, i.e. what an Add-in would use to read station contents — but this is an inference from filenames, not confirmed against documentation yet.
  - `Bin-net48\` (legacy, .NET Framework 4.8) — includes ScreenMaker/FlexPendant SDK assemblies (`ABB.Robotics.ScreenMaker.*`) alongside a smaller RobotStudio set. Looks oriented toward FlexPendant screen development rather than RobotStudio Add-ins, but again, unconfirmed.
  - Don't assume either of the above — verify against the actual API docs before committing to a target framework.
- Reference/demo RobotStudio content is at `D:\Projects\Models\RobotStudio\`:
  - `DemoProject.rspag` — a Pack & Go of the demo station
  - `Demo\Stations\`, `Demo\Libraries\`, `Demo\Virtual Controllers\`, `Demo\Backups\` — the unpacked station content
  - `IRB4600_40kg-255_IRC5_rev05_STEP_j\` — per-link STEP CAD files for the IRB4600 (the placeholder robot model currently used; real target hardware is an IRB5720)

## Status

Not started — skeleton only, no RobotStudio API integration code yet.

## Project Layout

```
src/RobotStudioUnityBridge/   .NET class library skeleton (currently netstandard2.0 placeholder;
                               target framework will be finalized once the delivery mechanism is chosen)
```
