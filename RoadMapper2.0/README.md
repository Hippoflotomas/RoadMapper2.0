# RoadMapper

Mark roads, walls, fences and places while you walk, and have them drawn onto [NomapPrinter](https://thunderstore.io/c/valheim/p/shudnal/NomapPrinter/)'s printed map. It's made for nomap servers: no more logging out to hand-draw roads in Paint.

## Features

- **The Surveyor**: its own tool (crafted at the workbench: 5 Wood, 2 Stone), with two build tabs. It never touches the terrain, costs no stamina and doesn't wear out.
- **Roads tab**: Path, Road, Wall and Fence markers, plus an Eraser. Each strike is recorded by the server and drawn into NomapPrinter's under-fog map layer, so roads appear on the map as you explore.
- **Map Markers tab**: 17 icons (House, Camp, Farm, Town, Castle, Tower, Lighthouse, Tavern, Trader, Signpost, Treasure and more), stamped onto the printed map.
- **See your work**: while you hold the Surveyor, every recorded road point near you shows as a small wisp torch tinted to its brush colour, and every map marker as a banner on a pole. Only you see them, and they vanish when you put the tool away.
- **Compass friendly**: map markers are also added as (unsaved) vanilla map pins, so compass mods that read map pins show them.
- **Server-driven**: the map layer is drawn by the server and synced to everyone by NomapPrinter. Reading a map table gets you the latest roads straight away.

## Requirements

- [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/), [Jotunn](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/), [NomapPrinter](https://thunderstore.io/c/valheim/p/shudnal/NomapPrinter/)
- Install on the **server and every client**, all on the same version (enforced).
- The world must be in nomap mode (dedicated server: `-setkey nomap`).
- NomapPrinter settings: `[Map custom layers] Under fog - Enable layer` and `Under fog - Share from server` on; `[Map style] Map type` anything except Vanilla. RoadMapper warns in the log if any of these are wrong.

## Configuration

`BepInEx/config/com.jotunn.RoadMapper.cfg`. Sections marked *(server)* are admin-only and synced from the server.

- **Brush1 to Brush4** *(server)*: map line width (metres) and colour for Path, Road, Wall and Fence.
- **Eraser** *(server)*: eraser radius.
- **DeDuplicate** *(server)*: points closer than this (same brush) are merged on world save.
- **Map Layer** *(server)*: how often the map layer is rewritten (strikes per write, timer, refresh on map table read, and its cooldown).
- **Flags**: show flags, max flags shown, and the radius they're shown in.
- **Pins**: show map markers as map pins.

## Custom map markers

Markers are 16x16 (up to 64x64) PNGs named `<id>_<name>.png`, ids from 50 up, e.g. `67_Portal.png`. The bundled set lives in the mod's `Markers` folder. Extra or replacement markers go in `BepInEx/config/RoadMapper/Markers/` (a file there with a bundled id replaces that marker). One PNG pixel is one map pixel (6 m at the default map size). Only the id is stored for a placed marker, so don't renumber markers once they're in use. Custom markers must be copied to the server and every client.

## Changelog

### 2.1.0
- New tool, the Surveyor, with Roads and Map Markers tabs. The marker pieces no longer live on the hoe and no longer flatten the ground.
- Map markers: 17 icons drawn onto the printed map, shown in the world as banners, and added as map pins for compass mods.
- Road flags: tinted wisp torches on recorded road points while you hold the Surveyor, restored from the server.
- Quieter logs: per-strike lines are now Debug only.

### 2.0.0
- Roads are drawn server-side into NomapPrinter's under-fog layer.
