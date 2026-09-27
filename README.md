# WallXrefSync for AutoCAD 2020

AutoCAD .NET plug-in source for synchronizing local wall boundaries and solid black hatches from an external DWG.

## Commands

- `WALLSETUP`: select the A drawing's external reference, select a rectangular area in B, and create the first set of local boundaries and hatches.
- `WALLSYNC`: reread the saved source DWG and update B. Existing output is matched by source entity handle; unchanged outlines stay in place, same-vertex-count edits update the local polyline, and topology changes replace only that outline and its hatch.
- `WALLRESET`: after confirmation, remove plug-in generated geometry and its saved configuration.

The selected region and external-reference handle are stored in B's Named Objects Dictionary. Generated boundaries use the non-plotting layer `G-墙_同步边界`; solid hatches use `G-墙_黑色填充` and are moved behind model-space objects.

## Build and load

This project targets .NET Framework 4.7 and references the AutoCAD 2020 managed assemblies under `C:\Program Files\Autodesk\AutoCAD 2020`. If AutoCAD is installed elsewhere, set the `AutoCADManagedDir` MSBuild property to that directory. Build for x64, then load `bin\Debug\net47\WallXrefSync.dll` with `NETLOAD`. Add the DLL's folder to `TRUSTEDPATHS` when `SECURELOAD` requires a trusted location.

## Supported drawing geometry

- Reads direct entities in A's model space. It processes closed `Polyline` entities (lightweight polylines) on the exact layer `G-墙`.
- Range filtering uses each transformed polyline's extents; outlines that touch or cross the selected rectangle are included whole.
- Each outline receives its own solid hatch. Nested loops or holes need additional loop grouping to represent voids.
- Source DWG must be saved before syncing. The plug-in reads its saved file from disk; reload B's XREF separately if its displayed reference also needs refreshing.

AutoCAD 2020 and its managed assemblies are not installed in the authoring environment, so this source has not been compiled or loaded in AutoCAD here. Confirm it on a copy of your project drawings before production use, especially with your XREF insertion transform and wall outline types.
