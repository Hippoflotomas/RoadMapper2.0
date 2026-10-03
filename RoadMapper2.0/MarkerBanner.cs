using Jotunn.Managers;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RoadMapper
{
    // The in-world stand-in for a map marker: a banner hanging off a 4 m log pole, with the
    // marker's icon on it. Built once per marker (the first time one is shown), kept as an
    // inactive template, and cloned for each marker near the player. Local only, like the road
    // flags: no network object, nothing saved, gone when the Surveyor is put away.
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
        private const float BarTuck = 0.04f;                       // how far the bar end sinks into the pole (before scaling)
        // Fallback banner background if the banner's own colour can't be read.
        private static readonly Color32 ClothColour = new Color32(222, 208, 176, 255); // plain linen
        private const int CanvasWidth = 64;                        // banner texture width in pixels

        private static bool _missingLogged;

        // Vanilla prefab by name: from the live scene in game, or Jotunn's cache at the main menu
        // (where the Surveyor's model is built, before any ZNetScene exists).
        internal static GameObject FindPrefab(string name)
        {
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            return prefab != null ? prefab : PrefabManager.Cache.GetPrefab<GameObject>(name);
        }

        // Returns null if anything needed is missing; the caller falls back to the wisp torch.
        // icon null = a plain banner (used for the Surveyor's own model).
        // still = the Surveyor's hand-held banner: one LOD, and the cloth drawn with a plain shader so
        // it can't sway or bend (see StaticClothMaterial).
        // scale = overall size (default BannerScale, half size).
        // background = cloth colour behind the icon; default is the banner's own purple (admin markers use gold).
        public static GameObject Build(MarkerIcon icon, Transform inactiveParent, bool still = false, float? scale = null, Color32? background = null)
        {
            GameObject polePrefab = FindPrefab(PolePrefabName);
            GameObject bannerPrefab = FindPrefab(BannerPrefabName);
            if (polePrefab == null || bannerPrefab == null)
            {
                WarnOnce($"'{PolePrefabName}' or '{BannerPrefabName}' not found");
                return null;
            }

            GameObject root = new GameObject($"RoadMapper_MarkerBanner_{(icon != null ? icon.Id.ToString() : "Plain")}");
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
            if (still) CollapseLods(pole);

            if (!TryGetBounds(pole, root.transform, null, out Bounds poleBounds, visibleOnly: true))
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
            if (still) CollapseLods(banner);

            MeshRenderer cloth = null;
            foreach (MeshRenderer r in banner.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!r.enabled) continue;
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
            filter.sharedMesh = WithFlatUVs(original, doubleSided: still);

            // Background is the banner's own colour (averaged from its texture), so the purple
            // banner stays purple with the icon on it.
            Color32 clothColour = background ?? AverageColour(cloth.sharedMaterial.GetTexture("_MainTex"), ClothColour);
            Texture2D bannerTexture = BuildBannerTexture(icon, original.bounds.size.z, original.bounds.size.y, clothColour);
            Material material = still ? StaticClothMaterial(banner, pole, cloth, bannerTexture) : null;
            if (material == null)
            {
                material = new Material(cloth.sharedMaterial);
                material.SetTexture("_MainTex", bannerTexture);
                if (still)
                    FreezeWind(material);
            }
            cloth.sharedMaterial = material;
            if (still)
            {
                RigidifyRest(banner, pole, cloth);
                ReplacePole(root.transform, pole, poleBounds, FindRigidMaterial(banner, pole, cloth));
            }

            // --- Hang it off the side of the pole like a flag: top (the bar) just under the pole top,
            // inner end against the pole. The cloth decides which way is "out": it's thin in one
            // horizontal direction and wide in the other. The whole banner, bar included, is what
            // gets lined up, so the bar end is what meets the pole.
            if (TryGetBounds(banner, root.transform, cloth, out Bounds clothBounds, visibleOnly: true)
                && TryGetBounds(banner, root.transform, null, out Bounds bannerBounds, visibleOnly: true))
            {
                // The bar end goes a little INTO the pole, so no sliver of daylight shows between them.
                Vector3 shift = Vector3.zero;
                shift.y = (poleBounds.max.y - TopMargin) - bannerBounds.max.y;
                if (clothBounds.size.z >= clothBounds.size.x)
                {
                    shift.z = poleBounds.max.z - BarTuck - bannerBounds.min.z;
                    shift.x = -clothBounds.center.x;
                }
                else
                {
                    shift.x = poleBounds.max.x - BarTuck - bannerBounds.min.x;
                    shift.z = -clothBounds.center.z;
                }
                Jotunn.Logger.LogDebug($"[RoadMapper] Banner layout: pole {poleBounds.size.x:0.00} x {poleBounds.size.z:0.00} m, banner {bannerBounds.size.x:0.00} x {bannerBounds.size.z:0.00} m.");
                banner.transform.localPosition += shift;
            }

            // Everything above is laid out at full size; scaling the root shrinks it all together.
            root.transform.localScale = Vector3.one * (scale ?? BannerScale);
            return root;
        }

        // The held Surveyor's banner can't use the cloth's own shader (Custom/Vegetation): it moves
        // vertices on the GPU by fixed world-space amounts (wind, bending), which on a model shrunk
        // to hand size tears the cloth off its pole, and changes as the player walks because it
        // depends on world position. So it gets a copy of a plain wooden material from the same
        // banner (its hanging bar) or the pole, which leaves vertices where the mesh puts them,
        // with the banner image swapped in. Null if neither has one; the caller then falls back.
        private static Material StaticClothMaterial(GameObject banner, GameObject pole, MeshRenderer cloth, Texture2D texture)
        {
            Material source = FindRigidMaterial(banner, pole, cloth);
            if (source == null)
            {
                Jotunn.Logger.LogWarning("[RoadMapper] Surveyor banner: no plain material to borrow; using the cloth shader.");
                return null;
            }

            Material material = new Material(source) { name = "RoadMapper_SurveyorCloth" };
            material.SetTexture("_MainTex", texture);
            material.SetTextureScale("_MainTex", Vector2.one);
            material.SetTextureOffset("_MainTex", Vector2.zero);
            // Drop the wood's own detail maps so they don't show through the banner image.
            foreach (string map in new[] { "_BumpMap", "_MetallicGlossMap", "_OcclusionMap", "_EmissionMap", "_DetailAlbedoMap", "_DetailNormalMap" })
                if (material.HasProperty(map)) material.SetTexture(map, null);
            material.DisableKeyword("_NORMALMAP");
            material.DisableKeyword("_EMISSION");
            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f); // the mesh is made double-sided anyway

            Jotunn.Logger.LogInfo($"[RoadMapper] Surveyor banner: cloth drawn with '{source.shader.name}' (from '{source.name}') instead of '{ClothShaderName}'.");
            return material;
        }

        // The other parts of the hand-held banner (the log pole, which comes from tree-log art, and
        // anything else) may use the same vertex-moving shader as the cloth, which makes them drift
        // apart while the player walks. Swap each such material for a copy of a rigid one, keeping
        // its own texture. Logs every part's shader once, so what's left can be checked.
        private static void RigidifyRest(GameObject banner, GameObject pole, MeshRenderer cloth)
        {
            Material source = FindRigidMaterial(banner, pole, cloth);
            List<string> report = new List<string>();
            foreach (GameObject part in new[] { pole, banner })
            {
                foreach (MeshRenderer r in part.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!r.enabled || r == cloth) continue;
                    Material[] materials = r.sharedMaterials;
                    for (int i = 0; i < materials.Length; i++)
                    {
                        Material m = materials[i];
                        if (m == null || m.shader == null) continue;
                        if (m.shader.name != ClothShaderName || source == null)
                        {
                            report.Add($"{part.name}/{r.name}: {m.shader.name}");
                            continue;
                        }
                        Material rigid = new Material(source) { name = m.name + "_RoadMapperRigid" };
                        if (m.HasProperty("_MainTex"))
                        {
                            rigid.SetTexture("_MainTex", m.GetTexture("_MainTex"));
                            rigid.SetTextureScale("_MainTex", m.GetTextureScale("_MainTex"));
                            rigid.SetTextureOffset("_MainTex", m.GetTextureOffset("_MainTex"));
                        }
                        if (rigid.HasProperty("_BumpMap"))
                            rigid.SetTexture("_BumpMap", m.HasProperty("_BumpMap") ? m.GetTexture("_BumpMap") : null);
                        if (rigid.HasProperty("_Color"))
                            rigid.SetColor("_Color", m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white);
                        materials[i] = rigid;
                        report.Add($"{part.name}/{r.name}: {m.shader.name} -> {source.shader.name}");
                    }
                    r.sharedMaterials = materials;
                }
            }
            Jotunn.Logger.LogInfo($"[RoadMapper] Surveyor banner parts: {string.Join("; ", report)}");
        }

        // The hand-held banner swaps the vanilla log pole for a plain cylinder of the same size. The
        // vanilla prefab carries a snow cap with its own shader and two copies of its meshes (the
        // new and worn states WearNTear normally switches between), and still showed the pole
        // moving on its own in the hand. A cylinder with the same wooden material as the
        // banner's bar has none of that. Placed markers keep the real pole.
        private static void ReplacePole(Transform root, GameObject pole, Bounds poleBounds, Material material)
        {
            if (material == null)
                return;

            GameObject cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder); // 1 wide, 2 tall, centred
            cylinder.name = "PoleSimple";
            RoadMapper.StripToVisuals(cylinder); // drops the collider
            cylinder.transform.SetParent(root, false);
            cylinder.transform.localPosition = poleBounds.center;
            cylinder.transform.localRotation = Quaternion.identity;
            cylinder.transform.localScale = new Vector3(poleBounds.size.x, poleBounds.size.y * 0.5f, poleBounds.size.z);
            cylinder.GetComponent<MeshRenderer>().sharedMaterial = material;

            // Immediate, not Destroy: the Surveyor measures the bundle again in this same frame.
            UnityEngine.Object.DestroyImmediate(pole);
            Jotunn.Logger.LogInfo($"[RoadMapper] Surveyor banner: pole replaced with a plain cylinder ({poleBounds.size.x:0.00} x {poleBounds.size.y:0.00} m before scaling).");
        }

        private static Material FindRigidMaterial(GameObject banner, GameObject pole, MeshRenderer cloth)
        {
            foreach (GameObject part in new[] { banner, pole })
                foreach (MeshRenderer r in part.GetComponentsInChildren<MeshRenderer>(true))
                {
                    Material m = r.sharedMaterial;
                    if (r == cloth || !r.enabled || m == null || m.shader == null || m.shader.name == ClothShaderName)
                        continue;
                    return m;
                }
            return null;
        }

        // Keep only each LODGroup's most detailed level and remove the group, so a model that is
        // shrunk and carried never swaps meshes (only the LOD0 cloth gets the banner image).
        internal static void CollapseLods(GameObject root)
        {
            foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true))
            {
                LOD[] lods = group.GetLODs();
                if (lods.Length > 1)
                {
                    HashSet<Renderer> keep = new HashSet<Renderer>();
                    foreach (Renderer r in lods[0].renderers)
                        if (r != null) keep.Add(r);
                    for (int i = 1; i < lods.Length; i++)
                        foreach (Renderer r in lods[i].renderers)
                            if (r != null && !keep.Contains(r)) r.enabled = false;
                }
                UnityEngine.Object.DestroyImmediate(group);
            }
        }

        // Copy of the mesh with UVs projected flat across its z (u) and y (v) extent, as in BannerShare.
        // doubleSided adds a back face for every triangle (flipped normals), for shaders that cull
        // back faces; the bounds are unchanged, so the layout is the same either way.
        private static Mesh WithFlatUVs(Mesh source, bool doubleSided = false)
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

            if (doubleSided)
            {
                int n = vertices.Length;
                Vector3[] normals = source.normals;
                Vector4[] tangents = source.tangents;
                Vector3[] v2 = new Vector3[n * 2];
                Vector2[] uv2 = new Vector2[n * 2];
                Vector3[] n2 = normals.Length == n ? new Vector3[n * 2] : null;
                Vector4[] t2 = tangents.Length == n ? new Vector4[n * 2] : null;
                for (int i = 0; i < n; i++)
                {
                    v2[i] = v2[i + n] = vertices[i];
                    uv2[i] = uv2[i + n] = uv[i];
                    if (n2 != null) { n2[i] = normals[i]; n2[i + n] = -normals[i]; }
                    if (t2 != null) { t2[i] = tangents[i]; t2[i + n] = new Vector4(-tangents[i].x, -tangents[i].y, -tangents[i].z, tangents[i].w); }
                }

                Mesh both = new Mesh { name = mesh.name };
                both.vertices = v2;
                both.uv = uv2;
                if (n2 != null) both.normals = n2;
                if (t2 != null) both.tangents = t2;
                // No vertex colours: they're the cloth shader's wind weights and mean nothing (or a
                // tint) to other shaders.
                both.subMeshCount = source.subMeshCount;
                for (int s = 0; s < source.subMeshCount; s++)
                {
                    int[] front = source.GetTriangles(s);
                    int[] tris = new int[front.Length * 2];
                    for (int i = 0; i < front.Length; i += 3)
                    {
                        tris[i] = front[i]; tris[i + 1] = front[i + 1]; tris[i + 2] = front[i + 2];
                        int j = front.Length + i;
                        tris[j] = front[i] + n; tris[j + 1] = front[i + 2] + n; tris[j + 2] = front[i + 1] + n;
                    }
                    both.SetTriangles(tris, s);
                }
                if (n2 == null) both.RecalculateNormals();
                both.RecalculateBounds();
                return both;
            }

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

            Color32[] pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = background;

            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = $"RoadMapper_MarkerBanner_{(icon != null ? icon.Id.ToString() : "Plain")}",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            if (icon == null)
            {
                tex.SetPixels32(pixels);
                tex.Apply(false);
                return tex;
            }

            int scale = Math.Max(1, Math.Min((int)(w * 0.75f) / icon.Width, (int)(h * 0.6f) / icon.Height));
            int iw = icon.Width * scale, ih = icon.Height * scale;
            int left = (w - iw) / 2;
            int bottom = Mathf.Clamp((h - ih) / 2 + h / 10, 0, Math.Max(0, h - ih));

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

            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        // The cloth's sway is done in its shader (Custom/Vegetation), not by a component, so stripping
        // components doesn't stop it. Zero every float/range property that looks like a wind
        // control. The names aren't documented, so this logs what it zeroed, or, if it found
        // nothing, the shader's full property list so the right one can be picked out.
        private static void FreezeWind(Material material)
        {
            Shader shader = material.shader;
            List<string> zeroed = new List<string>();
            List<string> all = new List<string>();
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                ShaderPropertyType type = shader.GetPropertyType(i);
                all.Add($"{name} ({type})");
                if (type != ShaderPropertyType.Float && type != ShaderPropertyType.Range)
                    continue;
                string lower = name.ToLowerInvariant();
                if (lower.Contains("wind") || lower.Contains("sway") || lower.Contains("bend")
                    || lower.Contains("wave") || lower.Contains("flutter") || lower.Contains("wobble"))
                {
                    material.SetFloat(name, 0f);
                    zeroed.Add(name);
                }
            }

            if (zeroed.Count > 0)
                Jotunn.Logger.LogInfo($"[RoadMapper] Surveyor banner: wind switched off ({string.Join(", ", zeroed)}).");
            else
                Jotunn.Logger.LogWarning($"[RoadMapper] Surveyor banner: no wind setting found on '{shader.name}'. Its properties: {string.Join(", ", all)}");
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
        // visibleOnly (for lining parts up against each other): skip meshes switched off inside go
        // (spare worn/broken states) and snow caps, and use the real vertices where the mesh is
        // readable, since a rotated mesh's box corners stick out past the mesh itself.
        internal static bool TryGetBounds(GameObject go, Transform space, Renderer only, out Bounds bounds, bool visibleOnly = false)
        {
            bounds = default;
            bool any = false;
            Matrix4x4 toSpace = space.worldToLocalMatrix;
            foreach (MeshRenderer r in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!r.enabled || (only != null && r != only)) continue;
                MeshFilter f = r.GetComponent<MeshFilter>();
                if (f == null || f.sharedMesh == null) continue;
                if (visibleOnly && (!ActiveWithin(r.transform, go.transform)
                    || (r.sharedMaterial != null && r.sharedMaterial.shader != null && r.sharedMaterial.shader.name.IndexOf("Snow", StringComparison.OrdinalIgnoreCase) >= 0)))
                    continue;

                Matrix4x4 m = toSpace * r.transform.localToWorldMatrix;
                if (visibleOnly && f.sharedMesh.isReadable)
                {
                    foreach (Vector3 v in f.sharedMesh.vertices)
                    {
                        Vector3 p = m.MultiplyPoint3x4(v);
                        if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                        else bounds.Encapsulate(p);
                    }
                    continue;
                }
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

        // True if t and every parent up to (and including) root are switched on. Parents above root
        // (the inactive template holders) don't count.
        private static bool ActiveWithin(Transform t, Transform root)
        {
            for (Transform c = t; c != null; c = c.parent)
            {
                if (!c.gameObject.activeSelf) return false;
                if (c == root) return true;
            }
            return true;
        }

        private static void WarnOnce(string problem)
        {
            if (_missingLogged) return;
            _missingLogged = true;
            Jotunn.Logger.LogWarning($"[RoadMapper] Marker banner: {problem}; showing the wisp torch placeholder instead.");
        }
    }
}
