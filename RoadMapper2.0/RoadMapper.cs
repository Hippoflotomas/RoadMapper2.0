using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Extensions;
using Jotunn.Managers;
using Jotunn.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace RoadMapper
{
    // RoadMapper 2.0
    // Ported from RoadMapper 1.x: the hoe marker tools, the eraser, the client->server RPCs
    // and the per-world point file. The old minimap overlay drawing and client resync are
    // NOT ported - in 2.0 the server renders the points into NomapPrinter's underfog layer.
    //
    // Layer pipeline (server only):
    //   strike/erase -> point file -> [trigger] -> render PNG off-thread -> write to
    //   BepInEx/config/shudnal.NomapPrinter/ -> NomapPrinter's file watcher picks it up and
    //   syncs it to every client -> it appears the next time someone reads a map table.
    // Triggers: every N strikes, a timer while there are unwritten changes, world save,
    // brush config change, server start, and a final flush when the world closes.
    //
    // Tools: since 2.1 the marker pieces live on their own tool (a hammer clone, for now) with its
    // own piece table and tabs, instead of being piggybacked onto the vanilla hoe.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [BepInDependency(NomapPrinterLink.PluginGuid)]
    // Server and every client need the same version: RPCs, pieces and the bundled markers must match.
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    internal class RoadMapper : BaseUnityPlugin
    {
        public const string PluginGUID = "com.jotunn.RoadMapper";
        public const string PluginName = "RoadMapper";
        public const string PluginVersion = "2.2.0";

        // Prefab names kept identical to 1.x so pieces already placed in a world still resolve.
        public const string PathMarkerPrefabName = "RoadMapper_PathMarker";
        public const string RoadMarkerPrefabName = "RoadMapper_RoadMarker";
        public const string WallMarkerPrefabName = "RoadMapper_WallMarker";
        public const string FenceMarkerPrefabName = "RoadMapper_FenceMarker";
        public const string EraserPrefabName = "RoadMapper_Eraser";

        // Admin pieces (overfog layer): same brushes and markers, prefab name with "Admin_" after
        // the "RoadMapper_" prefix, e.g. RoadMapper_Admin_PathMarker, RoadMapper_Admin_Marker_54.
        // They record into a separate point file that's drawn on NomapPrinter's OVER-fog layer,
        // so everyone sees them, explored or not. Hidden from non-admins; the server also refuses
        // admin strikes and erases from anyone who isn't on its admin list.
        public const string AdminPrefix = "RoadMapper_Admin_";
        public const string AdminEraserPrefabName = "RoadMapper_AdminEraser";
        // Added to an admin point's brush id when it's sent to clients (flags, pins), so the two
        // sets stay apart. Never stored: each file holds plain brush ids.
        internal const int AdminIdOffset = 100000;
        internal static bool IsAdminId(int id) => id >= AdminIdOffset;
        internal static int BaseId(int id) => id >= AdminIdOffset ? id - AdminIdOffset : id;

        // The dedicated roadmapping tool and its build menu.
        public const string ToolPrefabName = "RoadMapper_Tool";
        public const string PieceTableName = "_RoadMapperPieceTable";
        public const string RoadsCategory = "Roads";
        public const string MapMarkersCategory = "Map Markers";

        public struct MarkerPoint
        {
            public float x;
            public float z;
            public int BrushId;
        }

        // Use this class to add your own localization to the game
        // https://valheim-modding.github.io/Jotunn/tutorials/localization.html
        public static CustomLocalization Localization = LocalizationManager.Instance.GetLocalization();
        public static RoadMapper Instance;
        public static CustomRPC PathMarkerRPC;
        public static CustomRPC EraseRPC;
        public static CustomRPC LayerFlushRPC;
        public static CustomRPC FlagPointsRPC;
        public static CustomRPC MarkerPinsRPC;
        public static CustomRPC AdminMarkRPC;
        public static CustomRPC AdminEraseRPC;

        // ---------------------------------------------------------------------
        // Config
        // ---------------------------------------------------------------------

        // DeDup distance
        private ConfigEntry<float> _dedupDistance;

        // Brush 1 = Path Marker, 2 = Road Marker, 3 = Wall Marker, 4 = Fence Marker.
        // Width is in metres on the ground; the renderer converts to map pixels (6 m each at default size).
        private ConfigEntry<float> _brush1Width;
        private ConfigEntry<string> _brush1Colour;
        private ConfigEntry<float> _brush2Width;
        private ConfigEntry<string> _brush2Colour;
        private ConfigEntry<float> _brush3Width;
        private ConfigEntry<string> _brush3Colour;
        private ConfigEntry<float> _brush4Width;
        private ConfigEntry<string> _brush4Colour;

        // Eraser tool
        private ConfigEntry<float> _eraserSize;

        // Map layer
        private ConfigEntry<int> _strikesPerWrite;
        private ConfigEntry<float> _writeIntervalMinutes;
        private ConfigEntry<bool> _refreshOnTableRead;
        private ConfigEntry<float> _tableReadCooldownSeconds;

        // Ground flags (client-side, per player)
        private ConfigEntry<bool> _showFlags;
        private ConfigEntry<int> _maxFlags;
        private ConfigEntry<float> _flagRadius;

        // Marker pins (client-side, per player)
        private ConfigEntry<bool> _showMarkerPins;
        private ConfigEntry<bool> _removeDeathPins;

        private Harmony _harmony;

        private void Awake()
        {
            Jotunn.Logger.LogInfo("RoadMapper 2.0 has landed");
            Instance = this;

            BindConfig();
            MapMarkers.Scan();

            // Any change to how brushes look means the whole layer must be redrawn.
            EventHandler brushChanged = (_, __) =>
            {
                MarkLayerDirty("brush config changed", LayerKind.Both, writeNow: true);
                ClearFlagTemplates();
                if (_flagsActive && Player.m_localPlayer != null)
                {
                    ClearFlags();
                    RequestFlagPoints(Player.m_localPlayer.transform.position, Mathf.Clamp(_flagRadius.Value, 10f, MaxFlagRadius));
                }
            };
            foreach (ConfigEntry<float> width in new[] { _brush1Width, _brush2Width, _brush3Width, _brush4Width })
                width.SettingChanged += brushChanged;
            foreach (ConfigEntry<string> colour in new[] { _brush1Colour, _brush2Colour, _brush3Colour, _brush4Colour })
                colour.SettingChanged += brushChanged;

            PrefabManager.OnVanillaPrefabsAvailable += CreatePieces;
            ZNet.WorldSaveFinished += OnWorldSaveFinished;

            PathMarkerRPC = NetworkManager.Instance.AddRPC(
                "RoadMapper_PathMarkerRPC",
                ServerReceivePathMarker,
                ClientReceiveNothing);

            EraseRPC = NetworkManager.Instance.AddRPC(
                "RoadMapper_EraseRPC",
                ServerReceiveEraseRequest,
                ClientReceiveNothing);

            LayerFlushRPC = NetworkManager.Instance.AddRPC(
                "RoadMapper_LayerFlushRPC",
                ServerReceiveLayerFlushRequest,
                ClientReceiveNothing);

            // Client asks for the recorded points around it; server answers with them.
            FlagPointsRPC = NetworkManager.Instance.AddRPC(
                "RoadMapper_FlagPointsRPC",
                ServerReceiveFlagPointsRequest,
                ClientReceiveFlagPoints);

            // Client asks for every map marker on joining; server answers, and re-sends to
            // everyone whenever a marker is placed or erased.
            MarkerPinsRPC = NetworkManager.Instance.AddRPC(
                "RoadMapper_MarkerPinsRPC",
                ServerReceiveMarkerPinsRequest,
                ClientReceiveMarkerPins);

            // Admin (overfog) strikes and erases. Sent to the server only, which checks the sender.
            AdminMarkRPC = NetworkManager.Instance.AddRPC(
                "RoadMapper_AdminMarkRPC",
                ServerReceiveAdminMark,
                ClientReceiveNothing);
            AdminEraseRPC = NetworkManager.Instance.AddRPC(
                "RoadMapper_AdminEraseRPC",
                ServerReceiveAdminErase,
                ClientReceiveNothing);

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll();
            PatchNomapPrinterGenerateMap();
        }

        private void OnDestroy()
        {
            FlushLayerOnExit("plugin destroyed");
            ZNet.WorldSaveFinished -= OnWorldSaveFinished;
            _harmony?.UnpatchSelf();
        }

        private void OnApplicationQuit()
        {
            FlushLayerOnExit("application quit");
        }

        private void BindConfig()
        {
            _dedupDistance = Config.BindConfig("DeDuplicate", "Distance", 2f,
                "Minimum distance for two brushpoints to be apart before they are counted as duplicate",
                synced: true, configAttributes: AdminOnly());

            // "Width" replaces 1.x's "Size" key (which was in map pixels). Old "Size" lines left in
            // an existing config file are simply ignored.
            _brush1Width = Config.BindConfig("Brush1", "Width", 3f,
                "Width in metres of the Path Marker (brush 1) line on the map. One map pixel is 6 m.", synced: true, configAttributes: AdminOnly());
            _brush1Colour = Config.BindConfig("Brush1", "Colour", "#FFFFFF",
                "Map overlay colour for the Path Marker (brush 1), as #RRGGBB or #RRGGBBAA", synced: true, configAttributes: AdminOnly());

            _brush2Width = Config.BindConfig("Brush2", "Width", 5f,
                "Width in metres of the Road Marker (brush 2) line on the map. One map pixel is 6 m.", synced: true, configAttributes: AdminOnly());
            _brush2Colour = Config.BindConfig("Brush2", "Colour", "#F5C26B",
                "Map overlay colour for the Road Marker (brush 2), as #RRGGBB or #RRGGBBAA", synced: true, configAttributes: AdminOnly());

            _brush3Width = Config.BindConfig("Brush3", "Width", 4f,
                "Width in metres of the Wall Marker (brush 3) line on the map. One map pixel is 6 m.", synced: true, configAttributes: AdminOnly());
            _brush3Colour = Config.BindConfig("Brush3", "Colour", "#CBD6E2",
                "Map overlay colour for the Wall Marker (brush 3), as #RRGGBB or #RRGGBBAA", synced: true, configAttributes: AdminOnly());

            _brush4Width = Config.BindConfig("Brush4", "Width", 2f,
                "Width in metres of the Fence Marker (brush 4) line on the map. One map pixel is 6 m.", synced: true, configAttributes: AdminOnly());
            _brush4Colour = Config.BindConfig("Brush4", "Colour", "#8B4513",
                "Map overlay colour for the Fence Marker (brush 4), as #RRGGBB or #RRGGBBAA", synced: true, configAttributes: AdminOnly());

            _eraserSize = Config.BindConfig("Eraser", "Size", 2f,
                "Radius for the eraser tool", synced: true, configAttributes: AdminOnly());

            _strikesPerWrite = Config.BindConfig("Map Layer", "Strikes per write", 100,
                "Rewrite the NomapPrinter road layer after this many marker/eraser strikes. Each write re-syncs the layer to every client.",
                synced: true, configAttributes: AdminOnly());
            _writeIntervalMinutes = Config.BindConfig("Map Layer", "Write interval (minutes)", 5f,
                "If there are unwritten strikes, rewrite the layer after this many minutes even if 'Strikes per write' hasn't been reached. 0 disables.",
                synced: true, configAttributes: AdminOnly());
            _refreshOnTableRead = Config.BindConfig("Map Layer", "Refresh on map table read", true,
                "When a player reads a map table and the server has unwritten strikes, write the layer straight away and redraw that player's map once it arrives.",
                synced: true, configAttributes: AdminOnly());
            _tableReadCooldownSeconds = Config.BindConfig("Map Layer", "Table read cooldown (seconds)", 10f,
                "Minimum time between layer writes triggered by map table reads, so players repeatedly reading tables can't make the server rewrite constantly.",
                synced: true, configAttributes: AdminOnly());

            // Client-side, not synced: each player decides for themselves.
            _showFlags = Config.Bind("Flags", "Show flags", true,
                "While you hold the Surveyor, show a small flag, tinted to the brush colour, on every recorded road point around you. Flags are only visible to you and vanish when you put the Surveyor away.");
            _maxFlags = Config.Bind("Flags", "Max flags", 1000,
                "Most flags shown at once; the ones furthest from you are dropped first.");
            _showMarkerPins = Config.Bind("Pins", "Show marker pins", true,
                "Add every map marker as a vanilla map pin (never saved), so mods that read map pins, such as compasses, show them too.");
            _showMarkerPins.SettingChanged += (_, __) =>
            {
                if (_showMarkerPins.Value) _markerPinsRequested = false; // re-fetch on next Update
                else RemoveMarkerPins();
            };
            _removeDeathPins = Config.Bind("Pins", "Remove death pins", true,
                "Remove a death pin once its gravestone is gone: when you empty your grave, or when you pass the pin and its grave has been looted. Only pins within 20 m of you are checked, after their area has loaded.");
            _flagRadius = Config.Bind("Flags", "Radius", 100f,
                $"Show flags for recorded points within this many metres of you (the server caps it at {MaxFlagRadius:0} m).");
        }

        // A fresh attributes object per entry; ConfigurationManager tags each entry individually.
        private static ConfigurationManagerAttributes AdminOnly() => new ConfigurationManagerAttributes { IsAdminOnly = true };

        public (float width, string colour) GetBrushConfig(int brushId)
        {
            switch (brushId)
            {
                case 1: return (_brush1Width.Value, _brush1Colour.Value);
                case 2: return (_brush2Width.Value, _brush2Colour.Value);
                case 3: return (_brush3Width.Value, _brush3Colour.Value);
                case 4: return (_brush4Width.Value, _brush4Colour.Value);
                default: return (2f, "#FFFFFF");
            }
        }

        // ---------------------------------------------------------------------
        // NomapPrinter road layer (server side)
        // ---------------------------------------------------------------------

        // Captured on the main thread when the server's world is up. Cached so the final
        // flush can still run when ZNet is already gone (quit / back to menu).
        private bool _layerReady;
        private string _layerWorldName;
        private string _layerPointsPath;      // underfog: <World>.txt
        private string _overfogPointsPath;    // overfog (admin): <World>.overfog.txt
        private LayerRenderer.MapGeometry _layerGeometry;

        // Which layers have changes not yet written.
        private LayerKind _dirtyKinds;
        private bool _layerDirty => _dirtyKinds != LayerKind.None;
        private float _dirtySince;
        private int _strikesSinceWrite;
        private bool _writeRequested;
        private string _writeReason;

        // One render at a time; the exit flush waits on this if a background write is mid-flight.
        private readonly object _layerWriteLock = new object();
        private volatile bool _layerJobRunning;

        private void Update()
        {
            // Client side (every machine, including a listen/single-player host).
            if (_awaitingLayerUntil > 0f)
                CheckForRefreshedLayer();
            UpdateFlags();
            UpdateMarkerPins();
            DeathPins.Update(_removeDeathPins.Value);
            UpdateAdminPieces();

            bool serverWorldUp = IsServer && !string.IsNullOrEmpty(ZNet.instance.GetWorldName());

            if (!serverWorldUp)
            {
                // World closed without the app quitting (host went back to the main menu).
                if (_layerReady)
                {
                    FlushLayerOnExit("world closed");
                    _layerReady = false;
                }
                return;
            }

            if (!_layerReady)
                InitLayer();

            float interval = _writeIntervalMinutes.Value;
            if (_layerDirty && interval > 0f && Time.unscaledTime - _dirtySince >= interval * 60f)
                RequestLayerWrite($"{interval:0.#} min timer");

            if (_tableFlushPending && Time.unscaledTime - _lastTableFlushTime >= _tableReadCooldownSeconds.Value)
            {
                _tableFlushPending = false;
                if (_layerDirty)
                {
                    _lastTableFlushTime = Time.unscaledTime;
                    RequestLayerWrite("map table read, after cooldown");
                }
            }

            if (_writeRequested && !_layerJobRunning)
            {
                _writeRequested = false;
                StartBackgroundLayerWrite(_writeReason);
            }
        }

        private void InitLayer()
        {
            _layerWorldName = ZNet.instance.GetWorldName();
            _layerPointsPath = GetWorldDataFilePath();
            _overfogPointsPath = GetWorldDataFilePath(LayerKind.Overfog);
            _layerGeometry = LayerRenderer.MapGeometry.FromMultiplier(NomapPrinterLink.MapSizeMultiplier);
            _layerReady = true;

            Jotunn.Logger.LogInfo($"[RoadMapper] Road layer for world '{_layerWorldName}': " +
                $"{_layerGeometry.TextureSize}px, {_layerGeometry.PixelSize} m/px, NomapPrinter map type {NomapPrinterLink.MapType}. " +
                $"Writing to {NomapPrinterLink.ConfigDirectory}");

            bool hasOverfogPoints = LoadPointsFromFile(_overfogPointsPath).Count > 0;
            foreach (string problem in NomapPrinterLink.CheckSettings(hasOverfogPoints))
                Jotunn.Logger.LogWarning($"[RoadMapper] {problem}");

            // Always redraw once at startup so the layers match the current points, brushes and map size.
            MarkLayerDirty("server start", LayerKind.Both, writeNow: true);
        }

        // Called for every strike and every successful erase.
        private void OnLayerStrike(LayerKind kind)
        {
            _strikesSinceWrite++;
            int perWrite = Math.Max(1, _strikesPerWrite.Value);
            MarkLayerDirty($"{perWrite} strikes", kind, writeNow: _strikesSinceWrite >= perWrite);
        }

        private void MarkLayerDirty(string reason, LayerKind kind, bool writeNow)
        {
            if (!_layerDirty)
                _dirtySince = Time.unscaledTime;
            _dirtyKinds |= kind;
            if (writeNow)
                RequestLayerWrite(reason);
        }

        // Picked up by Update() on the main thread. Ignored until the server's world is up,
        // which also makes it a no-op on clients.
        private void RequestLayerWrite(string reason)
        {
            if (!_layerReady) return;
            _writeRequested = true;
            _writeReason = reason;
        }

        // ---------------------------------------------------------------------
        // Refresh on map table read
        //
        // NomapPrinter prints the map the instant a table is read, using whatever layer the
        // client already has, and never reprints when a new layer syncs in. So:
        //   1. client: NomapPrinter's GenerateMap() runs -> ask the server to flush, and
        //      remember which layer we printed with
        //   2. server: if there are unwritten strikes, write now (rate-limited)
        //   3. client: when a different layer arrives within the wait window, call
        //      GenerateMap() again. The base map is cached, so the reprint is quick.
        // If the server had nothing unwritten, no new layer comes and the wait just times out.
        // ---------------------------------------------------------------------

        private const float RefreshWaitSeconds = 15f;

        // Client state
        private float _awaitingLayerUntil;
        private string _layerAtPrint;
        private string _overfogAtPrint;
        private bool _reprinting;

        // Server state
        private float _lastTableFlushTime = float.NegativeInfinity;
        private bool _tableFlushPending;

        private void PatchNomapPrinterGenerateMap()
        {
            MethodInfo generateMap = NomapPrinterLink.GenerateMapMethod;
            if (generateMap == null)
            {
                Jotunn.Logger.LogWarning("[RoadMapper] Couldn't find NomapPrinter's MapMaker.GenerateMap(); 'Refresh on map table read' is disabled.");
                return;
            }
            _harmony.Patch(generateMap, postfix: new HarmonyMethod(typeof(RoadMapper), nameof(NomapPrinter_GenerateMap_Postfix)));
        }

        private static void NomapPrinter_GenerateMap_Postfix()
        {
            Instance?.OnMapPrintStarted();
        }

        private void OnMapPrintStarted()
        {
            if (_reprinting || !_refreshOnTableRead.Value || ZNet.instance == null)
                return;

            if (!NomapPrinterLink.CanReadUnderfogLayer)
            {
                Jotunn.Logger.LogWarning("[RoadMapper] Couldn't read NomapPrinter's underfog layer value; 'Refresh on map table read' can't redraw the map.");
                return;
            }

            _layerAtPrint = NomapPrinterLink.CurrentUnderfogLayer;
            _overfogAtPrint = NomapPrinterLink.CurrentOverfogLayer;
            _awaitingLayerUntil = Time.unscaledTime + RefreshWaitSeconds;

            if (IsServer)
                HandleLayerFlushRequest();
            else
            {
                ZNetPeer server = ZNet.instance.GetServerPeer();
                if (server != null)
                    LayerFlushRPC.SendPackage(server.m_uid, new ZPackage());
            }
        }

        private void CheckForRefreshedLayer()
        {
            if (Time.unscaledTime > _awaitingLayerUntil)
            {
                _awaitingLayerUntil = 0f;
                return;
            }

            // A synced layer is a fresh string, so reference inequality means "new layer arrived".
            // Either layer counts (the overfog value reads null if it can't be found).
            if (ReferenceEquals(NomapPrinterLink.CurrentUnderfogLayer, _layerAtPrint)
                && ReferenceEquals(NomapPrinterLink.CurrentOverfogLayer, _overfogAtPrint))
                return;

            _awaitingLayerUntil = 0f;
            _reprinting = true;
            try
            {
                Jotunn.Logger.LogInfo("[RoadMapper] Newer road layer arrived; redrawing the map.");
                NomapPrinterLink.GenerateMap();
            }
            catch (Exception ex)
            {
                Jotunn.Logger.LogWarning($"[RoadMapper] Map redraw failed. {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _reprinting = false;
            }
        }

        private IEnumerator ServerReceiveLayerFlushRequest(long sender, ZPackage package)
        {
            HandleLayerFlushRequest();
            yield break;
        }

        private void HandleLayerFlushRequest()
        {
            if (!_layerReady || !_refreshOnTableRead.Value || !_layerDirty)
                return;

            if (Time.unscaledTime - _lastTableFlushTime >= _tableReadCooldownSeconds.Value)
            {
                _lastTableFlushTime = Time.unscaledTime;
                RequestLayerWrite("map table read");
            }
            else
            {
                // Picked up by Update() once the cooldown has passed.
                _tableFlushPending = true;
            }
        }

        private struct LayerSnapshot
        {
            // One entry per layer to write.
            public List<(LayerKind Kind, List<MarkerPoint> Points)> Layers;
            public Dictionary<int, LayerRenderer.Brush> Brushes;
            public IReadOnlyDictionary<int, MarkerIcon> Markers;
            public LayerRenderer.MapGeometry Geometry;
            public string WorldName;
            public string Reason;
        }

        // Main thread only: reads config and Unity colour parsing.
        private LayerSnapshot TakeLayerSnapshot(string reason, LayerKind kinds)
        {
            Dictionary<int, LayerRenderer.Brush> brushes = new Dictionary<int, LayerRenderer.Brush>();
            for (int id = 1; id <= 4; id++)
            {
                (float width, string colour) = GetBrushConfig(id);
                if (!ColorUtility.TryParseHtmlString(colour, out Color c))
                {
                    Jotunn.Logger.LogWarning($"[RoadMapper] Brush{id} colour \"{colour}\" isn't a valid hex colour; using white.");
                    c = Color.white;
                }
                Color32 c32 = c;
                brushes[id] = new LayerRenderer.Brush { R = c32.r, G = c32.g, B = c32.b, A = c32.a, WidthMetres = width };
            }

            return new LayerSnapshot
            {
                Layers = SnapshotLayers(kinds),
                Brushes = brushes,
                Markers = MapMarkers.ById,
                Geometry = _layerGeometry,
                WorldName = _layerWorldName,
                Reason = reason
            };
        }

        private List<(LayerKind, List<MarkerPoint>)> SnapshotLayers(LayerKind kinds)
        {
            List<(LayerKind, List<MarkerPoint>)> layers = new List<(LayerKind, List<MarkerPoint>)>();
            if ((kinds & LayerKind.Underfog) != 0)
                layers.Add((LayerKind.Underfog, LoadPointsFromFile(_layerPointsPath)));
            if ((kinds & LayerKind.Overfog) != 0)
                layers.Add((LayerKind.Overfog, LoadPointsFromFile(_overfogPointsPath)));
            return layers;
        }

        private void StartBackgroundLayerWrite(string reason)
        {
            LayerSnapshot snapshot = TakeLayerSnapshot(reason, _dirtyKinds);
            _dirtyKinds = LayerKind.None;
            _strikesSinceWrite = 0;
            _layerJobRunning = true;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    WriteLayerFiles(snapshot);
                }
                catch (Exception ex)
                {
                    Jotunn.Logger.LogError($"[RoadMapper] Road layer write failed. {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    _layerJobRunning = false;
                }
            });
        }

        // Last chance to get unwritten strikes onto the map. Runs synchronously on the main thread.
        private void FlushLayerOnExit(string reason)
        {
            if (!_layerReady || !_layerDirty) return;
            try
            {
                LayerKind kinds = _dirtyKinds;
                _dirtyKinds = LayerKind.None;
                WriteLayerFiles(TakeLayerSnapshot(reason, kinds));
            }
            catch (Exception ex)
            {
                Jotunn.Logger.LogError($"[RoadMapper] Final road layer write failed. {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Thread-safe (no Unity API). Renders once, then writes the same PNG under every map type
        // NomapPrinter supports, so the layer keeps working if the server's map type is changed.
        private void WriteLayerFiles(LayerSnapshot s)
        {
            lock (_layerWriteLock)
            {
                foreach ((LayerKind kind, List<MarkerPoint> points) in s.Layers)
                    WriteLayer(s, kind, points);
            }
        }

        // Caller holds _layerWriteLock (the renderer shares one pixel buffer).
        private void WriteLayer(LayerSnapshot s, LayerKind kind, List<MarkerPoint> points)
        {
            {
                string dir = NomapPrinterLink.ConfigDirectory;
                Directory.CreateDirectory(dir);
                string suffix = kind == LayerKind.Overfog ? "overfog" : "underfog";

                // No admin markings and no overfog file yet: don't create one (it would be synced
                // to every client for nothing). Once a file exists it's always rewritten, so
                // erasing the last admin marking clears it.
                if (kind == LayerKind.Overfog && points.Count == 0
                    && !File.Exists(Path.Combine(dir, NomapPrinterLink.LayerFileName(NomapPrinterLink.LayerMapTypes[0], s.WorldName, suffix))))
                    return;

                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                byte[] png = LayerRenderer.Render(points, s.Brushes, s.Markers, s.Geometry);

                foreach (string mapType in NomapPrinterLink.LayerMapTypes)
                {
                    string dest = Path.Combine(dir, NomapPrinterLink.LayerFileName(mapType, s.WorldName, suffix));
                    // Write under a name NomapPrinter ignores, then swap it in, so its file watcher
                    // never reads a half-written PNG.
                    string tmp = dest + ".tmp";
                    File.WriteAllBytes(tmp, png);
                    if (File.Exists(dest))
                    {
                        try
                        {
                            File.Replace(tmp, dest, null);
                        }
                        catch (Exception)
                        {
                            File.Delete(dest);
                            File.Move(tmp, dest);
                        }
                    }
                    else
                    {
                        File.Move(tmp, dest);
                    }
                }

                Jotunn.Logger.LogInfo($"[RoadMapper] {(kind == LayerKind.Overfog ? "Admin overfog" : "Road")} layer written ({s.Reason}): {points.Count} points, " +
                    $"{png.Length / 1024} KB, {sw.ElapsedMilliseconds} ms.");
            }
        }

        // ---------------------------------------------------------------------
        // The tools (hoe pieces)
        // ---------------------------------------------------------------------

        private void CreatePieces()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= CreatePieces;

            CreateToolAndPieceTable();

            AddMarkerPiece(PathMarkerPrefabName, "Path Marker",
                "Marks a path outline on the map. Does not affect terrain.", LoadIcon("Icons/PathIcon.png"));
            AddMarkerPiece(RoadMarkerPrefabName, "Road Marker",
                "Like the path marker, but for major roads.", LoadIcon("Icons/RoadIcon.png"));
            AddMarkerPiece(WallMarkerPrefabName, "Wall Marker",
                "Marks out walls on the map.", LoadIcon("Icons/WallIcon.png"));
            AddMarkerPiece(FenceMarkerPrefabName, "Fence Marker",
                "Marks out fences on the map.", LoadIcon("Icons/FenceIcon.png"));
            AddMarkerPiece(EraserPrefabName, "Mark Eraser",
                "Erases map marks near where it is used.", LoadIcon("Icons/EraserIcon.png"));

            // In case I feel like adding more tools later:
            // AddMarkerPiece("RoadMapper_SomeMarker", "Some Marker", "Marks out something on the map.", "Icons/SomeIcon.png");
            // ...then give it a brush id in GetBrushId() and a Brush5 config section.

            // Map Markers tab: one piece per PNG in BepInEx/config/RoadMapper/Markers/ (see MapMarkers).
            foreach (MarkerIcon marker in MapMarkers.ById.Values)
            {
                AddMarkerPiece(marker.PrefabName, marker.Name,
                    $"Marks a {marker.Name.ToLowerInvariant()} on the map.", marker.Sprite, MapMarkersCategory);
            }

            // Admin pieces, at the end of the same tabs. They draw on the over-fog layer, which
            // everyone sees whether they've explored there or not.
            const string adminNote = " Admin only: shows on everyone's map, even where they haven't explored.";
            AddAdminPiece(PathMarkerPrefabName, "Admin Path Marker", "Marks a path outline." + adminNote, LoadIcon("Icons/PathIcon.png"), RoadsCategory);
            AddAdminPiece(RoadMarkerPrefabName, "Admin Road Marker", "Marks a major road." + adminNote, LoadIcon("Icons/RoadIcon.png"), RoadsCategory);
            AddAdminPiece(WallMarkerPrefabName, "Admin Wall Marker", "Marks out walls." + adminNote, LoadIcon("Icons/WallIcon.png"), RoadsCategory);
            AddAdminPiece(FenceMarkerPrefabName, "Admin Fence Marker", "Marks out fences." + adminNote, LoadIcon("Icons/FenceIcon.png"), RoadsCategory);
            _adminPieces.Add(AddMarkerPiece(AdminEraserPrefabName, "Admin Mark Eraser",
                "Erases admin map marks near where it is used. Doesn't touch ordinary marks. Admin only.",
                AdminIcon(LoadIcon("Icons/EraserIcon.png"))).Piece);
            foreach (MarkerIcon marker in MapMarkers.ById.Values)
            {
                AddAdminPiece(marker.PrefabName, "Admin " + marker.Name,
                    $"Marks a {marker.Name.ToLowerInvariant()}." + adminNote, marker.Sprite, MapMarkersCategory);
            }

            // Hidden until we know this player is an admin.
            foreach (Piece piece in _adminPieces)
                if (piece != null) piece.m_enabled = false;
            _adminPiecesShown = false;
        }

        private static readonly List<Piece> _adminPieces = new List<Piece>();
        private static bool _adminPiecesShown;
        private float _nextAdminCheck;

        private static void AddAdminPiece(string basePrefabName, string name, string description, Sprite icon, string category)
        {
            string prefabName = AdminPrefix + basePrefabName.Substring("RoadMapper_".Length);
            _adminPieces.Add(AddMarkerPiece(prefabName, name, description, AdminIcon(icon), category).Piece);
        }

        // This player counts as an admin when they're the host / single player, or the server has
        // told Jotunn they're on its admin list.
        internal static bool LocalPlayerIsAdmin =>
            ZNet.instance != null && (ZNet.instance.IsServer() || SynchronizationManager.Instance.PlayerIsAdmin);

        private static readonly MethodInfo UpdateAvailablePiecesListMethod =
            AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");

        // Shows or hides the admin pieces when admin status changes. Hiding is cosmetic; the
        // server's sender check is what actually stops a non-admin.
        private void UpdateAdminPieces()
        {
            if (Time.unscaledTime < _nextAdminCheck) return;
            _nextAdminCheck = Time.unscaledTime + 1f;

            bool show = LocalPlayerIsAdmin;
            if (show == _adminPiecesShown) return;
            _adminPiecesShown = show;
            foreach (Piece piece in _adminPieces)
                if (piece != null) piece.m_enabled = show;

            if (Player.m_localPlayer != null && UpdateAvailablePiecesListMethod != null)
                UpdateAvailablePiecesListMethod.Invoke(Player.m_localPlayer, null);
            Jotunn.Logger.LogInfo($"[RoadMapper] Admin pieces {(show ? "shown" : "hidden")}.");
        }

        // The ordinary icon with a gold frame, so admin pieces stand out in the build menu.
        private static Sprite AdminIcon(Sprite source)
        {
            if (source == null) return null;
            try
            {
                Rect r = source.textureRect;
                int w = (int)r.width, h = (int)r.height;
                Color32[] all = source.texture.GetPixels32();
                int texW = source.texture.width;
                Color32[] px = new Color32[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        px[y * w + x] = all[((int)r.y + y) * texW + (int)r.x + x];

                int t = Math.Max(1, Math.Min(w, h) / 16);
                Color32 gold = new Color32(255, 196, 40, 255);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (x < t || y < t || x >= w - t || y >= h - t)
                            px[y * w + x] = gold;

                Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    filterMode = source.texture.filterMode,
                    wrapMode = TextureWrapMode.Clamp,
                    name = source.texture.name + "_Admin"
                };
                tex.SetPixels32(px);
                tex.Apply(false);
                Sprite sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
                sprite.name = source.name + "_Admin";
                return sprite;
            }
            catch (Exception ex)
            {
                // e.g. an unreadable texture: the plain icon will do.
                Jotunn.Logger.LogWarning($"[RoadMapper] Admin icon for '{source.name}' not framed: {ex.Message}");
                return source;
            }
        }

        // The roadmapping tool: its own item with its own piece table, so the marker pieces
        // no longer clutter the hoe. The tabs are the table's custom categories.
        private static void CreateToolAndPieceTable()
        {
            PieceTableConfig tableConfig = new PieceTableConfig
            {
                CanRemovePieces = false,
                UseCategories = false,
                UseCustomCategories = true,
                CustomCategories = new[] { RoadsCategory, MapMarkersCategory }
            };
            PieceManager.Instance.AddPieceTable(new CustomPieceTable(PieceTableName, tableConfig));

            ItemConfig toolConfig = new ItemConfig
            {
                // Display name only. The prefab stays RoadMapper_Tool so tools already crafted carry over.
                Name = "Surveyor",
                Description = "Marks roads, walls and places onto the printed map. Does not affect terrain.",
                PieceTable = PieceTableName,
                CraftingStation = CraftingStations.Workbench
            };

            // Inventory icon (Icons/ToolIcon.png next to the DLL): the wax-sealed scroll.
            // If the file's missing, the hammer's icon stays.
            Sprite toolIcon = LoadIcon("Icons/ToolIcon.png");
            if (toolIcon != null)
                toolConfig.Icons = new[] { toolIcon };
            // Same recipe as the vanilla hoe for now.
            toolConfig.AddRequirement("Wood", 5, 0);
            toolConfig.AddRequirement("Stone", 2, 0);

            // Cloned from the vanilla Hammer for how it's held and used (was the Hoe until 2.1; the
            // prefab name is unchanged, so existing tools carry over). The hammer's mesh is then
            // swapped for a kitbashed bundle of a tiny marker banner and a tiny wisp torch.
            CustomItem tool = new CustomItem(ToolPrefabName, "Hammer", toolConfig);
            SurveyorModel.Apply(tool.ItemPrefab);

            // Marking the map shouldn't wear you out or wear the tool out.
            ItemDrop.ItemData.SharedData shared = tool.ItemDrop.m_itemData.m_shared;
            shared.m_attack.m_attackStamina = 0f;
            shared.m_useDurability = false;

            ItemManager.Instance.AddItem(tool);
        }

        private static CustomPiece AddMarkerPiece(string prefabName, string name, string description, Sprite icon,
            string category = RoadsCategory)
        {
            PieceConfig config = new PieceConfig
            {
                Name = name,
                Description = description,
                PieceTable = PieceTableName,
                Category = category,
                Icon = icon
            };

            // Cloned from the hoe's Level Ground piece to borrow its placement ghost.
            CustomPiece piece = new CustomPiece(prefabName, "mud_road_v2", config);

            // The clone also carries the TerrainOp that flattens the ground, so strip it and put
            // in a component that just removes the placed copy. Marking must never touch terrain.
            foreach (TerrainOp op in piece.PiecePrefab.GetComponentsInChildren<TerrainOp>(true))
                UnityEngine.Object.DestroyImmediate(op, true);
            piece.PiecePrefab.AddComponent<MarkerPieceCleanup>();

            PieceManager.Instance.AddPiece(piece);
            return piece;
        }

        // Icons live in an Icons folder next to the DLL.
        private static Sprite LoadIcon(string relativePath)
        {
            string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string spritePath = Path.Combine(dllDir, relativePath);
            if (!File.Exists(spritePath))
            {
                Jotunn.Logger.LogWarning($"[RoadMapper] Icon not found: {spritePath}");
                return null;
            }
            return AssetUtils.LoadSpriteFromFile(spritePath);
        }

        // Brush id for a piece, and whether it's an admin (overfog) piece. 0 = not one of mine.
        private static int GetBrushId(string pieceName, out bool admin)
        {
            admin = pieceName.StartsWith(AdminPrefix, StringComparison.Ordinal);
            if (admin)
                pieceName = "RoadMapper_" + pieceName.Substring(AdminPrefix.Length);
            switch (pieceName)
            {
                case PathMarkerPrefabName: return 1;
                case RoadMarkerPrefabName: return 2;
                case WallMarkerPrefabName: return 3;
                case FenceMarkerPrefabName: return 4;
                default:
                    return MapMarkers.TryGetIdForPrefab(pieceName, out int markerId) ? markerId : 0; // 0 = not one of mine
            }
        }

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        // Valheim announces every newly known build piece top-left ("New piece: ..."). With a
        // marker per icon that's dozens of messages the first time you pick up the Surveyor, so
        // marker pieces are learned silently: same bookkeeping as the vanilla method (the piece
        // goes into m_knownRecipes), just without the message.
        [HarmonyPatch(typeof(Player), "AddKnownPiece")]
        public static class Player_AddKnownPiece_Patch
        {
            public static bool Prefix(Piece piece, HashSet<string> ___m_knownRecipes)
            {
                if (piece == null || !(MapMarkers.TryGetIdForPrefab(piece.name, out _)
                        || piece.name.StartsWith(AdminPrefix, StringComparison.Ordinal) || piece.name == AdminEraserPrefabName))
                    return true; // not a marker: vanilla behaviour, message and all

                ___m_knownRecipes.Add(piece.m_name);
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
        public static class Player_PlacePiece_Patch
        {
            public static void Postfix(Piece piece, Vector3 pos)
            {
                if (piece == null) return;

                if (piece.name == AdminEraserPrefabName)
                {
                    if (!LocalPlayerIsAdmin) return;
                    Instance.RemoveFlagsNear(pos, admin: true);
                    if (IsServer)
                        Instance.EraseMarkersNear(pos.x, pos.z, LayerKind.Overfog);
                    else
                        SendToServer(AdminEraseRPC, pos.x, pos.z, null);
                    return;
                }

                if (piece.name == EraserPrefabName)
                {
                    Instance.RemoveFlagsNear(pos, admin: false);
                    if (IsServer)
                    {
                        Instance.EraseMarkersNear(pos.x, pos.z, LayerKind.Underfog);
                    }
                    else
                    {
                        ZPackage erasePackage = new ZPackage();
                        erasePackage.Write(pos.x);
                        erasePackage.Write(pos.z);
                        EraseRPC.SendPackage(ZRoutedRpc.Everybody, erasePackage);
                        Jotunn.Logger.LogDebug($"[RoadMapper] Erase request sent: ({pos.x}, {pos.z})");
                    }
                    return;
                }

                int brushId = GetBrushId(piece.name, out bool admin);
                if (brushId == 0) return;

                if (admin)
                {
                    if (!LocalPlayerIsAdmin) return;
                    Instance.SpawnFlag(pos, brushId + AdminIdOffset);
                    if (IsServer)
                        Instance.RecordMarkerPoint(pos.x, pos.z, brushId, LayerKind.Overfog);
                    else
                        SendToServer(AdminMarkRPC, pos.x, pos.z, brushId);
                    return;
                }

                Instance.SpawnFlag(pos, brushId);

                if (IsServer)
                {
                    Instance.RecordMarkerPoint(pos.x, pos.z, brushId, LayerKind.Underfog);
                    return;
                }

                ZPackage package = new ZPackage();
                package.Write(pos.x);
                package.Write(pos.z);
                package.Write(brushId);
                PathMarkerRPC.SendPackage(ZRoutedRpc.Everybody, package);
                Jotunn.Logger.LogDebug($"[RoadMapper] Paint coord sent: ({pos.x}, {pos.z}, {brushId})");
            }
        }

        // Admin RPCs go to the server peer only.
        private static void SendToServer(CustomRPC rpc, float x, float z, int? brushId)
        {
            ZNetPeer server = ZNet.instance != null ? ZNet.instance.GetServerPeer() : null;
            if (server == null) return;
            ZPackage package = new ZPackage();
            package.Write(x);
            package.Write(z);
            if (brushId.HasValue) package.Write(brushId.Value);
            rpc.SendPackage(server.m_uid, package);
        }

        // ---------------------------------------------------------------------
        // Ground flags (client side, local only)
        //
        // A small tinted flag goes down wherever the local player strikes with a road tool, so
        // you can see what you've marked. They are plain GameObjects (no ZNetView): nobody else
        // sees them, nothing is saved, and they all go when the Surveyor leaves your hand.
        //
        // The flag is the Mistlands wisp torch at half scale, tinted to the brush colour, glowing
        // ball and all (the ball is what makes the colour readable; the wood torch's flame was
        // too small to tell colours apart). It's a copy of the torch prefab stripped
        // down to meshes and particles: no light, sound, collider, fuel logic or network object.
        // TODO(model): swap FlagSourcePrefabName for a proper flag model when there is one.
        // ---------------------------------------------------------------------

        private const string FlagSourcePrefabName = "piece_groundtorch_mist"; // Wisp torch
        private const float FlagScale = 0.5f;

        // Server never sends points from further away than this, whatever the client asks for.
        private const float MaxFlagRadius = 300f;
        // Re-ask the server once you've walked this fraction of the radius from the last ask.
        private const float FlagRefreshFraction = 0.33f;
        private const float FlagRefreshMinSeconds = 2f;

        // Flags keyed by point (0.1 m grid + brush), so a refresh from the server can keep the
        // flags that are still there, add new ones and drop erased ones without everything
        // flickering. The server stores floats as text, so exact float keys wouldn't line up.
        private readonly Dictionary<(int, int, int), GameObject> _flags = new Dictionary<(int, int, int), GameObject>();
        private readonly Dictionary<int, GameObject> _flagTemplates = new Dictionary<int, GameObject>();
        private GameObject _flagTemplateRoot;
        private bool _flagSourceMissingLogged;

        private bool _flagsActive;
        private Vector3 _lastFlagRequestPos;
        private float _lastFlagRequestTime = float.NegativeInfinity;

        private static (int, int, int) FlagKey(float x, float z, int brushId) =>
            ((int)Math.Round(x * 10f), (int)Math.Round(z * 10f), brushId);

        // Line brushes (1-4) and loaded map markers (50+) get flags, admin or not; anything else is ignored.
        private static bool HasFlag(int brushId)
        {
            int id = BaseId(brushId);
            return (id >= 1 && id <= 4) || MapMarkers.IsMarker(id);
        }

        // Admin points look different, so players can see a road is the server's and not theirs:
        // line brushes are a trio of wisps joined at the base and splayed out at the top (same tint,
        // so colours read the same as ordinary flags) around an untinted standing iron torch, and
        // marker banners are gold instead of purple.
        // (Tried and dropped: the standing iron torch and the Dvergr pole lantern; their colours
        // were too hard to tell apart.)
        private const int AdminTrioCount = 3;
        private const float AdminTrioSplay = 20f;     // degrees each wisp leans out from upright
        private const string AdminTorchPrefabName = "piece_groundtorch"; // Standing iron torch, natural flame
        private static readonly Color32 AdminBannerColour = new Color32(201, 162, 39, 255); // gold

        // Markers show a banner on a pole (MarkerBanner), one template per marker. If that can't be
        // built, every marker falls back to one shared full-size magenta wisp torch, cached under this key.
        private const int MarkerFlagTemplateKey = -1;
        private static readonly Color MarkerFlagColour = new Color(1f, 0f, 1f, 1f);
        private const float MarkerFlagScale = 1f;

        private static bool IsHoldingTool(Player player)
        {
            // GetRightItem() isn't public, so look through what's equipped instead. The tool can
            // only ever be equipped in the right hand, so this is the same check.
            foreach (ItemDrop.ItemData item in player.GetInventory().GetEquippedItems())
            {
                if (item.m_dropPrefab != null && item.m_dropPrefab.name == ToolPrefabName)
                    return true;
            }
            return false;
        }

        // Tool comes out: ask the server for the points around you. While held: ask again once
        // you've walked far enough. Tool goes away: everything is destroyed.
        private void UpdateFlags()
        {
            Player player = Player.m_localPlayer;
            bool shouldShow = player != null && _showFlags.Value && IsHoldingTool(player);

            if (!shouldShow)
            {
                if (_flagsActive)
                {
                    _flagsActive = false;
                    ClearFlags();
                }
                return;
            }

            Vector3 here = player.transform.position;
            float radius = Mathf.Clamp(_flagRadius.Value, 10f, MaxFlagRadius);

            if (!_flagsActive)
            {
                _flagsActive = true;
                RequestFlagPoints(here, radius);
                return;
            }

            Vector3 moved = here - _lastFlagRequestPos;
            float refreshDistance = radius * FlagRefreshFraction;
            if (moved.x * moved.x + moved.z * moved.z >= refreshDistance * refreshDistance
                && Time.unscaledTime - _lastFlagRequestTime >= FlagRefreshMinSeconds)
            {
                RequestFlagPoints(here, radius);
            }
        }

        private void RequestFlagPoints(Vector3 centre, float radius)
        {
            _lastFlagRequestPos = centre;
            _lastFlagRequestTime = Time.unscaledTime;

            if (IsServer)
            {
                // Host / single player: no round trip needed.
                ApplyFlagPoints(FindPointsNear(centre.x, centre.z, radius));
                return;
            }

            ZNetPeer server = ZNet.instance != null ? ZNet.instance.GetServerPeer() : null;
            if (server == null) return;

            ZPackage package = new ZPackage();
            package.Write(centre.x);
            package.Write(centre.z);
            package.Write(radius);
            FlagPointsRPC.SendPackage(server.m_uid, package);
        }

        // Makes the shown flags match this set of points: keeps matches, adds new, removes the rest.
        private void ApplyFlagPoints(List<MarkerPoint> points)
        {
            if (!_flagsActive) return; // tool was put away while the answer was in flight

            HashSet<(int, int, int)> wanted = new HashSet<(int, int, int)>();
            foreach (MarkerPoint p in points)
            {
                if (!HasFlag(p.BrushId)) continue;
                (int, int, int) key = FlagKey(p.x, p.z, p.BrushId);
                if (!wanted.Add(key)) continue;
                if (!_flags.ContainsKey(key))
                    CreateFlag(key, new Vector3(p.x, GroundHeightAt(p.x, p.z), p.z), p.BrushId);
            }

            List<(int, int, int)> stale = new List<(int, int, int)>();
            foreach (KeyValuePair<(int, int, int), GameObject> kv in _flags)
                if (!wanted.Contains(kv.Key)) stale.Add(kv.Key);
            foreach ((int, int, int) key in stale)
                DestroyFlag(key);

            TrimFlagsToMax();
        }

        // The server only stores x and z, so height comes from the world: the terrain, or a floor
        // or bridge up to a few metres above it (so flags on stone roads and decks sit on top).
        private static float GroundHeightAt(float x, float z)
        {
            ZoneSystem zones = ZoneSystem.instance;
            if (zones == null) return 0f;
            float ground = zones.GetGroundHeight(new Vector3(x, 0f, z));
            if (zones.GetSolidHeight(new Vector3(x, ground, z), out float solid, 3) && solid > ground)
                return solid;
            return ground;
        }

        // Called straight after a local strike, so there's a flag before the server has even heard of it.
        private void SpawnFlag(Vector3 pos, int brushId)
        {
            if (!_flagsActive || !HasFlag(brushId)) return;
            (int, int, int) key = FlagKey(pos.x, pos.z, brushId);
            if (_flags.ContainsKey(key)) return;
            CreateFlag(key, pos, brushId);
            TrimFlagsToMax();
        }

        private void CreateFlag((int, int, int) key, Vector3 pos, int brushId)
        {
            GameObject template = GetFlagTemplate(brushId);
            if (template == null) return;

            // Spin from the position rather than at random, so a flag keeps its angle across refreshes.
            float yaw = Mathf.Abs((key.Item1 * 73856093) ^ (key.Item2 * 19349663)) % 360;
            _flags[key] = Instantiate(template, pos, Quaternion.Euler(0f, yaw, 0f));
        }

        private void DestroyFlag((int, int, int) key)
        {
            if (_flags.TryGetValue(key, out GameObject flag) && flag != null)
                Destroy(flag);
            _flags.Remove(key);
        }

        // Over the limit: drop the flags furthest from the player.
        private void TrimFlagsToMax()
        {
            int max = Math.Max(1, _maxFlags.Value);
            if (_flags.Count <= max) return;

            Vector3 here = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : _lastFlagRequestPos;
            List<KeyValuePair<(int, int, int), GameObject>> all = new List<KeyValuePair<(int, int, int), GameObject>>(_flags);
            all.Sort((a, b) => FlatDistanceSq(b.Value, here).CompareTo(FlatDistanceSq(a.Value, here)));
            for (int i = 0; i < all.Count - max; i++)
                DestroyFlag(all[i].Key);
        }

        private static float FlatDistanceSq(GameObject go, Vector3 p)
        {
            if (go == null) return float.MaxValue;
            Vector3 d = go.transform.position - p;
            return d.x * d.x + d.z * d.z;
        }

        // Each eraser only takes down its own kind of flag, the same as on the server.
        private void RemoveFlagsNear(Vector3 pos, bool admin)
        {
            float r2 = _eraserSize.Value * _eraserSize.Value;
            List<(int, int, int)> doomed = new List<(int, int, int)>();
            foreach (KeyValuePair<(int, int, int), GameObject> kv in _flags)
                if (IsAdminId(kv.Key.Item3) == admin && FlatDistanceSq(kv.Value, pos) <= r2) doomed.Add(kv.Key);
            foreach ((int, int, int) key in doomed)
                DestroyFlag(key);
        }

        private void ClearFlags()
        {
            foreach (GameObject flag in _flags.Values)
                if (flag != null) Destroy(flag);
            _flags.Clear();
        }

        private void ClearFlagTemplates()
        {
            foreach (GameObject template in _flagTemplates.Values)
                if (template != null) Destroy(template);
            _flagTemplates.Clear();
        }

        // One template per brush, kept under an inactive holder so the templates themselves never
        // render. Instantiating a template with no parent gives an active copy.
        private GameObject GetFlagTemplate(int flagId)
        {
            bool admin = IsAdminId(flagId);
            int brushId = BaseId(flagId);
            bool isMarker = MapMarkers.IsMarker(brushId);
            if (_flagTemplates.TryGetValue(flagId, out GameObject cached) && cached != null)
                return cached;

            if (_flagTemplateRoot == null)
            {
                _flagTemplateRoot = new GameObject("RoadMapper_FlagTemplates");
                _flagTemplateRoot.SetActive(false);
                DontDestroyOnLoad(_flagTemplateRoot);
            }

            if (isMarker && MapMarkers.ById.TryGetValue(brushId, out MarkerIcon icon))
            {
                GameObject banner = MarkerBanner.Build(icon, _flagTemplateRoot.transform, background: admin ? AdminBannerColour : (Color32?)null);
                if (banner != null)
                {
                    _flagTemplates[flagId] = banner;
                    return banner;
                }
                // Couldn't build the banner: shared placeholder below.
            }

            int templateKey = isMarker ? MarkerFlagTemplateKey : flagId;
            if (_flagTemplates.TryGetValue(templateKey, out cached) && cached != null)
                return cached;

            GameObject source = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(FlagSourcePrefabName) : null;
            if (source == null)
            {
                if (!_flagSourceMissingLogged)
                {
                    _flagSourceMissingLogged = true;
                    Jotunn.Logger.LogWarning($"[RoadMapper] Flag model '{FlagSourcePrefabName}' not found; no flags will be shown.");
                }
                return null;
            }

            Color colour;
            float scale;
            if (isMarker)
            {
                colour = MarkerFlagColour;
                scale = MarkerFlagScale;
            }
            else
            {
                (_, string colourHex) = GetBrushConfig(brushId);
                if (!ColorUtility.TryParseHtmlString(colourHex, out colour))
                    colour = Color.white;
                colour.a = 1f;
                scale = FlagScale;
            }

            GameObject template = admin && !isMarker
                ? BuildAdminTrio(source, colour, templateKey, scale, _flagTemplateRoot.transform)
                : BuildFlagTemplate(source, colour, templateKey, scale, _flagTemplateRoot.transform);
            _flagTemplates[templateKey] = template;
            return template;
        }

        // Three ordinary flags sharing one foot, turned 120 degrees apart and each leaning outward.
        // The wisp's pivot is at its foot, so the lean pivots on the shared base.
        private static GameObject BuildAdminTrio(GameObject source, Color colour, int flagId, float scale, Transform inactiveParent)
        {
            GameObject root = new GameObject($"RoadMapper_AdminFlag_{flagId}");
            root.transform.SetParent(inactiveParent, false);
            for (int i = 0; i < AdminTrioCount; i++)
            {
                GameObject wisp = BuildFlagTemplate(source, colour, flagId, scale, root.transform);
                wisp.transform.localPosition = Vector3.zero;
                wisp.transform.localRotation = Quaternion.Euler(0f, i * 360f / AdminTrioCount, 0f) * Quaternion.Euler(AdminTrioSplay, 0f, 0f);
            }

            // An iron torch in the middle, with its own orange flame (not tinted), so the cluster
            // reads as "admin" at a glance. Skipped if the prefab can't be found.
            GameObject torchSource = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(AdminTorchPrefabName) : null;
            if (torchSource != null)
            {
                GameObject torch = BuildPlainTemplate(torchSource, scale, root.transform);
                torch.transform.localPosition = Vector3.zero;
                torch.transform.localRotation = Quaternion.identity;
            }
            return root;
        }

        // A prefab stripped to its meshes and fire, in its own colours.
        private static GameObject BuildPlainTemplate(GameObject source, float scale, Transform inactiveParent)
        {
            GameObject root = Instantiate(source, inactiveParent, false);
            root.name = $"RoadMapper_Plain_{source.name}";
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;

            Fireplace fireplace = root.GetComponent<Fireplace>();
            if (fireplace != null)
            {
                if (fireplace.m_enabledObject != null) fireplace.m_enabledObject.SetActive(true);
                if (fireplace.m_enabledObjectLow != null)
                {
                    fireplace.m_enabledObjectLow.SetActive(true);
                    if (fireplace.m_enabledObjectHigh != null) fireplace.m_enabledObjectHigh.SetActive(false);
                }
                else if (fireplace.m_enabledObjectHigh != null)
                {
                    fireplace.m_enabledObjectHigh.SetActive(true);
                }
            }

            StripToVisuals(root);
            foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                ParticleSystem.MainModule main = ps.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy; // so the scale applies to the flame too
            }
            StandOnGround(root, source.name);
            root.transform.localScale = Vector3.one * scale;
            return root;
        }

        internal static GameObject BuildFlagTemplate(GameObject source, Color colour, int brushId, float scale, Transform inactiveParent)
        {
            // Instantiated under an inactive parent, so no Awake/Start runs on any of the torch's
            // scripts (ZNetView, Fireplace, WearNTear...) and it's safe to rip them out.
            GameObject root = Instantiate(source, inactiveParent, false);
            root.name = $"RoadMapper_Flag_{brushId}";
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;

            // The fire lives on child objects that Fireplace switches on at runtime, so switch the
            // right one on ourselves before Fireplace is removed. Low-quality fire when there's a
            // choice: there can be hundreds of these flags.
            Fireplace fireplace = root.GetComponent<Fireplace>();
            if (fireplace != null)
            {
                if (fireplace.m_enabledObject != null) fireplace.m_enabledObject.SetActive(true);
                if (fireplace.m_enabledObjectLow != null)
                {
                    fireplace.m_enabledObjectLow.SetActive(true);
                    if (fireplace.m_enabledObjectHigh != null) fireplace.m_enabledObjectHigh.SetActive(false);
                }
                else if (fireplace.m_enabledObjectHigh != null)
                {
                    fireplace.m_enabledObjectHigh.SetActive(true);
                }
            }

            StripToVisuals(root);

            // Meshes (the post and the wisp ball): the same greyscale-then-tint as the flames below,
            // including any glow (emission), so the whole flag reads as the brush colour instead of
            // the brush colour mixed with the torch's own blue.
            Dictionary<Material, Material> tinted = new Dictionary<Material, Material>();
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material mat = materials[i];
                    if (mat == null) continue;
                    if (!tinted.TryGetValue(mat, out Material t))
                    {
                        t = new Material(mat);
                        GreyscaleTextureProperty(t, "_MainTex");
                        GreyscaleTextureProperty(t, "_EmissionMap");
                        if (t.HasProperty("_Color"))
                            t.color = colour;
                        TintColourProperty(t, "_EmissionColor", colour);
                        tinted[mat] = t;
                    }
                    materials[i] = t;
                }
                renderer.sharedMaterials = materials;
            }

            // Fire. Valheim's flame textures have the orange painted in, so tinting alone would
            // just give muddy orange. Each flame material gets a greyscale copy of its texture,
            // and then the brush colour is applied on top of that.
            List<string> flameInfo = new List<string>();
            foreach (ParticleSystemRenderer psr in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                Material[] materials = psr.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material mat = materials[i];
                    if (mat == null) continue;
                    if (!tinted.TryGetValue(mat, out Material t))
                    {
                        t = new Material(mat);
                        if (t.HasProperty("_MainTex") && t.mainTexture != null)
                            t.mainTexture = GetGreyscaleTexture(t.mainTexture);
                        TintColourProperty(t, "_Color", colour);
                        TintColourProperty(t, "_TintColor", colour);
                        TintColourProperty(t, "_EmissionColor", colour);
                        tinted[mat] = t;
                        flameInfo.Add($"{mat.name} ({mat.shader.name})");
                    }
                    materials[i] = t;
                }
                psr.sharedMaterials = materials;
            }

            // Particle colour multiplies with the material colour. If the material already carries
            // the tint, the particles go white so the colour isn't applied twice (which darkens
            // it). If it doesn't, the particles carry the tint. Brightness is kept either way.
            foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                ParticleSystem.MainModule main = ps.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy; // so the half scale applies to the flame too

                Material flameMat = ps.GetComponent<ParticleSystemRenderer>()?.sharedMaterial;
                bool materialTinted = flameMat != null && (flameMat.HasProperty("_Color") || flameMat.HasProperty("_TintColor"));

                Color start = main.startColor.mode == ParticleSystemGradientMode.Color ? main.startColor.color : Color.white;
                float intensity = Mathf.Max(1f, Mathf.Max(start.r, Mathf.Max(start.g, start.b)));
                Color particle = materialTinted ? Color.white : colour;
                main.startColor = new Color(particle.r * intensity, particle.g * intensity, particle.b * intensity, start.a);

                // Colour over lifetime keeps only its fade, so it can't drag the colour back to orange.
                ParticleSystem.ColorOverLifetimeModule overLifetime = ps.colorOverLifetime;
                if (overLifetime.enabled)
                {
                    GradientAlphaKey[] alphaKeys = overLifetime.color.mode == ParticleSystemGradientMode.Gradient && overLifetime.color.gradient != null
                        ? overLifetime.color.gradient.alphaKeys
                        : new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) };
                    Gradient fade = new Gradient();
                    fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) }, alphaKeys);
                    overLifetime.color = fade;
                }

                // Same for colour by speed, which fire effects sometimes use instead.
                ParticleSystem.ColorBySpeedModule bySpeed = ps.colorBySpeed;
                if (bySpeed.enabled)
                    bySpeed.enabled = false;
            }

            if (flameInfo.Count > 0)
                Jotunn.Logger.LogDebug($"[RoadMapper] Flag {brushId} flame materials: {string.Join(", ", flameInfo)}");

            // Stand it on the ground: not every prefab's pivot is at its foot (the Dvergr pole
            // lantern's is near the top, so it came out mostly buried). Lift the children so the
            // lowest mesh point sits at the root's origin, which is placed on the ground.
            StandOnGround(root, source.name);

            root.transform.localScale = Vector3.one * scale;
            return root;
        }

        private static void StandOnGround(GameObject root, string label)
        {
            if (MarkerBanner.TryGetBounds(root, root.transform, null, out Bounds b, visibleOnly: true) && Mathf.Abs(b.min.y) > 0.01f)
            {
                Vector3 lift = new Vector3(0f, -b.min.y, 0f);
                foreach (Transform child in root.transform)
                    child.localPosition += lift;
                Jotunn.Logger.LogDebug($"[RoadMapper] Flag model '{label}' lifted {lift.y:0.00} m to stand on the ground.");
            }
        }

        // Keeps only what's needed to draw the torch. Everything else is removed in an order that
        // respects [RequireComponent], so nothing is destroyed while another component needs it.
        internal static void StripToVisuals(GameObject root)
        {
            List<Component> doomed = new List<Component>();
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                if (c is Transform || c is MeshFilter || c is MeshRenderer || c is LODGroup
                    || c is ParticleSystem || c is ParticleSystemRenderer)
                    continue;
                doomed.Add(c);
            }

            for (int pass = 0; pass < 10 && doomed.Count > 0; pass++)
            {
                for (int i = doomed.Count - 1; i >= 0; i--)
                {
                    if (IsRequiredByAnother(doomed[i], doomed)) continue;
                    UnityEngine.Object.DestroyImmediate(doomed[i]);
                    doomed.RemoveAt(i);
                }
            }

            if (doomed.Count > 0)
                Jotunn.Logger.LogWarning($"[RoadMapper] Flag model: couldn't strip {doomed.Count} component(s), e.g. {doomed[0].GetType().Name}.");
        }

        // Keeps the property's own alpha and brightness; only the hue and saturation come from the brush.
        private static void TintColourProperty(Material mat, string property, Color colour)
        {
            if (!mat.HasProperty(property)) return;
            Color original = mat.GetColor(property);
            float intensity = Mathf.Max(original.r, Mathf.Max(original.g, original.b));
            if (intensity <= 0f) return; // property unused (black), leave it alone
            mat.SetColor(property, new Color(colour.r * intensity, colour.g * intensity, colour.b * intensity, original.a));
        }

        private static void GreyscaleTextureProperty(Material mat, string property)
        {
            if (!mat.HasProperty(property)) return;
            Texture tex = mat.GetTexture(property);
            if (tex != null)
                mat.SetTexture(property, GetGreyscaleTexture(tex));
        }

        private static readonly Dictionary<Texture, Texture2D> GreyscaleTextures = new Dictionary<Texture, Texture2D>();

        // Greyscale copy of any texture, readable or not: blit it to a render texture, read that
        // back, convert. Brightness is stretched so the brightest texel is white, which keeps
        // the tinted flame as bright as the original. Cached per source texture.
        private static Texture GetGreyscaleTexture(Texture source)
        {
            if (GreyscaleTextures.TryGetValue(source, out Texture2D cached) && cached != null)
                return cached;

            int w = source.width, h = source.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            Texture2D grey = new Texture2D(w, h, TextureFormat.RGBA32, true);
            grey.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            Color32[] pixels = grey.GetPixels32();
            int maxLum = 1;
            int[] lum = new int[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                // Rec. 709 luma, integer weights out of 256.
                lum[i] = (pixels[i].r * 54 + pixels[i].g * 183 + pixels[i].b * 19) >> 8;
                if (lum[i] > maxLum) maxLum = lum[i];
            }
            for (int i = 0; i < pixels.Length; i++)
            {
                byte v = (byte)Math.Min(255, lum[i] * 255 / maxLum);
                pixels[i] = new Color32(v, v, v, pixels[i].a);
            }
            grey.SetPixels32(pixels);
            grey.Apply(true);
            grey.wrapMode = source.wrapMode;
            grey.filterMode = source.filterMode;
            grey.name = source.name + "_RoadMapperGrey";

            GreyscaleTextures[source] = grey;
            return grey;
        }

        private static bool IsRequiredByAnother(Component c, List<Component> doomed)
        {
            Type type = c.GetType();
            foreach (Component other in doomed)
            {
                if (other == c || other.gameObject != c.gameObject) continue;
                foreach (RequireComponent req in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                {
                    if ((req.m_Type0 != null && req.m_Type0.IsAssignableFrom(type))
                        || (req.m_Type1 != null && req.m_Type1.IsAssignableFrom(type))
                        || (req.m_Type2 != null && req.m_Type2.IsAssignableFrom(type)))
                        return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------------
        // RPC handlers
        // ---------------------------------------------------------------------

        private IEnumerator ServerReceivePathMarker(long sender, ZPackage package)
        {
            float x = package.ReadSingle();
            float z = package.ReadSingle();
            int brushId = package.ReadInt();
            RecordMarkerPoint(x, z, brushId, LayerKind.Underfog);
            yield break;
        }

        private IEnumerator ServerReceiveEraseRequest(long sender, ZPackage package)
        {
            float x = package.ReadSingle();
            float z = package.ReadSingle();
            EraseMarkersNear(x, z, LayerKind.Underfog);
            yield break;
        }

        private IEnumerator ServerReceiveAdminMark(long sender, ZPackage package)
        {
            float x = package.ReadSingle();
            float z = package.ReadSingle();
            int brushId = package.ReadInt();
            if (SenderIsAdmin(sender, "admin mark"))
                RecordMarkerPoint(x, z, brushId, LayerKind.Overfog);
            yield break;
        }

        private IEnumerator ServerReceiveAdminErase(long sender, ZPackage package)
        {
            float x = package.ReadSingle();
            float z = package.ReadSingle();
            if (SenderIsAdmin(sender, "admin erase"))
                EraseMarkersNear(x, z, LayerKind.Overfog);
            yield break;
        }

        // Same check vanilla uses for kick/ban and remote commands: the peer's host name
        // (platform id) against the server's admin list.
        private static bool SenderIsAdmin(long sender, string what)
        {
            ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(sender) : null;
            if (peer != null && peer.m_socket != null && ZNet.instance.IsAdmin(peer.m_socket.GetHostName()))
                return true;
            Jotunn.Logger.LogWarning($"[RoadMapper] Refused {what} from {(peer != null ? peer.m_playerName : sender.ToString())}: not an admin.");
            return false;
        }

        // Flag points: client sends (x, z, radius), server answers with every point in range.
        private IEnumerator ServerReceiveFlagPointsRequest(long sender, ZPackage package)
        {
            float x = package.ReadSingle();
            float z = package.ReadSingle();
            float radius = Mathf.Clamp(package.ReadSingle(), 0f, MaxFlagRadius);

            List<MarkerPoint> points = FindPointsNear(x, z, radius);
            ZPackage reply = new ZPackage();
            reply.Write(points.Count);
            foreach (MarkerPoint p in points)
            {
                reply.Write(p.x);
                reply.Write(p.z);
                reply.Write(p.BrushId);
            }
            FlagPointsRPC.SendPackage(sender, reply);
            yield break;
        }

        private IEnumerator ClientReceiveFlagPoints(long sender, ZPackage package)
        {
            int count = package.ReadInt();
            List<MarkerPoint> points = new List<MarkerPoint>(count);
            for (int i = 0; i < count; i++)
                points.Add(new MarkerPoint { x = package.ReadSingle(), z = package.ReadSingle(), BrushId = package.ReadInt() });
            ApplyFlagPoints(points);
            yield break;
        }

        private List<MarkerPoint> FindPointsNear(float x, float z, float radius)
        {
            List<MarkerPoint> near = new List<MarkerPoint>();
            if (ZNet.instance == null || string.IsNullOrEmpty(ZNet.instance.GetWorldName()))
                return near;

            float r2 = radius * radius;
            foreach (MarkerPoint p in LoadPointsFromFile(GetWorldDataFilePath()))
            {
                float dx = p.x - x, dz = p.z - z;
                if (dx * dx + dz * dz <= r2) near.Add(p);
            }
            // Admin points too, tagged so clients show them as admin flags.
            foreach (MarkerPoint p in LoadPointsFromFile(GetWorldDataFilePath(LayerKind.Overfog)))
            {
                float dx = p.x - x, dz = p.z - z;
                if (dx * dx + dz * dz <= r2) near.Add(new MarkerPoint { x = p.x, z = p.z, BrushId = p.BrushId + AdminIdOffset });
            }
            return near;
        }

        // ---------------------------------------------------------------------
        // Marker pins
        //
        // Every map marker also becomes a vanilla map pin on each client, with the marker's own
        // icon and name. Nomap hides the map itself, but the pins are still there for anything
        // that reads them, e.g. compass mods. Pins are added with save: false, so they never
        // reach anyone's character file; they're rebuilt from the server's point file on join
        // and whenever a marker is placed or erased.
        //
        // NomapPrinter won't print them: it only prints pins whose icon it knows by name.
        // ---------------------------------------------------------------------

        // Icon3 is the plain "pin" type. Some compass mods filter by pin type; if yours hides
        // these, this is the line to change.
        private const Minimap.PinType MarkerPinType = Minimap.PinType.Icon3;

        private readonly List<Minimap.PinData> _markerPins = new List<Minimap.PinData>();
        private bool _markerPinsRequested;

        private void UpdateMarkerPins()
        {
            // Minimap goes with the game scene (logout); its pins go with it.
            if (Minimap.instance == null)
            {
                _markerPinsRequested = false;
                _markerPins.Clear();
                return;
            }
            // Wait for a player, and don't re-ask on respawn: Minimap (and our pins) survive death.
            if (Player.m_localPlayer == null || _markerPinsRequested || !_showMarkerPins.Value)
                return;

            _markerPinsRequested = true;
            if (IsServer)
            {
                ApplyMarkerPins(FindAllMarkerPoints());
                return;
            }
            ZNetPeer server = ZNet.instance != null ? ZNet.instance.GetServerPeer() : null;
            if (server != null)
                MarkerPinsRPC.SendPackage(server.m_uid, new ZPackage());
            else
                _markerPinsRequested = false; // not connected yet; try again next frame
        }

        private void ApplyMarkerPins(List<MarkerPoint> points)
        {
            RemoveMarkerPins();
            if (!_showMarkerPins.Value || Minimap.instance == null)
                return;

            foreach (MarkerPoint p in points)
            {
                // Markers this client has no icon for are skipped.
                if (!MapMarkers.ById.TryGetValue(BaseId(p.BrushId), out MarkerIcon icon))
                    continue;

                Vector3 pos = new Vector3(p.x, GroundHeightAt(p.x, p.z), p.z);
                Minimap.PinData pin = Minimap.instance.AddPin(pos, MarkerPinType, icon.Name, false, false);
                if (pin == null) continue;
                pin.m_icon = icon.Sprite;
                _markerPins.Add(pin);
            }
        }

        private void RemoveMarkerPins()
        {
            if (Minimap.instance != null)
            {
                foreach (Minimap.PinData pin in _markerPins)
                    Minimap.instance.RemovePin(pin);
            }
            _markerPins.Clear();
        }

        // Server: every marker point in the world (any id from 50 up; each client decides which it can show).
        private List<MarkerPoint> FindAllMarkerPoints()
        {
            List<MarkerPoint> markers = new List<MarkerPoint>();
            if (ZNet.instance == null || string.IsNullOrEmpty(ZNet.instance.GetWorldName()))
                return markers;
            foreach (MarkerPoint p in LoadPointsFromFile(GetWorldDataFilePath()))
                if (p.BrushId >= MapMarkers.FirstMarkerId) markers.Add(p);
            foreach (MarkerPoint p in LoadPointsFromFile(GetWorldDataFilePath(LayerKind.Overfog)))
                if (p.BrushId >= MapMarkers.FirstMarkerId)
                    markers.Add(new MarkerPoint { x = p.x, z = p.z, BrushId = p.BrushId + AdminIdOffset });
            return markers;
        }

        private static ZPackage PackPoints(List<MarkerPoint> points)
        {
            ZPackage package = new ZPackage();
            package.Write(points.Count);
            foreach (MarkerPoint p in points)
            {
                package.Write(p.x);
                package.Write(p.z);
                package.Write(p.BrushId);
            }
            return package;
        }

        private static List<MarkerPoint> UnpackPoints(ZPackage package)
        {
            int count = package.ReadInt();
            List<MarkerPoint> points = new List<MarkerPoint>(count);
            for (int i = 0; i < count; i++)
                points.Add(new MarkerPoint { x = package.ReadSingle(), z = package.ReadSingle(), BrushId = package.ReadInt() });
            return points;
        }

        // Server: a marker was placed or erased, so everyone gets the new list.
        private void BroadcastMarkerPins()
        {
            if (!IsServer) return;
            List<MarkerPoint> markers = FindAllMarkerPoints();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                MarkerPinsRPC.SendPackage(peer.m_uid, PackPoints(markers)); // fresh package per peer
            if (Player.m_localPlayer != null)
                ApplyMarkerPins(markers); // host / single player
        }

        private IEnumerator ServerReceiveMarkerPinsRequest(long sender, ZPackage package)
        {
            MarkerPinsRPC.SendPackage(sender, PackPoints(FindAllMarkerPoints()));
            yield break;
        }

        private IEnumerator ClientReceiveMarkerPins(long sender, ZPackage package)
        {
            ApplyMarkerPins(UnpackPoints(package));
            yield break;
        }

        private IEnumerator ClientReceiveNothing(long sender, ZPackage package)
        {
            yield break;
        }

        // ---------------------------------------------------------------------
        // Point store (server side): BepInEx/config/RoadMapper/<WorldName>.txt
        // One "x,z,brushId" per line. Same format and folder as 1.x.
        // ---------------------------------------------------------------------

        // Ordinary (underfog) points: <WorldName>.txt, as in 1.x. Admin (overfog): <WorldName>.overfog.txt.
        public string GetWorldDataFilePath(LayerKind kind = LayerKind.Underfog)
        {
            string folder = Path.Combine(BepInEx.Paths.ConfigPath, "RoadMapper");
            Directory.CreateDirectory(folder);
            string suffix = kind == LayerKind.Overfog ? ".overfog" : "";
            return Path.Combine(folder, $"{ZNet.instance.GetWorldName()}{suffix}.txt");
        }

        private static float Distance(MarkerPoint a, MarkerPoint b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        public List<MarkerPoint> LoadPointsFromFile(string path)
        {
            List<MarkerPoint> points = new List<MarkerPoint>();
            if (!File.Exists(path)) return points;

            foreach (string line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                string[] parts = line.Split(',');
                if (parts.Length != 3
                    || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
                    || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int brushId))
                {
                    Jotunn.Logger.LogWarning($"[RoadMapper] Skipping cocked up line in {path}: \"{line}\"");
                    continue;
                }

                points.Add(new MarkerPoint { x = x, z = z, BrushId = brushId });
            }
            return points;
        }

        private static void WritePointsToFile(string path, List<MarkerPoint> points)
        {
            string[] lines = new string[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                lines[i] = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", points[i].x, points[i].z, points[i].BrushId);
            }
            File.WriteAllLines(path, lines);
        }

        private void RecordMarkerPoint(float x, float z, int brushId, LayerKind kind)
        {
            Jotunn.Logger.LogDebug($"[RoadMapper] Recording {(kind == LayerKind.Overfog ? "admin " : "")}paint coord: ({x:F1}, {z:F1}, {brushId})");
            try
            {
                string path = GetWorldDataFilePath(kind);
                string line = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}{3}", x, z, brushId, Environment.NewLine);
                File.AppendAllText(path, line);
                Jotunn.Logger.LogDebug($"[RoadMapper] Wrote point to {path}");
                OnLayerStrike(kind);
                if (brushId >= MapMarkers.FirstMarkerId)
                    BroadcastMarkerPins();
            }
            catch (Exception ex)
            {
                Jotunn.Logger.LogError($"[RoadMapper] A fuck up occurred. {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void EraseMarkersNear(float x, float z, LayerKind kind)
        {
            string path = GetWorldDataFilePath(kind);
            List<MarkerPoint> loaded = LoadPointsFromFile(path);
            MarkerPoint erasePos = new MarkerPoint { x = x, z = z, BrushId = 0 };

            List<MarkerPoint> kept = new List<MarkerPoint>();
            int removedCount = 0;
            bool removedMarker = false;
            foreach (MarkerPoint point in loaded)
            {
                if (Distance(point, erasePos) <= _eraserSize.Value)
                {
                    removedCount++;
                    removedMarker |= point.BrushId >= MapMarkers.FirstMarkerId;
                    continue;
                }
                kept.Add(point);
            }

            if (removedCount == 0)
            {
                Jotunn.Logger.LogInfo($"[RoadMapper] Eraser used at ({x:F1}, {z:F1})... but there is nothing there to erase.");
                return;
            }

            WritePointsToFile(path, kept);
            Jotunn.Logger.LogInfo($"[RoadMapper] {(kind == LayerKind.Overfog ? "Admin eraser" : "Eraser")} removed {removedCount} marker(s) near ({x:F1}, {z:F1}).");
            OnLayerStrike(kind);
            if (removedMarker)
                BroadcastMarkerPins();
        }

        private void OnWorldSaveFinished()
        {
            if (!IsServer) return;

            // Unwritten strikes go out with the save, whatever dedup finds.
            if (_layerDirty) RequestLayerWrite("world save");

            DeduplicateFile(GetWorldDataFilePath(LayerKind.Underfog));
            DeduplicateFile(GetWorldDataFilePath(LayerKind.Overfog));
        }

        private void DeduplicateFile(string path)
        {
            List<MarkerPoint> loaded = LoadPointsFromFile(path);
            if (loaded.Count == 0) return;

            List<MarkerPoint> kept = new List<MarkerPoint>();
            foreach (MarkerPoint point in loaded)
            {
                bool isDuplicate = false;
                foreach (MarkerPoint existing in kept)
                {
                    if (existing.BrushId == point.BrushId && Distance(existing, point) <= _dedupDistance.Value)
                    {
                        isDuplicate = true;
                        break;
                    }
                }
                if (!isDuplicate) kept.Add(point);
            }

            WritePointsToFile(path, kept);
            Jotunn.Logger.LogInfo($"[RoadMapper] World save dedup ({Path.GetFileName(path)}): {loaded.Count} lines -> {kept.Count} after dedup.");
        }
    }

    // Which NomapPrinter custom layer a set of points belongs to.
    [Flags]
    internal enum LayerKind
    {
        None = 0,
        Underfog = 1,   // ordinary marks: shown only where the player has explored
        Overfog = 2,    // admin marks: shown everywhere
        Both = Underfog | Overfog
    }

    // Takes the place of the TerrainOp stripped from the marker pieces: the placed copy removes
    // itself straight away so nothing persists in the world. The build ghost is instantiated
    // with ZNetView.m_forceDisableInit set; in that case only this component goes, and the
    // ghost stays visible for aiming (same trick TerrainOp uses).
    internal class MarkerPieceCleanup : MonoBehaviour
    {
        private void Awake()
        {
            if (ZNetView.m_forceDisableInit)
                Destroy(this);
        }

        private void Start()
        {
            ZNetView nview = GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                if (nview.IsOwner())
                    nview.Destroy();
            }
            else
            {
                Destroy(gameObject);
            }
        }
    }
}
