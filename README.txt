LOITERING MUNITION DRONES - RimWorld mod (1.6)
=========================================

INSTALL
1. Build the DLL once (see BUILD below). It lands in Assemblies/Shahed136Drones.dll.
2. Copy this whole folder into  RimWorld/Mods/
3. Enable "Shahed-136 Drones" in the mod list (below Core / DLCs).

BUILD
Needs the .NET SDK (https://dotnet.microsoft.com/download). Then:
    cd Source/Shahed136Drones
    dotnet build -c Release
The Krafs.Rimworld.Ref NuGet package supplies the game references automatically,
so you don't need to point at your RimWorld install.

HOW IT WORKS IN GAME
1. Research tab "Drones":
     - Loitering munitions (needs Microelectronics)  -> unlocks the launch rail + drone
     - Long-range navigation                         -> range 20 -> 45 world tiles
2. Build the "drone launch rail" (Security tab, 3x3).
3. Open its Bills tab, add "assemble Shahed-136" (60 steel, 2 components, 25 chemfuel,
   Crafting 6). Each finished bill mounts a drone on the rail (max 3).
4. Click "Launch Shahed-136", pick a tile on the world map inside the red ring:
     - Tile with an open map (your other colony, a raid/quest site you're on):
       you jump to that map and choose the exact cell, building or pawn.
       Moving targets (pawns) are tracked until impact.
     - Faction settlement (no map open): confirmation, then an abstract strike
       with a letter and a goodwill penalty.
5. The drone flies for (distance x 150) ticks, then dives in and explodes
   (radius 4.9, 70 bomb damage).

TUNING (no code needed)
Defs/ThingDefs_DroneLauncher.xml -> capacity, range, extendedRange, ticksPerTile,
settlementGoodwillImpact, explosionRadius, damage, armorPenetration.
Defs/RecipeAndWorkDefs_Shahed.xml -> drone cost and work amount.

RECON DRONE (research "reconnaissance drones")
- Launch rail bills: "assemble recon drone" (new one) or "mount recon drone" (takes a landed
  one from storage). The rail holds either one Shahed or one recon drone.
- "Launch recon drone": pick a tile in range, then a colonist. The colonist walks to the rail,
  launches it and stays there as operator.
- Over a tile with an open map the drone flies in and circles. Select it: "Fly to" (needs the
  operator), "Autopilot", "Go back". It reveals fog below it.
- While it circles over a tile, that tile counts as recon for Shahed-136 strikes.
- "Go back": it flies home and lands next to the rail as an item. Haul it, mount it again.
- Fuel lasts 1 day (reconFuelTicks). Out of fuel = crash. You get a low-fuel warning.
- If the operator leaves the rail, the drone switches to autopilot. "Assign operator" fixes it.

EXTRAS
- Launch animation: the drone slides off the rail and climbs away with a smoke trail.
- World map: a Shahed icon flies from the launcher to the target (click it for time to impact).
- Sounds: Sounds/Shahed/Shahed_Launch.ogg and Shahed_Incoming.ogg. Replace them with
  your own files (same names) to change the sounds; no rebuild needed.
  Note: the engine sound is cut from a YouTube recording - fine for personal use,
  check its license before uploading the mod anywhere.

DEV MODE: god mode adds a "DEV: Load drone" button on the rail for quick testing.
