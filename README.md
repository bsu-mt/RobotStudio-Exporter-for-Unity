# RobotStudio Unity Bridge

A RobotStudio add-in that, in one export action, packages a robot arm assembly simulation — per-link geometry, joint motion timeline, and I/O signal timeline — into a Unity-importable format for interactive playback on Meta Quest 2.

This is the export side of a two-part pipeline:

- **This project (`RobotStudio-Exporter-for-Unity`)** — a RobotStudio add-in. This is the actual product being built; the goal is for it to work with *any* RobotStudio station, not just one hardcoded demo. Designed for same-machine use first, but the export package format should not assume the importing machine is the same one that produced it (so it can move to a different machine — shared folder, USB, cloud drive, etc. — later without a redesign).
- **[Unity-Importer-from-RobotStudio](https://github.com/bsu-mt/Unity-Importer-from-RobotStudio)** — a Unity project that imports the exported package and plays it back interactively on Quest 2 (play/pause/step, live signal inspection). It doesn't talk to RobotStudio directly; it only knows about the export package format this project defines. Right now it's a test harness for validating this add-in's output, not a polished end-user app.

If you're picking this project back up in a new session: the goal right now is **research and prototyping**, not a finished tool. See "What to do in this session" below.

## Design (locked in)

**One RobotStudio add-in, one export action, one package.** No cloud service, no separate transfer/storage component — a dedicated storage/transfer layer is more infrastructure than a single export package needs. If cross-machine handoff is ever required, the package can simply be dropped in a synced folder (Dropbox/OneDrive) or copied by hand; the export format itself doesn't need to know how it got moved.

Manual export was considered and rejected for two of the three data types: RobotStudio has manual STEP export for geometry, but the **joint motion timeline** and **signal event timeline** don't exist as files anywhere in RobotStudio — they're transient state that only exists while a simulation is running. There's no menu item to produce them; capturing them means hooking into the simulation programmatically (via the `SimulationController`/`Mechanism` and `IOSystem` signal-changed events in the API) while it plays and logging every timestep. That's what makes an add-in — not a manual export step — necessary.

On export, the add-in:

1. **Enumerates the mechanism's links** and exports each link's geometry individually (not a single fused mesh), preserving the ability to reconstruct the kinematic hierarchy on the Unity side.
2. **Converts geometry to a Unity-friendly format.** RobotStudio's own export formats are things like STEP/VRML/STL/IGES — not FBX/glTF. So this step is really two chained stages under the hood (RobotStudio-side export via the API, then an automated conversion pass — most likely invoking FreeCAD headlessly, matching the STEP → FreeCAD → FBX/glTF pipeline already used for the demo robot's geometry) — but still a single action from the user's perspective.
3. **Captures a joint-hierarchy manifest**: parent-child structure, per-joint local offset transforms, joint axis, and limits — enough for the Unity side to reassemble the per-link meshes into a correctly-jointed rig without hand-authoring it.
4. **Runs the simulation and records the joint motion timeline and signal event timeline** as it plays. This step takes roughly as long as the simulation itself — it is not instantaneous like the geometry export.
5. **Packages everything together** (link meshes + hierarchy manifest + motion timeline + signal timeline) into one export package that the Unity-side importer reads as a unit.

## Open Design Decisions

Nothing below is decided yet — these are the questions the next session should work through, ideally by reading RobotStudio API docs and experimenting, not by guessing.

### 1. Exact packaging/file format

What the export package actually looks like on disk — a folder with a defined layout, a single zip, or something else — and the exact schema for the joint-hierarchy manifest, joint motion timeline, and signal event timeline, designed to match what the Unity-side importer expects. Open questions carried over from earlier discussion:

- Sampling rate/density needed for the joint timeline (currently only start/end per segment is available from the demo station; a denser timeline will eventually come from a MoveIt integration upstream — the schema should accommodate that without rework)
- RobotStudio's exact coordinate/axis convention (WorkObject orientation) for a station, so the Unity-side coordinate transform is derived precisely rather than guessed by trial and error

### 2. Geometry conversion mechanics

Whether the FreeCAD conversion step is invoked automatically by the add-in as a subprocess (requires FreeCAD installed and scriptable headlessly — needs verifying), or is a separate manual/semi-automated step for now while the pipeline is being proven out.

## What to Do in This Session

Roughly in this order:

1. **Read the RobotStudio API documentation.** RobotStudio 2026 is installed locally (see "Local Environment" below), but the in-app Help is user-facing documentation, not the developer/API reference — that lives on ABB's Developer Center (search for "RobotStudio SDK" / "RobotStudio API"). Look specifically for: how add-ins are packaged and loaded, the `Station` / `Mechanism` / `SimulationController` object model, how to enumerate/read I/O signals and their values over time, and what geometry export formats/APIs are actually available per-link (not just whole-station).
2. **Figure out which `ABB.Robotics.*` DLL set an add-in actually needs to reference** — see the note on `Bin` vs `Bin-net48` below, since this determines the target framework for `src/RobotStudioUnityBridge/RobotStudioUnityBridge.csproj` (currently a `netstandard2.0` placeholder with no real code).
3. **Prototype small**: get a minimal add-in that can read the demo station (see "Reference Station Data" below) and print out something simple — e.g. list of mechanisms and their joint values — before attempting any real export logic.
4. **Verify FreeCAD can be driven headlessly** from the add-in (or decide to defer that and do geometry conversion as a manual step for now).
5. Only after the above works, start building the real export logic: per-link geometry export, hierarchy manifest, and simulation recording.

## Local Environment

- **RobotStudio 2026** is installed at `C:\Program Files (x86)\ABB\RobotStudio 2026`.
- It ships two separate sets of `ABB.Robotics.*.dll` assemblies:
  - `Bin\` (modern) — includes `ABB.Robotics.RobotStudio.Stations.dll` and `ABB.Robotics.RobotStudio.Stations.Forms.dll`, which the `Bin-net48\` set does **not** have. This looks like it may be the set needed for Station-object-model access, i.e. what an add-in would use to read station contents — but this is an inference from filenames, not confirmed against documentation yet.
  - `Bin-net48\` (legacy, .NET Framework 4.8) — includes ScreenMaker/FlexPendant SDK assemblies (`ABB.Robotics.ScreenMaker.*`) alongside a smaller RobotStudio set. Looks oriented toward FlexPendant screen development rather than RobotStudio add-ins, but again, unconfirmed.
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
