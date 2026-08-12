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
2. **Converts geometry to a Unity-friendly format.** RobotStudio's own export formats are things like STEP/VRML/STL/IGES — not directly usable by Unity. So this step is really two chained stages under the hood (RobotStudio-side export via the API, then an automated conversion pass — most likely invoking FreeCAD headlessly, matching the STEP → FreeCAD → OBJ+MTL pipeline already used for the demo robot's geometry) — but still a single action from the user's perspective. **OBJ+MTL, not FBX/glTF**: FBX is ruled out because Unity's FBX import is Editor-only (Autodesk FBX SDK) and can't be loaded at runtime in a built Quest app, which is a hard requirement here since the importer loads export packages dynamically after the app ships. glTF is a reasonable alternative (Unity's glTFast supports runtime loading with proper PBR materials) but OBJ+MTL is simpler to generate deterministically from FreeCAD and simpler to parse at runtime, and since per-link hierarchy/animation are carried by our own manifest/timeline (not the mesh file's embedded scene graph or skeletal animation), OBJ+MTL's lack of those features costs nothing. Worth revisiting glTF later if OBJ/MTL's flat (non-PBR) materials turn out to look too plain.
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

### 3. Format to watch: USD

[OpenUSD](https://openusd.org/) is increasingly the standard for robot digital twins (NVIDIA Isaac Sim, ROS 2 tooling), and outclasses OBJ/glTF on hierarchy, variants, and material fidelity. Not adopted now — Unity's USD support (`com.unity.formats.usd`) is still primarily Editor-side, not mature enough for runtime loading on Quest — but worth re-evaluating if/when Unity's runtime USD tooling catches up, or if this pipeline ever needs to interoperate with ROS 2/Isaac Sim.

## What to Do in This Session

1. ~~Read the RobotStudio API documentation.~~ Skipped in favor of a faster path: reverse-engineering the real add-ins shipped with the local RobotStudio 2026 install (decompiling via reflection, since no internet SDK docs were consulted). See "Local Environment" below for what that turned up — it answered the `Bin` vs `Bin-net48` question and the add-in entrypoint contract directly from working examples.
2. ~~Figure out which `ABB.Robotics.*` DLL set an add-in needs.~~ **Resolved** — see below.
3. **Prototype small — done and verified in RobotStudio itself.** `Addin.AddinMain()` → `ExportPipeline.LogActiveStationSummary()` reads `Station.ActiveStation`, enumerates its `Mechanism`s, and logs each one's joint values to `%TEMP%\RobotStudioUnityBridge.log` (there's no console when hosted inside RobotStudio, hence the log file). `AddinMain()` only runs once, at load time, so it also subscribes to `Project.ActiveProjectChanged` to re-log when a station is opened afterward. Confirmed actually loading and running inside a real RobotStudio 2026 instance — see "Testing the Add-in" for how, and for two real gotchas hit along the way (`.rspak` packaging format, `MinimumHostVersion` version gate).
4. **Verify FreeCAD can be driven headlessly** — not started.
5. Real export logic (per-link geometry export, hierarchy manifest, simulation recording) — not started, blocked on step 3's in-app verification.

## Local Environment

- **RobotStudio 2026** is installed at `C:\Program Files (x86)\ABB\RobotStudio 2026`.
- **`Bin` vs `Bin-net48` — resolved.** `Bin\RobotStudio.exe` ships a `RobotStudio.runtimeconfig.json` targeting `net10.0` / `Microsoft.WindowsDesktop.App` — RobotStudio 2026's main process is a modern .NET (not .NET Framework) desktop app. `Bin-net48\RsAddinHost.exe` is a separate net48 host process, used only for legacy `AddInType=Internal` add-ins (e.g. the built-in `IOConfigurator`, `FleetManagement` — both ABB-internal, declared with an explicit `<Entrypoint>` in their `.rsaddin`). Real, general-purpose add-ins (e.g. the built-in `UsdConverter`, `AddInType=General`) live directly under `Bin\Addins\` and load in-process into the `net10.0` host. **This project targets `net10.0-windows` and references the `Bin\` assemblies** — confirmed working, `dotnet build` succeeds against them (see `src/RobotStudioUnityBridge/RobotStudioUnityBridge.csproj`).
- **Add-in manifest format and entrypoint convention — resolved by decompiling `UsdConverter` and `IOConfigurator`** (both shipped in `Bin\Addins\`): a `.rsaddin` XML file sits next to the assembly, declaring `AddInType` (`General` for a normal in-process add-in), `Assembly/FileName`, and optionally `Entrypoint`. When `Entrypoint` is omitted (as in `UsdConverter.rsaddin`), RobotStudio finds the entrypoint by convention: a type named `Addin` with a method `AddinMain()` — confirmed by reflecting over `UsdConverter.dll`, which has exactly `UsdConverter.Addin.AddinMain()` as a static method. This project's manifest (`RobotStudioUnityBridge.rsaddin`) and entrypoint (`Addin.cs`) follow that same convention.
- **Station object model, enough for the prototype and beyond:** `ABB.Robotics.RobotStudio.Stations.Station.ActiveStation` (static) → `.GraphicComponents` (`IEnumerable<GraphicComponent>`, `Mechanism : GraphicComponent`) → per-`Mechanism` `GetJointValues()`, `NumActiveJoints`, `GetJointTypes()`, `GetJointLimits()`, `GetParentLink()`, `GetJointTransform()`. Also found directly on `Station`: `.IOSignals` (`IOSignalCollection`) plus `IOSignalChanged`/`IOSignalValueChanged` events — meaning the I/O signal timeline can likely be captured straight from the `Station`, without going through a separate virtual-controller connection (`ABB.Robotics.Controllers.PC.dll`'s `IOSystemDomain.Signal`, which also exists but looks like the wrong layer for this). Have **not** yet found a `SimulationController` type under that exact name — worth another reflection pass (or real docs) once simulation recording is being built.
- Reference/demo RobotStudio content is at `D:\Projects\Models\RobotStudio\`:
  - `DemoProject.rspag` — a Pack & Go of the demo station
  - `Demo\Stations\`, `Demo\Libraries\`, `Demo\Virtual Controllers\`, `Demo\Backups\` — the unpacked station content
  - `IRB4600_40kg-255_IRC5_rev05_STEP_j\` — per-link STEP CAD files for the IRB4600 (the placeholder robot model currently used; real target hardware is an IRB5720)
- `dotnet --list-sdks` confirms the .NET 10 SDK (`10.0.302`) and `Microsoft.WindowsDesktop.App 10.0.10` runtime are installed locally, matching what the add-in needs to build/run against.

## Testing the Add-in

There's no "Add-in path" option in RobotStudio 2026's UI (an earlier guess in this doc was wrong). Installing is done via the **Add-Ins ribbon tab → Install** button, which only accepts `.rspak` (or `.rmf`) files, not a bare `.rsaddin`. Steps:

1. `.\pack.ps1` from the repo root — builds the add-in and produces `dist/RobotStudioUnityBridge-<version>.rspak`.
2. In RobotStudio: **Add-Ins tab → Install**, pick that `.rspak`.
3. Restart RobotStudio (autoload happens at startup), then open any station. Check `%TEMP%\RobotStudioUnityBridge.log` for the mechanism/joint-value dump.

Two real gotchas hit while getting this working (both reverse-engineered by decompiling `RobotStudio.dll`/`ABB.Robotics.RobotStudio.dll` with `ilspycmd`, since this is undocumented):

- **`.rspak` layout is strict.** The zip must contain a top-level `<Name>-<Version>/` folder (e.g. `RobotStudioUnityBridge-0.1.3/`) holding `manifest.xml` directly inside it, with the add-in itself under `<that folder>/RobotStudio/Add-In/`. A `manifest.xml` sitting at the zip root (no wrapping folder) fails validation with "is not a valid distribution package" — `RspakMetadata.ExtractMetadata` requires the `manifest.xml` entry's path to have exactly one path separator. `pack.ps1` reproduces this layout.
- **`MinimumHostVersion` (in the `.rsaddin`) and `MinClientVersion` (in `manifest.xml`) must both be ≥ `26.1`.** RobotStudio's `AddinManager.IsRuntimeCompatible` treats either of these fields, if present, as a shortcut signal for "modern .NET add-in, skip the reflection-based compat check" — but only if the value is at or above some internal "first .NET-hosted RobotStudio version" threshold. We initially used `26.0` (a guess) and got a hard "Not compatible with this version of RobotStudio" with no other explanation; `26.1` (matching the real `UsdConverter.rsaddin`) fixed it immediately. If this breaks again on a future RobotStudio version, bump both.

Also worth knowing: `AddinMain()` runs exactly once, when the add-in loads (which for a `Dependencies=Station` add-in happens the first time RobotStudio's station subsystem initializes — in practice, at startup, not tied to any specific station being open). It does **not** re-run for every station opened afterward; that's why `Addin.cs` separately subscribes to `Project.ActiveProjectChanged`.

## Status

Minimal add-in prototype written, builds clean, **and confirmed loading and running inside a real RobotStudio 2026 instance** — reads the active station's mechanisms and joint values, logs them to a file, re-runs when a new station is opened. No real export logic yet.

## Project Layout

```
pack.ps1                          builds + packages the add-in into dist/*.rspak (see "Testing the Add-in")
src/RobotStudioUnityBridge/
  RobotStudioUnityBridge.csproj   net10.0-windows, references Bin\ABB.Robotics.RobotStudio*.dll; <Version> here
                                   is pack.ps1's source of truth for the package version
  RobotStudioUnityBridge.rsaddin  add-in manifest (AddInType=General, copied next to the built .dll)
  Addin.cs                        RobotStudio-facing entrypoint (Addin.AddinMain())
  ExportPipeline.cs               the actual pipeline logic; currently just the read-only prototype
```
