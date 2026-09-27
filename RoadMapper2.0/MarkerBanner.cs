using System;
using UnityEngine;

namespace RoadMapper
{
    // The in-world stand-in for a map marker: a banner hanging off a 4 m log pole, with the
    // marker's icon on it. Built once per marker (the first time one is shown), kept as an
    // inactive template, and cloned for each marker near the player. Local only, like the road
    // flags: no network object, nothing saved, gone when the Roadmapper is put away.
    //
    // The banner image trick comes from BannerShare: vanilla banner UVs don't cover the whole
    // cloth, so the cloth mesh gets fresh UVs projected flat across its z/y bounds, and then any
    // image maps onto the whole banner. The mesh's vertex colours (the wind weighting) are kept,
    // so the cloth still sways in the wind even with every script stripped off.
    internal static class MarkerBanner
    {
        public const string PolePrefabName = "wood_pole_log_4";   // Log Pole 4 m
        public const string BannerPrefabName = "piece_banner09";   // the purple one
        private const string ClothShaderName = "Custom/Vegetation";

        private const float TopMargin = 0.15f;                     // gap between pole top and banner top (before scaling)
        private const float BannerScale = 0.5f;                    // whole thing, pole and banner: half size
        private const float PoleGirth = 0.5f;                      // pole thickness only (x/z), applied before layout
        // Fallback banner background if the banner's own colour can't be read.
        private static readonly Color32 ClothColour = new Color32(222, 208, 176, 255); // plain linen
        private const int CanvasWidth = 64;                        // banner texture width in pixels

        private static bool _missingLogged;

        // Returns null if anything needed is missing; the caller falls back to the wisp torch.
        public static GameObject Build(MarkerIcon icon, Transform inactiveParent)
        {
            ZNetScene scene = ZNetScene.instance;
            GameObject polePrefab = scene != null ? scene.GetPrefab(PolePrefabName) : null;
            GameObject bannerPrefab = scene != null ? scene.GetPrefab(BannerPrefabName) : null;
            if (polePrefab == null || bannerPrefab == null)
            {
                WarnOnce($"'{PolePrefabName}' or '{BannerPrefabName}' not found");
                return null;
            }

            GameObject root = new GameObject($"RoadMapper_MarkerBanner_{icon.Id}");
            root.transform.SetParent(inactiveParent, false);

            // --- Pole: stripped to its meshes, standing on the ground, centred on the point.
            GameObject pole = UnityEngine.Object.Instantiate(polePrefab, root.transform, false);
            pole.name = "Pole";
            pole.transform.localPosition = Vector3.zero;
            pole.transform.localRotation = Quaternion.identity;
            // Thinner pole, same height. Done before measuring, so the banner hangs against the
            // slimmer pole rather than floating where the old surface was.
            pole.transform.localScale = Vector3.Scale(pole.transform.localScale, new Vector3(PoleGirth, 1f, PoleGirth));
            RoadMapper.StripToVisuals(pole);

            if (!TryGetBounds(pole, root.transform, null, out Bounds poleBounds))
            {
                WarnOnce($"'{PolePrefabName}' has no meshes");
                UnityEngine.Object.Destroy(root);
                return null;
            }
            Vector3 poleShift = new Vector3(-poleBounds.center.x, -poleBounds.min.y, -poleBounds.center.z);
            pole.transform.localPosition = poleShift;
            poleBounds.center += poleShift;

            // --- Banner: the cloth plus its wooden hanging bar.
            GameObject banner = UnityEngine.Object.Instantiate(bannerPrefab, root.transform, false);
            banner.name = "Banner";
            banner.transform.localPosition = Vector3.zero;
            banner.transform.localRotation = Quaternion.identity;
            RoadMapper.StripToVisuals(banner);

            MeshRenderer cloth = null;
            foreach (MeshRenderer r in banner.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material m = r.sharedMaterial;
                if (m != null && m.shader != null && m.shader.name == ClothShaderName)
                {
                    cloth = r;
                    break;
                }
            }
            MeshFilter filter = cloth != null ? cloth.GetComponent<MeshFilter>() : null;
            if (filter == null || filter.sharedMesh == null)
            {
                WarnOnce($"no '{ClothShaderName}' cloth found on '{BannerPrefabName}'");
                UnityEngine.Object.Destroy(root);
                return null;
            }

            Mesh original = filter.sharedMesh;
            filter.sharedMesh = WithFlatUVs(original);

            // Background is the banner's own colour (averaged from its texture), so the purple
            // banner stays purple with the icon on it.
            Color32 background = AverageColour(cloth.sharedMaterial.GetTexture("_MainTex"), ClothColour);
            Material material = new Material(cloth.sharedMaterial);
            material.SetTexture("_MainTex", BuildBannerTexture(icon, original.bounds.size.z, original.bounds.size.y, background));
            cloth.sharedMaterial = material;

            // --- Hang it off the side of the pole like a flag: top (the bar) just under the pole top,
            // inner end against the pole. The cloth decides which way is "out": it's thin in one
            // horizontal direction and wide in the other. The whole banner, bar included, is what
            // gets lined up, so the bar end is what meets the pole.
            if (TryGetBounds(banner, root.transform, cloth, out Bounds clothBounds)
                && TryGetBounds(banner, root.transform, null, out Bounds bannerBounds))
            {
                Vector3 shift = Vector3.zero;
                shift.y = (poleBounds.max.y - TopMargin) - bannerBounds.max.y;
                if (clothBounds.size.z >= clothBounds.size.x)
                {
                    shift.z = poleBounds.max.z - bannerBounds.min.z;
                    shift.x = -clothBounds.center.x;
                }
                else
                {
                    shift.x = poleBounds.max.x - bannerBounds.min.x;
                    shift.z = -clothBounds.center.z;
                }
                banner.transform.localPosition += shift;
            }

            // Everything above is laid out at full size; scaling the root shrinks it all together.
            root.transform.localScale = Vector3.one * BannerScale;
            return root;
        }

        // Copy of the mesh with UVs projected flat across its z (u) and y (v) extent, as in BannerShare.
        private static Mesh WithFlatUVs(Mesh source)
        {
            Mesh mesh = new Mesh { name = source.name + "_RoadMapper" };
            Vector3[] vertices = source.vertices;
            mesh.vertices = vertices;
            mesh.normals = source.normals;
            mesh.tangents = source.tangents;
            mesh.colors = source.colors; // wind weighting
            mesh.subMeshCount = source.subMeshCount;
            for (int i = 0; i < source.subMeshCount; i++)
                mesh.SetTriangles(source.GetTriangles(i), i);

            Bounds b = source.bounds;
            float sizeZ = Mathf.Max(b.size.z, 0.0001f);
            float sizeY = Mathf.Max(b.size.y, 0.0001f);
            Vector2[] uv = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                uv[i] = new Vector2((vertices[i].z - b.min.z) / sizeZ, (vertices[i].y - b.min.y) / sizeY);
            mesh.uv = uv;
            mesh.RecalculateBounds();
            return mesh;
        }

        // Plain cloth with the icon blown up (whole-pixel steps, so pixel art stays crisp) in the
        // middle, a little above centre. The texture has the banner's own proportions so the
        // icon isn't stretched.
        private static Texture2D BuildBannerTexture(MarkerIcon icon, float widthMetres, float heightMetres, Color32 background)
        {
            int w = CanvasWidth;
            int h = Mathf.Clamp(Mathf.RoundToInt(w * heightMetres / Mathf.Max(widthMetres, 0.01f)), 16, 256);

            int scale = Math.Max(1, Math.Min((int)(w * 0.75f) / icon.Width, (int)(h * 0.6f) / icon.Height));
            int iw = icon.Width * scale, ih = icon.Height * scale;
            int left = (w - iw) / 2;
            int bottom = Mathf.Clamp((h - ih) / 2 + h / 10, 0, Math.Max(0, h - ih));

            Color32[] pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = background;

            for (int y = 0; y < ih; y++)
            {
                int cy = bottom + y;                                // canvas row, bottom-up
                if (cy < 0 || cy >= h) continue;
                int srcRow = icon.Height - 1 - y / scale;           // icon rows are stored top-down
                for (int x = 0; x < iw; x++)
                {
                    int cx = left + x;
                    if (cx < 0 || cx >= w) continue;
                    int s = (srcRow * icon.Width + x / scale) * 4;
                    int a = icon.Rgba[s + 3];
                    if (a == 0) continue;
                    Color32 bg = pixels[cy * w + cx];
                    pixels[cy * w + cx] = new Color32(
                        (byte)((icon.Rgba[s] * a + bg.r * (255 - a)) / 255),
                        (byte)((icon.Rgba[s + 1] * a + bg.g * (255 - a)) / 255),
                        (byte)((icon.Rgba[s + 2] * a + bg.b * (255 - a)) / 255),
                        255);
                }
            }

            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = $"RoadMapper_MarkerBanner_{icon.Id}",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        // Alpha-weighted average colour of any texture, readable or not (blitted down small and
        // read back). Returns the fallback if there's no texture or nothing opaque in it.
        private static Color32 AverageColour(Texture source, Color32 fallback)
        {
            if (source == null) return fallback;

            const int size = 32;
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            Texture2D small = new Texture2D(size, size, TextureFormat.RGBA32, false);
            small.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            Color32[] pixels = small.GetPixels32();
            UnityEngine.Object.Destroy(small);

            long r = 0, g = 0, b = 0, weight = 0;
            foreach (Color32 c in pixels)
            {
                r += c.r * c.a;
                g += c.g * c.a;
                b += c.b * c.a;
                weight += c.a;
            }
            if (weight == 0) return fallback;
            return new Color32((byte)(r / weight), (byte)(g / weight), (byte)(b / weight), 255);
        }

        // Bounds of the (enabled) meshes under go, in the given transform's space. Worked out from
        // the meshes rather than Renderer.bounds, which isn't reliable on inactive objects.
        private static bool TryGetBounds(GameObject go, Transform space, Renderer only, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            Matrix4x4 toSpace = space.worldToLocalMatrix;
            foreach (MeshRenderer r in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!r.enabled || (only != null && r != only)) continue;
                MeshFilter f = r.GetComponent<MeshFilter>();
                if (f == null || f.sharedMesh == null) continue;

                Matrix4x4 m = toSpace * r.transform.localToWorldMatrix;
                Bounds mb = f.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 p = m.MultiplyPoint3x4(corner);
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                    else bounds.Encapsulate(p);
                }
            }
            return any;
        }

        private static void WarnOnce(string problem)
        {
            if (_missingLogged) return;
            _missingLogged = true;
            Jotunn.Logger.LogWarning($"[RoadMapper] Marker banner: {problem}; showing the wisp torch placeholder instead.");
        }
    }
}
