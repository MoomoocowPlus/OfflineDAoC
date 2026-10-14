# recast_cli — native Linux replacement for the tweaked RecastDemo.exe

`World/NavmeshMgr.cs RunRecast` launches `base/RecastDemo.exe <in.gset> <out.nav>`.
The original binary was a Windows x64 PE (repurposed RecastDemo), which cannot run on this
Linux box. This tool is a native (aarch64 ELF, name kept as `RecastDemo.exe` so `RunRecast`
finds it unchanged) re-implementation of exactly what that binary did in its headless mode.

## How the parameters were recovered

Ghidra 12.1.4 headless decompilation of the original PE (see `.scratch/buildnav.log`):

- CLI entry `FUN_140021d20` → factory `PTR_FUN_140070088` = `FUN_140021cf0`
  → constructs the 0x198-byte sample = **Sample_TileMesh** (ctor `FUN_1400167c0`,
  base ctor `FUN_14000fee0`).
- Headless runs have no GUI, so the ctor defaults are the build parameters:
  cellSize 0.1, cellHeight 0.1, agentHeight 1.8, agentRadius 0.3, agentMaxClimb 0.9,
  agentMaxSlope 80, regionMinSize 8, regionMergeSize 96, edgeMaxLen 12, edgeMaxError 1.3,
  vertsPerPoly 6, detailSampleDist 4, detailSampleMaxError 1, partition = watershed.
  Filters (low-hanging obstacles, ledge spans, walkable low-height spans) all enabled.
- `Sample_TileMesh` ctor sets tileSize = 96 (`handleMeshChanged` only overrides it when the
  gset carries an `s` settings line; BuildNav's `GeomSetWriter` never writes one).
- `buildTiledNavigation` (`FUN_1400179c0`) + per-tile builder (`FUN_140016c60`) give the
  derived config: walkableHeight = ceil(1.8/0.1) = 18, walkableClimb = floor(0.9/0.1) = 9,
  walkableRadius = ceil(0.3/0.1) = 3, borderSize = radius+3 = 6, maxEdgeLen = 12/0.1 = 120,
  minRegionArea = 8² = 64, mergeRegionArea = 96² = 9216, detailSampleDist = 0.4,
  detailSampleMaxError = 0.1, width = height = 96 + 2·6 = 108.
- `dtNavMeshParams` written to the .nav: orig = mesh bmin, cs = ch = tileSize·cellSize = 9.6,
  maxTiles = 65536, maxPolys = 1048576 ("handle large worlds" tweak).
- Off-mesh connection limit = 0x1000 = 4096 (`loadGeomSet` log message; matches
  `NavmeshMgr.RecastOffMeshConnectionLimit`).

## Fidelity

Built against recastnavigation **v1.6.0** Recast + this repo's OpenDAoC-Core Detour fork
(compiled with `DT_POLYREF64`, same 64-bit poly refs as the original exe and the server).
v1.6.0 was chosen because the original binary contains the era-typical
"Keep Itermediate Results" typo and uses `rcCreateChunkyTriMesh` (both gone in newer upstream).

Semantic comparison of a rebuilt shipped zone (`zone000`, 45796 tiles):

- .nav header, dtNavMeshParams, tile grid (214×214), mesh bbox, per-tile
  walkableHeight/Radius/Climb: **exact match**
- 44912 / 45796 tiles (98.1 %) have **byte-identical poly counts**
- total polys 67219 vs 70236 (−4.3 %): concentrated in ~900 tiles; the shipped mesh is a
  pass-2 output (ladder pads + 138 off-mesh connections appended) and was built from an
  earlier exporter's geometry, so a small drift is expected.
- Full pipeline (BuildNav export → pass 1 → Detour.Managed ladder placement → pass 2)
  verified end-to-end on zone013 on Linux, and the produced mesh loads on the server
  ("Loading NavMesh successful for zone 13").

## Build

    cd recast-cli/build && cmake .. -DCMAKE_BUILD_TYPE=Release && make -j
    # build/RecastDemo.exe is the tool; install to bin/<cfg>/net10.0/base/RecastDemo.exe

`third_party/` vendors recastnavigation v1.6.0 (Recast + ChunkyTriMesh) and the fork's
Detour; both are permissively licensed (zlib-style). Tile building is OpenMP-parallel
(deterministic output; verified identical to the serial build).
