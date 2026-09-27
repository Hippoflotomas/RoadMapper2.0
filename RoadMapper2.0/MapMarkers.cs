using BepInEx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace RoadMapper
{
    // One map marker icon, loaded from BepInEx/config/RoadMapper/Markers/<id>_<name>.png.
    internal sealed class MarkerIcon
    {
        public int Id;
        public string Name;       // "Camp Site" from 52_camp_site.png
        public string FileName;
        public int Width;
        public int Height;
        public byte[] Rgba;       // Width*Height*4, TOP row first (PNG order, north up on the map)
        public Sprite Sprite;     // build menu icon

        public string PrefabName => $"RoadMapper_Marker_{Id}";
    }

    // The marker set. Scanned once at startup, then read-only, so the background layer render
    // can read it without locking.
    //
    // The ID comes from the filename, not from folder order, so adding or renaming files never
    // changes what an already-placed marker shows. The map is drawn by the server from ITS copy of
    // this folder; each client builds its Map Markers tab from ITS copy. Ship the same folder to
    // everyone. A marker the server doesn't have is simply left off the map.
    internal static class MapMarkers
    {
        public const int FirstMarkerId = 50;

        // Icons are stamped at their own size (one PNG pixel = one map pixel, 6 m at default map
        // size). This stops one oversized PNG painting over half a biome.
        public const int MaxIconSize = 64;

        // The set that ships with the mod: <plugin folder>/Markers, next to the DLL.
        public static string BundledFolder =>
            Path.Combine(Path.GetDirectoryName(typeof(MapMarkers).Assembly.Location) ?? "", "Markers");

        // Optional extras / replacements: BepInEx/config/RoadMapper/Markers. A file here with the same
        // id as a bundled one replaces it; new ids add to the set.
        public static string Folder => Path.Combine(Paths.ConfigPath, "RoadMapper", "Markers");

        private static readonly Dictionary<int, MarkerIcon> _byId = new Dictionary<int, MarkerIcon>();
        private static readonly Dictionary<string, int> _idByPrefab = new Dictionary<string, int>();

        public static IReadOnlyDictionary<int, MarkerIcon> ById => _byId;

        public static bool IsMarker(int brushId) => _byId.ContainsKey(brushId);

        public static bool TryGetIdForPrefab(string prefabName, out int id) => _idByPrefab.TryGetValue(prefabName, out id);

        private static readonly Regex FileNamePattern = new Regex(@"^(\d+)_(.+)\.png$", RegexOptions.IgnoreCase);

        // Main thread only (Texture2D). Safe to call more than once; later calls rescan from scratch.
        // Every client and the server must end up with the same set: the mod's version check covers
        // the bundled set; anything added in the config folder has to be copied to everyone by hand.
        public static void Scan()
        {
            Dictionary<int, MarkerIcon> found = new Dictionary<int, MarkerIcon>();

            int bundled = ScanFolder(BundledFolder, found, replaces: false);
            Directory.CreateDirectory(Folder);
            int extra = ScanFolder(Folder, found, replaces: true);

            // Store in id order: the build menu follows it.
            _byId.Clear();
            _idByPrefab.Clear();
            List<int> ids = new List<int>(found.Keys);
            ids.Sort();
            foreach (int id in ids)
            {
                _byId[id] = found[id];
                _idByPrefab[found[id].PrefabName] = id;
            }

            Jotunn.Logger.LogInfo($"[RoadMapper] {_byId.Count} map marker(s): {bundled} bundled ({BundledFolder}), {extra} from {Folder}");
        }

        private static int ScanFolder(string folder, Dictionary<int, MarkerIcon> found, bool replaces)
        {
            if (!Directory.Exists(folder))
                return 0;

            string[] files = Directory.GetFiles(folder, "*.png");
            // Numeric order ("104" would otherwise sort before "51"); only matters for which of two
            // same-id files in one folder wins.
            Array.Sort(files, (a, b) =>
            {
                int ia = LeadingNumber(Path.GetFileName(a)), ib = LeadingNumber(Path.GetFileName(b));
                return ia != ib ? ia.CompareTo(ib) : StringComparer.OrdinalIgnoreCase.Compare(a, b);
            });

            HashSet<int> seenHere = new HashSet<int>();
            int loaded = 0;
            foreach (string path in files)
            {
                string fileName = Path.GetFileName(path);
                Match match = FileNamePattern.Match(fileName);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                {
                    Jotunn.Logger.LogWarning($"[RoadMapper] Marker '{fileName}' skipped: name it <id>_<name>.png, e.g. {FirstMarkerId}_portal.png.");
                    continue;
                }
                if (id < FirstMarkerId)
                {
                    Jotunn.Logger.LogWarning($"[RoadMapper] Marker '{fileName}' skipped: marker ids start at {FirstMarkerId} (lower ids are road brushes).");
                    continue;
                }
                if (!seenHere.Add(id))
                {
                    Jotunn.Logger.LogWarning($"[RoadMapper] Marker '{fileName}' skipped: id {id} is used twice in {folder}.");
                    continue;
                }
                if (found.TryGetValue(id, out MarkerIcon existing) && !replaces)
                    continue; // can't happen for the first folder; kept for safety

                MarkerIcon icon = Load(path, fileName, id, match.Groups[2].Value);
                if (icon == null) continue;

                if (existing != null)
                    Jotunn.Logger.LogInfo($"[RoadMapper] Marker {id}: '{fileName}' replaces bundled '{existing.FileName}'.");
                found[id] = icon;
                loaded++;
            }
            return loaded;
        }

        private static MarkerIcon Load(string path, string fileName, int id, string rawName)
        {
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                // Jotunn's wrapper rather than Unity's LoadImage: Unity's overloads include a
                // ReadOnlySpan<byte> one that won't resolve against .NET Framework 4.8 (CS0518).
                if (!Jotunn.Utils.AssetUtils.LoadImage(tex, File.ReadAllBytes(path)))
                {
                    Jotunn.Logger.LogWarning($"[RoadMapper] Marker '{fileName}' skipped: couldn't decode the PNG.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Jotunn.Logger.LogWarning($"[RoadMapper] Marker '{fileName}' skipped: {ex.GetType().Name}: {ex.Message}");
                return null;
            }

            int w = tex.width, h = tex.height;
            if (w > MaxIconSize || h > MaxIconSize)
            {
                Jotunn.Logger.LogWarning($"[RoadMapper] Marker '{fileName}' skipped: {w}x{h} is bigger than the {MaxIconSize}x{MaxIconSize} limit.");
                return null;
            }

            // Unity's pixel rows run bottom-up; the map layer's run top-down. Flip while copying.
            Color32[] pixels = tex.GetPixels32();
            byte[] rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                int srcRow = (h - 1 - y) * w;
                for (int x = 0; x < w; x++)
                {
                    Color32 c = pixels[srcRow + x];
                    int i = (y * w + x) * 4;
                    rgba[i] = c.r;
                    rgba[i + 1] = c.g;
                    rgba[i + 2] = c.b;
                    rgba[i + 3] = c.a;
                }
            }

            // Pixel art: keep it crisp when the build menu scales it up.
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
            // Unique names: mods that cache map pin icons (NomapPrinter does) key them by sprite name.
            sprite.name = $"RoadMapper_{id}";
            tex.name = sprite.name;

            return new MarkerIcon
            {
                Id = id,
                Name = PrettyName(rawName),
                FileName = fileName,
                Width = w,
                Height = h,
                Rgba = rgba,
                Sprite = sprite
            };
        }

        private static int LeadingNumber(string fileName)
        {
            int n = 0, i = 0;
            while (i < fileName.Length && char.IsDigit(fileName[i]) && n < 100000000)
                n = n * 10 + (fileName[i++] - '0');
            return i == 0 ? int.MaxValue : n; // no number: sorts last (and gets skipped by the scan anyway)
        }

        // "camp_site" -> "Camp Site"
        private static string PrettyName(string raw)
        {
            string spaced = raw.Replace('_', ' ').Replace('-', ' ').Trim();
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
        }
    }
}
