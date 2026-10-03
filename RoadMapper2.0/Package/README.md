# RoadMapper

Mark roads, walls, fences and places while you walk, and have them drawn onto [NomapPrinter](https://thunderstore.io/c/valheim/p/shudnal/NomapPrinter/)'s printed map. It's made for nomap servers: no more logging out to hand-draw roads in Paint.

## Features

- **The Surveyor**: its own tool (crafted at the workbench: 5 Wood, 2 Stone), with two build tabs. It never touches the terrain, costs no stamina and doesn't wear out.
- **Roads tab**: Path, Road, Wall and Fence markers, plus an Eraser. Each strike is recorded by the server and drawn into NomapPrinter's under-fog map layer, so roads appear on the map as you explore.
- **Map Markers tab**: 20 icons (House, Camp, Farm, Town, Castle, Tower, Lighthouse, Trader, Entrance, Mine, Lumber, Bridge, Harbour, Longship, pins and more), stamped onto the printed map.
- **See your work**: while you hold the Surveyor, every recorded road point near you shows as a small wisp torch tinted to its brush colour, and every map marker as a banner on a pole. Only you see them, and they vanish when you put the tool away.
- **Compass friendly**: map markers are also added as (unsaved) vanilla map pins, so compass mods that read map pins show them.
- **Death pins clean themselves up**: when your gravestone is gone (you emptied it, or someone else looted it), its death pin is removed the next time you're near it. Handy in nomap, where vanilla death pins are hard to remove and compass mods show every one.
- **Admin marks for everyone**: admins get extra pieces (gold-framed icons, at the end of each tab): admin versions of every road brush and marker, plus an Admin Mark Eraser. These are drawn on NomapPrinter's *over*-fog layer, so every player sees them, explored or not: handy for marking the main settlement and highways for new players. Everyone holding the Surveyor sees admin marks (a trio of splayed wisps around an iron torch, and gold banners) so it's clear they're the server's. The ordinary eraser can't remove them, and the server refuses admin marks and erases from anyone not on its admin list.
- **Server-driven**: the map layer is drawn by the server and synced to everyone by NomapPrinter. Reading a map table gets you the latest roads straight away.

## Requirements

- [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/), [Jotunn](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/), [NomapPrinter](https://thunderstore.io/c/valheim/p/shudnal/NomapPrinter/)
- Install on the **server and every client**, all on the same version (enforced).
- The world must be in nomap mode (dedicated server: `-setkey nomap`).
- NomapPrinter settings: `[Map custom layers] Under fog - Enable layer` and `Under fog - Share from server` on (and `Over fog - Enable layer` and `Over fog - Share from server` for admin marks; both are off by default); `[Map style] Map type` anything except Vanilla. RoadMapper warns in the log if any of these are wrong.

## Configuration

`BepInEx/config/com.jotunn.RoadMapper.cfg`. Sections marked *(server)* are admin-only and synced from the server.

- **Brush1 to Brush4** *(server)*: map line width (metres) and colour for Path, Road, Wall and Fence.
- **Eraser** *(server)*: eraser radius.
- **DeDuplicate** *(server)*: points closer than this (same brush) are merged on world save.
- **Map Layer** *(server)*: how often the map layer is rewritten (strikes per write, timer, refresh on map table read, and its cooldown).
- **Flags**: show flags, max flags shown, and the radius they're shown in.
- **Pins**: show map markers as map pins, and remove death pins once their gravestone is gone (on by default).

## Custom map markers

Markers are 16x16 (up to 64x64) PNGs named `<id>_<name>.png`, ids from 50 up, e.g. `100_Portal.png`. The bundled set lives in the mod's `Markers` folder. Extra or replacement markers go in `BepInEx/config/RoadMapper/Markers/` (a file there with a bundled id replaces that marker). One PNG pixel is one map pixel (6 m at the default map size). Only the id is stored for a placed marker, so don't renumber markers once they're in use. Custom markers must be copied to the server and every client.

## Credits

- Map marker icons: [1-bit Pixel Icons](https://nikoichu.itch.io/pixel-icons) by **Nikoichu** (CC0 1.0). Thank you!
- The banner-image technique (rebuilding the cloth's UVs so a custom image covers the whole banner) comes from my BannerShare mod.

## Changelog

### 2.2.0
- Admin marks: admin-only road brushes, markers and eraser that draw on NomapPrinter's over-fog layer, so everyone sees them on the map, explored or not. Shown in the world as a trio of wisps around an iron torch, and gold banners. Stored separately in `BepInEx/config/RoadMapper/<World>.overfog.txt`; the server checks every admin mark against its admin list. Needs NomapPrinter's `Over fog - Enable layer` and `Over fog - Share from server` (both off by default).
- Death pins are removed once their gravestone is gone (config: Pins / Remove death pins).
- Map marker set reworked: added Entrance, Pin, Small Pin, Mine, Lumber, Bridge, Harbour and Longship; removed Hut, Observatory, Tavern, Shop and Treasure. Markers already placed with a removed icon are no longer drawn on the map.
- The Surveyor held in the hand no longer comes apart: the banner, pole and wisp stay together while you walk.
- Marker banners now hang flush against their pole.

### 2.1.0
- New tool, the Surveyor, with Roads and Map Markers tabs. The marker pieces no longer live on the hoe and no longer flatten the ground.
- Map markers: 17 icons drawn onto the printed map, shown in the world as banners, and added as map pins for compass mods.
- Road flags: tinted wisp torches on recorded road points while you hold the Surveyor, restored from the server.
- Quieter logs: per-strike lines are now Debug only.

### 2.0.0
- Roads are drawn server-side into NomapPrinter's under-fog layer.
