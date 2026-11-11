# Third-party notices

NEBO is licensed under the GNU GPL version 3 with additional permissions for the
MS-PL libraries below and for DCS World (see [LICENSE](LICENSE) and
[LICENSE-EXCEPTION.md](LICENSE-EXCEPTION.md); versions up to 0.15.0 were MIT). It uses or works with the following,
each under its own terms.

## Libraries in the client

| Library | License |
|---|---|
| [SkiaSharp](https://github.com/mono/SkiaSharp) (Skia) | MIT (Skia: BSD-3-Clause) |
| [AvalonDock](https://github.com/Dirkster99/AvalonDock) | Microsoft Public License (MS-PL) |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) / SQLitePCLRaw | MIT / Apache-2.0; SQLite is public domain |
| .NET runtime and WPF | MIT |

The server DLL declares the Lua 5.1 C API itself (`server/dll/lua`) and links to the Lua that DCS
ships; Lua is MIT-licensed.

## Map data

- **Natural Earth** (`client/Nebo.Client/Assets/basemap.json`, the Scope map): public domain,
  [naturalearthdata.com](https://www.naturalearthdata.com).
- **Esri World Imagery** (the Satellite base map): loaded online from Esri under its terms of
  use; the attribution is shown on the map.
- **DCS World data**: the DCS Map, DCS Charts and DCS Satellite base maps and airfield details
  are read from the user's own DCS World installation at run time; so are the data cartridge
  tables (each aircraft's DTC defaults and definitions, made by DCS's own DTC code on the
  user's PC) and the unit types the server sends. The separate terrain
  download (`NEBO-Terrain-<version>.zip`) holds terrain height grids sampled from DCS World. DCS World, its terrains and their data belong to
  Eagle Dynamics SA and the terrain developers.

## Trademarks

DCS and DCS World are trademarks of Eagle Dynamics SA. SimpleRadio Standalone (SRS) and
Foothold are the work of their own authors. NEBO is an independent community project,
not affiliated with or endorsed by any of them.
