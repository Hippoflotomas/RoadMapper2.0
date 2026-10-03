using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace RoadMapper
{
    // Removes a vanilla death pin once its gravestone is gone. Client side only: death pins live
    // in the character's own map data, not on the server.
    //
    // A death pin has no link to a particular gravestone, so they're matched by position. One
    // check covers both cases:
    //   - you empty your own grave: it despawns while you're standing there;
    //   - someone else looted it (or it's gone for any other reason): you walk past the pin.
    // Either way, when you're near a death pin, its area has finished loading, and no gravestone
    // is anywhere near it for a few checks in a row, the pin is removed.
    internal static class DeathPins
    {
        // Only pins this close to the player are judged, so their area is certainly loaded.
        private const float CheckRadius = 20f;
        // A grave this close to a pin counts as that pin's grave. Generous, because gravestones
        // can slide down slopes or float off in water. Any grave counts, not just yours: when
        // unsure, the pin stays.
        private const float MatchRadius = 30f;
        private const float CheckInterval = 1f;  // seconds between checks
        private const int ChecksBeforeRemoving = 3; // consecutive "no grave" checks needed

        // Gravestones currently loaded, refreshed each check (once a second, so the search is cheap).
        private static TombStone[] _graves = new TombStone[0];
        private static readonly Dictionary<Minimap.PinData, int> Missing = new Dictionary<Minimap.PinData, int>();
        private static readonly List<Minimap.PinData> ToRemove = new List<Minimap.PinData>();
        private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> PinsRef =
            AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");

        private static float _nextCheck;

        public static void Update(bool enabled)
        {
            if (!enabled || Minimap.instance == null || Player.m_localPlayer == null || ZNetScene.instance == null)
            {
                Missing.Clear();
                return;
            }
            if (Time.time < _nextCheck)
                return;
            _nextCheck = Time.time + CheckInterval;

            _graves = Object.FindObjectsOfType<TombStone>();
            Vector3 player = Player.m_localPlayer.transform.position;

            ToRemove.Clear();
            List<Minimap.PinData> pins = PinsRef(Minimap.instance);
            foreach (Minimap.PinData pin in pins)
            {
                if (pin.m_type != Minimap.PinType.Death)
                    continue;

                if (DistanceXZ(pin.m_pos, player) > CheckRadius
                    || !ZNetScene.instance.IsAreaReady(pin.m_pos)
                    || GraveNear(pin.m_pos))
                {
                    Missing.Remove(pin);
                    continue;
                }

                Missing.TryGetValue(pin, out int count);
                if (++count >= ChecksBeforeRemoving)
                    ToRemove.Add(pin);
                else
                    Missing[pin] = count;
            }

            foreach (Minimap.PinData pin in ToRemove)
            {
                Missing.Remove(pin);
                Minimap.instance.RemovePin(pin);
                Jotunn.Logger.LogInfo($"[RoadMapper] Removed death pin at ({pin.m_pos.x:0}, {pin.m_pos.z:0}): its gravestone is gone.");
            }

            // Forget pins that were removed some other way.
            if (Missing.Count > 0)
            {
                ToRemove.Clear();
                foreach (Minimap.PinData pin in Missing.Keys)
                    if (!pins.Contains(pin)) ToRemove.Add(pin);
                foreach (Minimap.PinData pin in ToRemove)
                    Missing.Remove(pin);
            }
        }

        private static bool GraveNear(Vector3 pos)
        {
            foreach (TombStone grave in _graves)
                if (grave != null && DistanceXZ(grave.transform.position, pos) <= MatchRadius)
                    return true;
            return false;
        }

        private static float DistanceXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
