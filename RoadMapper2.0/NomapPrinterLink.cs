using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace RoadMapper
{
    // Everything RoadMapper needs to know about NomapPrinter. Read-only: we never touch
    // NomapPrinter's config, we only look at it so we can match its geometry and warn
    // the admin when the settings won't let our layer through.
    //
    // Source of truth for all of this is shudnal/NomapPrinter (checked against commit b17a0ce):
    //   - custom layers are found anywhere under BepInEx/config/shudnal.NomapPrinter/ at startup,
    //     but its FileSystemWatcher matches live changes on eargs.Name, which for a file in a
    //     subfolder is normally the relative path ("Sub\\file.png") and won't match. So we write
    //     to the TOP level of that folder to be sure live updates get picked up.
    //   - file name: <MapType>.<WorldName or WorldUID>.underfog.png
    //   - the server only syncs the file whose MapType matches the server's own "Map type".
    internal static class NomapPrinterLink
    {
        public const string PluginGuid = "shudnal.NomapPrinter";

        // Every MapType NomapPrinter can draw with custom layers (Vanilla doesn't use them).
        // We write the same PNG under all of them, so a map type change on the server just works.
        public static readonly string[] LayerMapTypes = { "BirdsEye", "Topographical", "Chart", "OldChart" };

        public static string ConfigDirectory => Path.Combine(Paths.ConfigPath, PluginGuid);

        // layer: "underfog" (ordinary marks) or "overfog" (admin marks).
        public static string LayerFileName(string mapType, string worldName, string layer = "underfog") => $"{mapType}.{worldName}.{layer}.png";

        private static ConfigFile Config =>
            Chainloader.PluginInfos.TryGetValue(PluginGuid, out PluginInfo info) && info.Instance != null
                ? info.Instance.Config
                : null;

        public static bool IsLoaded => Config != null;

        private static object Get(string section, string key)
        {
            ConfigFile cfg = Config;
            if (cfg == null) return null;
            ConfigDefinition def = new ConfigDefinition(section, key);
            return cfg.ContainsKey(def) ? cfg[def].BoxedValue : null;
        }

        public static float MapSizeMultiplier =>
            Get("Map style extended", "Map size multiplier") is float f && f > 0f ? f : 1f;

        public static string MapType => Get("Map style", "Map type")?.ToString() ?? "unknown";

        // ---------------------------------------------------------------------
        // Reflection into NomapPrinter internals, for "refresh on map table read".
        // Both are optional: if shudnal renames them, the feature switches itself off
        // with a warning and everything else keeps working.
        //   NomapPrinter.MapMaker.GenerateMap()                - starts a map print
        //   NomapPrinter.NomapPrinter.customLayerUnderfog.Value - the synced underfog PNG (base64)
        // ---------------------------------------------------------------------

        public static MethodInfo GenerateMapMethod =>
            AccessTools.Method("NomapPrinter.MapMaker:GenerateMap", new Type[0]);

        private static object _underfogSyncedValue;
        private static PropertyInfo _underfogValueProperty;
        private static bool _underfogLookupDone;

        private static object _overfogSyncedValue;
        private static PropertyInfo _overfogValueProperty;
        private static bool _overfogLookupDone;

        // Same for the overfog layer (NomapPrinter.customLayerOverfog). Null if unavailable.
        public static string CurrentOverfogLayer
        {
            get
            {
                if (!_overfogLookupDone)
                {
                    _overfogLookupDone = true;
                    FieldInfo field = AccessTools.Field(AccessTools.TypeByName("NomapPrinter.NomapPrinter"), "customLayerOverfog");
                    _overfogSyncedValue = field?.GetValue(null);
                    _overfogValueProperty = _overfogSyncedValue?.GetType().GetProperty("Value");
                }
                return _overfogValueProperty?.GetValue(_overfogSyncedValue) as string;
            }
        }

        // Current underfog layer as NomapPrinter sees it on this machine. Null if unavailable.
        // A newly synced layer is a new string object, so callers compare by reference.
        public static string CurrentUnderfogLayer
        {
            get
            {
                if (!_underfogLookupDone)
                {
                    _underfogLookupDone = true;
                    FieldInfo field = AccessTools.Field(AccessTools.TypeByName("NomapPrinter.NomapPrinter"), "customLayerUnderfog");
                    _underfogSyncedValue = field?.GetValue(null);
                    _underfogValueProperty = _underfogSyncedValue?.GetType().GetProperty("Value");
                }
                return _underfogValueProperty?.GetValue(_underfogSyncedValue) as string;
            }
        }

        public static bool CanReadUnderfogLayer
        {
            get
            {
                _ = CurrentUnderfogLayer;
                return _underfogValueProperty != null;
            }
        }

        public static bool GenerateMap()
        {
            MethodInfo method = GenerateMapMethod;
            if (method == null) return false;
            method.Invoke(null, null);
            return true;
        }

        // Returns human-readable problems with NomapPrinter's settings; empty if all good.
        // hasOverfogPoints: only warn about the over-fog settings when there are admin marks to show.
        public static List<string> CheckSettings(bool hasOverfogPoints = false)
        {
            List<string> problems = new List<string>();

            if (!IsLoaded)
            {
                problems.Add("NomapPrinter is not loaded. RoadMapper's map layer does nothing without it.");
                return problems;
            }

            if (!(Get("Map custom layers", "Under fog - Enable layer") is bool enabled && enabled))
                problems.Add("NomapPrinter: [Map custom layers] 'Under fog - Enable layer' is off. Roads will not be drawn on the map.");

            if (!(Get("Map custom layers", "Under fog - Share from server") is bool share && share))
                problems.Add("NomapPrinter: [Map custom layers] 'Under fog - Share from server' is off. Clients will not receive the road layer.");

            if (hasOverfogPoints)
            {
                if (!(Get("Map custom layers", "Over fog - Enable layer") is bool overEnabled && overEnabled))
                    problems.Add("NomapPrinter: [Map custom layers] 'Over fog - Enable layer' is off. Admin marks will not be drawn on the map.");
                if (!(Get("Map custom layers", "Over fog - Share from server") is bool overShare && overShare))
                    problems.Add("NomapPrinter: [Map custom layers] 'Over fog - Share from server' is off. Clients will not receive the admin layer.");
            }

            if (MapType == "Vanilla")
                problems.Add("NomapPrinter: [Map style] 'Map type' is Vanilla, which ignores custom layers. Use BirdsEye, Topographical, Chart or OldChart.");

            return problems;
        }
    }
}
