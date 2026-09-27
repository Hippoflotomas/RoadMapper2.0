using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace RoadMapper
{
    // The Surveyor's look: the hammer clone's own mesh is hidden and a "bundle" goes in its place:
    // a tiny marker banner (plain purple, on its log pole) and a tiny wisp torch, side by side and
    // leaning slightly apart, like two surveyor's stakes carried together.
    //
    // Everything is sized and aimed from the hammer's own mesh: the bundle runs along the
    // hammer's long axis, from its handle end towards its head, slightly longer than the hammer.
    // So it sits in the hand wherever the hammer did, without hand-tuned offsets. The same object
    // is what you see when the tool is dropped on the ground.
    internal static class SurveyorModel
    {
        private const string WispPrefabName = "piece_groundtorch_mist";
        private static readonly Color WispColour = new Color(0.55f, 0.8f, 1f, 1f); // wisp blue

        private const float LengthFactor = 1.15f; // bundle length relative to the hammer it replaces
        private const float StakeSpacing = 0.03f; // distance of each pole from the grip line (bundle height = 1 here)
        private const float Splay = 6f;           // degrees each stake leans outward
        private const float BannerSpin = 0f;      // turn the banner around its own pole if the cloth sits awkwardly

        public static void Apply(GameObject itemPrefab)
        {
            // Dedicated server: nothing is ever drawn, and the texture work below needs a GPU.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return;

            try
            {
                Build(itemPrefab);
            }
            catch (Exception ex)
            {
                Jotunn.Logger.LogWarning($"[RoadMapper] Surveyor model not built ({ex.GetType().Name}: {ex.Message}); it keeps the hammer look.");
            }
        }

        private static void Build(GameObject itemPrefab)
        {
            // Held items show their "attach" child in the hand.
            Transform attach = itemPrefab.transform.Find("attach");
            if (attach == null) attach = itemPrefab.transform;

            if (!MarkerBanner.TryGetBounds(attach.gameObject, attach, null, out Bounds hammer))
            {
                Jotunn.Logger.LogWarning("[RoadMapper] Surveyor model: couldn't measure the hammer; keeping the hammer look.");
                return;
            }

            GameObject wispPrefab = MarkerBanner.FindPrefab(WispPrefabName);
            if (wispPrefab == null)
            {
                Jotunn.Logger.LogWarning($"[RoadMapper] Surveyor model: '{WispPrefabName}' not found; keeping the hammer look.");
                return;
            }

            // Assemble under an inactive holder so none of the vanilla scripts wake up.
            GameObject workshop = new GameObject("RoadMapper_SurveyorWorkshop");
            workshop.SetActive(false);
            try
            {
                GameObject bundle = new GameObject("SurveyorBundle");
                bundle.transform.SetParent(workshop.transform, false);

                GameObject banner = MarkerBanner.Build(null, bundle.transform, still: true);
                GameObject wisp = RoadMapper.BuildFlagTemplate(wispPrefab, WispColour, 0, 1f, bundle.transform);
                if (banner == null || wisp == null)
                {
                    Jotunn.Logger.LogWarning("[RoadMapper] Surveyor model: couldn't build the banner or wisp; keeping the hammer look.");
                    return;
                }

                // Both stakes to height 1, standing on y = 0, their POLES on the grip line (not the
                // middle of their shapes: the banner's cloth sticks out sideways, and centring on it
                // pushed the pole out the back of the hand). Then a pole's width apart, leaning apart.
                StandUp(banner, bundle.transform);
                StandUp(wisp, bundle.transform);
                banner.transform.localRotation = Quaternion.Euler(0f, BannerSpin, 0f) * banner.transform.localRotation;
                banner.transform.localPosition += new Vector3(-StakeSpacing, 0f, 0f);
                wisp.transform.localPosition += new Vector3(StakeSpacing, 0f, 0f);
                banner.transform.localRotation = Quaternion.Euler(0f, 0f, Splay) * banner.transform.localRotation;
                wisp.transform.localRotation = Quaternion.Euler(0f, 0f, -Splay) * wisp.transform.localRotation;

                // Aim along the hammer: long axis, pointing from the handle end to the head end
                // (the end further from the grip, which is the attach point's origin).
                int axis = hammer.size.x >= hammer.size.y
                    ? (hammer.size.x >= hammer.size.z ? 0 : 2)
                    : (hammer.size.y >= hammer.size.z ? 1 : 2);
                float headSign = Mathf.Abs(hammer.max[axis]) >= Mathf.Abs(hammer.min[axis]) ? 1f : -1f;
                Vector3 direction = Vector3.zero;
                direction[axis] = headSign;

                Vector3 buttEnd = hammer.center;
                buttEnd[axis] = headSign > 0f ? hammer.min[axis] : hammer.max[axis];

                // Hide the hammer, then put the bundle where it was.
                foreach (MeshRenderer r in attach.GetComponentsInChildren<MeshRenderer>(true))
                    r.enabled = false;

                bundle.transform.SetParent(attach, false);
                bundle.transform.localRotation = Quaternion.FromToRotation(Vector3.up, direction);
                bundle.transform.localScale = Vector3.one * (hammer.size[axis] * LengthFactor);
                bundle.transform.localPosition = buttEnd;
            }
            finally
            {
                UnityEngine.Object.Destroy(workshop);
            }
        }

        // Scale a part to height 1 and drop its base to y = 0. Sideways it stays on its own origin,
        // which for both parts is the centre of their pole (the marker banner centres its pole on
        // its origin; the torch's pivot is its post).
        private static void StandUp(GameObject part, Transform space)
        {
            if (!MarkerBanner.TryGetBounds(part, space, null, out Bounds b) || b.size.y <= 0f)
                return;

            part.transform.localScale *= 1f / b.size.y;
            MarkerBanner.TryGetBounds(part, space, null, out b);
            part.transform.localPosition = new Vector3(0f, part.transform.localPosition.y - b.min.y, 0f);
        }
    }
}
