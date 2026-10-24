using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vec2 = System.Numerics.Vector2;
using Vec3 = System.Numerics.Vector3;

namespace SVS_VirginBlood
{
    /// <summary>
    /// Paints blood into a texture laid out on the body mesh's UVs, so it can be drawn
    /// over the skin as an extra material. Positions come from the mesh's bind pose:
    /// a blob around the crotch bone, and streak images wrapped down the inner thighs.
    /// </summary>
    /// <remarks>
    /// Unity's math types go through the IL2CPP runtime on every call, so the painting
    /// loops use System.Numerics and plain arrays instead.
    /// </remarks>
    internal static class BodyOverlay
    {
        private const int TexSize = 2048;
        private const string BodyRendererName = "o_body";
        // Narrowest a drip is drawn, as a fraction of thigh length (about 2-3 mm)
        private const float MinDripWidth = 0.006f;

        private class StreakImage
        {
            public int Width;
            public int Height;
            public float[] Alpha;
            public float[] Shade;
            // Lowest point of each separate drip, in image space (v = 0 at the top), with Z the drip's
            // width just above it as a fraction of the image width
            public List<Vec3> Tips;
        }

        // A round drop of blood on a streak, like a bead of sweat, in the streak's image space
        private struct Bead
        {
            public float U;
            public float V;
            public float Radius;
        }

        // Everything read from the body mesh, which is slow through IL2CPP and the same on every re-roll
        private class MeshData
        {
            public int VertexCount;
            public Vec3[] Verts;
            public Vec2[] Uvs;
            public int[] Tris;
            public Dictionary<string, Vec3> BonePositions;
            public Dictionary<string, float[]> LegMembership;
        }

        private static List<StreakImage> _streaks;
        private static readonly Dictionary<IntPtr, MeshData> _meshCache = new Dictionary<IntPtr, MeshData>();

        internal static SkinnedMeshRenderer FindBody(Transform root)
        {
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr != null && smr.name == BodyRendererName)
                    return smr;
            }
            return null;
        }

        private static MeshData GetMeshData(SkinnedMeshRenderer body)
        {
            var mesh = body.sharedMesh;
            if (mesh == null) return null;
            var vertexCount = mesh.vertexCount;
            if (_meshCache.TryGetValue(mesh.Pointer, out var cached) && cached.VertexCount == vertexCount)
                return cached;

            var verts = mesh.vertices;
            var uvs = mesh.uv;
            var tris = mesh.triangles;
            var weights = mesh.boneWeights;
            var bindposes = mesh.bindposes;
            var bones = body.bones;

            var data = new MeshData
            {
                VertexCount = vertexCount,
                Verts = new Vec3[verts.Length],
                Uvs = new Vec2[verts.Length],
                Tris = new int[tris.Length],
                BonePositions = new Dictionary<string, Vec3>(),
                LegMembership = new Dictionary<string, float[]>(),
            };
            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                data.Verts[i] = new Vec3(v.x, v.y, v.z);
            }
            for (int i = 0; i < uvs.Length && i < verts.Length; i++)
            {
                var uv = uvs[i];
                data.Uvs[i] = new Vec2(uv.x, uv.y);
            }
            for (int i = 0; i < tris.Length; i++)
                data.Tris[i] = tris[i];

            var boneNames = new string[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                boneNames[i] = bone != null ? bone.name : "";
                if (i < bindposes.Length && boneNames[i] != "" && !data.BonePositions.ContainsKey(boneNames[i]))
                {
                    var p = bindposes[i].inverse.MultiplyPoint3x4(Vector3.zero);
                    data.BonePositions[boneNames[i]] = new Vec3(p.x, p.y, p.z);
                }
            }

            // How much each vertex belongs to each leg, so a streak never lands on the other leg
            var count = Math.Min(verts.Length, weights.Length);
            var boneIndex = new int[count * 4];
            var boneWeight = new float[count * 4];
            for (int v = 0; v < count; v++)
            {
                var w = weights[v];
                boneIndex[v * 4] = w.boneIndex0; boneWeight[v * 4] = w.weight0;
                boneIndex[v * 4 + 1] = w.boneIndex1; boneWeight[v * 4 + 1] = w.weight1;
                boneIndex[v * 4 + 2] = w.boneIndex2; boneWeight[v * 4 + 2] = w.weight2;
                boneIndex[v * 4 + 3] = w.boneIndex3; boneWeight[v * 4 + 3] = w.weight3;
            }
            foreach (var side in new[] { "L", "R" })
            {
                var suffix = "_" + side;
                var isLegBone = boneNames.Select(n => n.EndsWith(suffix) &&
                    (n.Contains("thigh") || n.Contains("leg") || n.Contains("knee") || n.Contains("hipleg"))).ToArray();
                var m = new float[verts.Length];
                for (int v = 0; v < count; v++)
                {
                    var sum = 0f;
                    for (int j = v * 4; j < v * 4 + 4; j++)
                    {
                        var b = boneIndex[j];
                        if (b >= 0 && b < isLegBone.Length && isLegBone[b]) sum += boneWeight[j];
                    }
                    m[v] = sum;
                }
                data.LegMembership[side] = m;
            }

            if (_meshCache.Count > 8) _meshCache.Clear();
            _meshCache[mesh.Pointer] = data;
            return data;
        }

        // The mask keeps shade in R and coverage in A, so the colour can be changed later without repainting
        // position: 0 centres the blood on the crotch bone at the top of the slit, 1 on the anus bone.
        // progress: how far the drips have run, 0 to 1
        internal static BloodTexture GenerateTexture(SkinnedMeshRenderer body, int seed, bool drips, float spread, float streakLength,
            float position, Color color, float progress)
        {
            var started = DateTime.Now;
            var data = GetMeshData(body);
            if (data == null) return null;
            var meshMs = (DateTime.Now - started).TotalMilliseconds;

            if (!data.BonePositions.TryGetValue("cf_j_kokan", out var kokan))
            {
                VirginBloodPlugin.Log.LogWarning("Body has no cf_j_kokan bone, cannot place blood");
                return null;
            }

            var rng = new System.Random(seed);
            float Rand(float min, float max) => min + (float)rng.NextDouble() * (max - min);

            var legs = new List<Leg>();
            foreach (var side in new[] { "L", "R" })
            {
                if (!data.BonePositions.TryGetValue($"cf_s_thigh01_{side}", out var top)
                    || !data.BonePositions.TryGetValue($"cf_s_leg01_{side}", out var knee))
                {
                    VirginBloodPlugin.Log.LogWarning($"Body is missing thigh bones for side {side}");
                    continue;
                }
                legs.Add(new Leg(side, top, knee, kokan, data.LegMembership[side]));
            }

            // Thigh length sets the scale, so the blood follows body sliders
            var scale = legs.Count > 0 ? legs.Average(l => l.Length) : 0.4f;

            // The crotch bone sits at the front top of the slit, which runs back underneath toward the anus.
            // The opening is part way along it
            Vec3 k, slitDir;
            if (data.BonePositions.TryGetValue("cf_s_ana", out var ana) || data.BonePositions.TryGetValue("cf_j_ana", out ana))
            {
                k = Vec3.Lerp(kokan, ana, position);
                slitDir = Vec3.Normalize(ana - kokan);
            }
            else
            {
                // Without an anus bone, assume the slit runs back and a little down
                slitDir = Vec3.Normalize(new Vec3(0f, -0.3f, -1f));
                k = kokan + slitDir * 0.16f * scale * position;
            }
            var blobRadius = 0.05f * scale * spread;

            // Streaks run down the part of each thigh facing the opening, not the front of the groin
            legs = legs.Select(l => new Leg(l.Side, l.Top, l.Top + l.Dir * l.Length, k, l.Membership)).ToList();

            // Blood gathers at the opening and runs down, so shapes are narrow and biased downward.
            // Each sphere has its own stretch: x is across the body, y is up
            var centers = new List<Vec3>();
            var radii = new List<float>();
            var stretches = new List<Vec3>();
            void AddSphere(Vec3 center, float radius, float width, float height)
            {
                centers.Add(center);
                radii.Add(radius);
                // Front-to-back counts as height too, since the slit curves from facing forward to facing down
                stretches.Add(new Vec3(1f / width, 1f / height, 1f / height));
            }

            // Core: a narrow patch along the slit around the opening, reaching further back than forward
            var coreCount = rng.Next(3, 6);
            for (int i = 0; i < coreCount; i++)
            {
                AddSphere(k + slitDir * Rand(-0.5f, 0.7f) * blobRadius
                    + new Vec3(Rand(-0.15f, 0.15f) * blobRadius, Rand(-0.2f, 0.05f) * blobRadius, 0f),
                    Rand(0.35f, 0.7f) * blobRadius, 0.55f, 1.1f);
            }

            // Smears: short tapering trails running down and out toward the inner thighs
            var smearCount = rng.Next(0, 3);
            for (int i = 0; i < smearCount; i++)
            {
                var side = rng.Next(2) == 0 ? -1f : 1f;
                var dir = Vec3.Normalize(new Vec3(side * Rand(0.3f, 0.9f), -1f, Rand(-0.3f, 0.3f)));
                var start = new Vec3(k.X + side * 0.2f * blobRadius, k.Y - Rand(0.2f, 0.6f) * blobRadius, k.Z);
                var steps = rng.Next(3, 7);
                for (int j = 0; j < steps; j++)
                {
                    var f = (float)j / steps;
                    AddSphere(start + dir * f * Rand(1.2f, 1.8f) * blobRadius,
                        Lerp(0.3f, 0.08f, f) * blobRadius, 0.7f, 1f);
                }
            }

            // Droplets: a few small spots, mostly below
            var dropletCount = rng.Next(1, 5);
            for (int i = 0; i < dropletCount; i++)
            {
                var dir = Vec3.Normalize(new Vec3(Rand(-1f, 1f), Rand(-1f, 0.2f), Rand(-0.6f, 0.6f)));
                AddSphere(k + dir * Rand(0.8f, 1.8f) * blobRadius, Rand(0.04f, 0.1f) * blobRadius, 1f, 1f);
            }

            // Streaks: a random KK streak image on each leg that gets one
            var streakList = new List<Streak>();
            if (drips)
            {
                var images = LoadStreaks();
                // A little blood goes a long way: at most one streak per leg, sometimes none, but never none at all
                var bleeding = legs.Select(_ => rng.NextDouble() < 0.5).ToArray();
                if (legs.Count > 0 && !bleeding.Any(b => b))
                    bleeding[rng.Next(legs.Count)] = true;
                for (int l = 0; l < legs.Count; l++)
                {
                    var leg = legs[l];
                    if (!bleeding[l] || images.Count == 0) continue;
                    {
                        var image = images[rng.Next(images.Count)];
                        var length = leg.Length * 0.35f * streakLength * Rand(0.7f, 1.1f);
                        // Narrower than the image, so the drips stay thin instead of fanning across the thigh
                        var width = MathF.Min(length * image.Width / image.Height * 0.3f, leg.Length * 0.08f);
                        // Thinner drips than this would vanish on the body and leave their beads floating
                        var minDrip = leg.Length * MinDripWidth;

                        // Beads gather at the drip tips, with the odd one partway down a drip.
                        // A drop is only a little wider than the drip it sits on
                        var beads = new List<Bead>();
                        foreach (var tip in image.Tips)
                        {
                            if (rng.NextDouble() > 0.5) continue;
                            var drip = MathF.Max(tip.Z * width, minDrip);
                            var radius = Math.Clamp(drip * 0.65f, leg.Length * 0.005f, leg.Length * 0.015f) * Rand(0.9f, 1.15f);
                            // Centred a full radius up from the tip, so it overlaps the end of the drip
                            // (the thin last bit of a drip can vanish on the body, leaving a gap otherwise)
                            beads.Add(new Bead { U = tip.X, V = tip.Y - radius / length, Radius = radius });
                            if (rng.NextDouble() < 0.15)
                            {
                                var v = tip.Y * Rand(0.3f, 0.8f);
                                SampleImage(image, tip.X, v, out var a, out _);
                                if (a > 0.8f)
                                    beads.Add(new Bead { U = tip.X, V = v, Radius = radius * Rand(0.5f, 0.75f) });
                            }
                        }

                        streakList.Add(new Streak
                        {
                            Beads = beads.ToArray(),
                            Leg = leg,
                            Image = image,
                            Length = length,
                            Width = width,
                            Widen = minDrip * 0.5f / width,
                            Center = Rand(-0.25f, 0.25f) * leg.Length * 0.1f,
                            // Blood leaves the opening, so streaks start at its level or just below
                            Start = leg.Along(k) + Rand(0f, 0.06f) * leg.Length,
                            // Each drip sets off a little later and runs at its own speed
                            Delay = Rand(0f, 0.15f),
                            Duration = Rand(0.6f, 0.85f),
                            Mirror = leg.Side == "R",
                        });
                    }
                }
            }
            var streaks = streakList.ToArray();

            // Joins each streak to the stain: a tapering smear from the opening to where the streak starts on the thigh
            foreach (var streak in streaks)
            {
                if (!StreakTop(data, streak, out var top)) continue;
                const int steps = 8;
                for (int j = 0; j <= steps; j++)
                {
                    var f = (float)j / steps;
                    AddSphere(Vec3.Lerp(k, top, f), Lerp(0.32f, 0.14f, f) * blobRadius, 0.8f, 1f);
                }
            }

            var sphereCenters = centers.ToArray();
            var sphereRadii = radii.ToArray();
            var sphereStretch = stretches.ToArray();
            // Every sphere lies within this distance of the crotch, so pixels further out skip them
            var blobReach = 0f;
            for (int i = 0; i < sphereCenters.Length; i++)
                blobReach = MathF.Max(blobReach, Vec3.Distance(sphereCenters[i], k) + sphereRadii[i] * 1.2f * 1.1f);

            var verts = data.Verts;
            var uvs = data.Uvs;
            var tris = data.Tris;
            var noiseSeed = rng.Next();
            var noiseScale = 1f / (0.18f * blobRadius);
            var nearBlob = MathF.Max(blobRadius * 2.5f, blobReach);
            var colors = new Color32[TexSize * TexSize];
            // How far the blood stands off the skin, for the normal map that makes drops catch the light
            var heights = new float[TexSize * TexSize];
            var memA = new float[streaks.Length];
            var memB = new float[streaks.Length];
            var memC = new float[streaks.Length];

            bool NearBlood(Vec3 p)
            {
                if (Vec3.Distance(p, k) < nearBlob) return true;
                foreach (var s in streaks)
                    if (s.Covers(p)) return true;
                return false;
            }

            var paintStarted = DateTime.Now;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                var va = verts[a];
                var vb = verts[b];
                var vc = verts[c];
                if (!NearBlood(va) && !NearBlood(vb) && !NearBlood(vc))
                    continue;

                var p0 = uvs[a] * TexSize;
                var p1 = uvs[b] * TexSize;
                var p2 = uvs[c] * TexSize;
                var area = Edge(p0, p1, p2);
                if (MathF.Abs(area) < 1e-6f) continue;
                var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
                var maxX = Math.Min(TexSize - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
                var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
                var maxY = Math.Min(TexSize - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));

                for (int s = 0; s < streaks.Length; s++)
                {
                    var m = streaks[s].Leg.Membership;
                    memA[s] = a < m.Length ? m[a] : 0f;
                    memB[s] = b < m.Length ? m[b] : 0f;
                    memC[s] = c < m.Length ? m[c] : 0f;
                }

                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        var px = new Vec2(x + 0.5f, y + 0.5f);
                        var w0 = Edge(p1, p2, px) / area;
                        var w1 = Edge(p2, p0, px) / area;
                        var w2 = Edge(p0, p1, px) / area;
                        // Small tolerance closes hairline gaps along UV seams
                        if (w0 < -0.02f || w1 < -0.02f || w2 < -0.02f) continue;

                        var pos = va * w0 + vb * w1 + vc * w2;
                        var alpha = 0f;
                        var shade = 1f;
                        // When the flowing blood reaches this pixel; the stain itself is there from the start
                        var time = 0f;
                        // Which leg's drip this is, so each leg can flow on its own: 0 stain, 1 left, 2 right
                        byte leg = 0;
                        var height = 0f;

                        // Blob field > 0 inside the stain, growing toward each sphere's centre
                        if (Vec3.Distance(pos, k) < blobReach)
                        {
                            var n = Noise(pos * noiseScale, noiseSeed);
                            var field = 0f;
                            for (int i = 0; i < sphereCenters.Length; i++)
                            {
                                var d = ((pos - sphereCenters[i]) * sphereStretch[i]).Length();
                                field = MathF.Max(field, 1f - d / (sphereRadii[i] * (0.8f + 0.4f * n)));
                            }
                            if (field > 0f)
                            {
                                var interior = Clamp01(field * 2.5f);
                                // Soft rim, so the stain soaks into the skin, thin at the rim and fuller in the middle
                                alpha = SmoothStep(Clamp01(field * 3f)) * Lerp(0.7f, 0.95f, interior) * Lerp(0.85f, 1f, n);
                                // Fresh blood is bright where it is thin and only a little darker where it pools
                                shade = Lerp(1f, 0.82f, interior) * Lerp(0.9f, 1f, n);
                                // A thin wet film, nearly flat
                                height = alpha * 0.15f;
                            }
                        }

                        for (int s = 0; s < streaks.Length; s++)
                        {
                            var membership = memA[s] * w0 + memB[s] * w1 + memC[s] * w2;
                            if (membership <= 0.05f) continue;
                            streaks[s].Sample(pos, out var sampleAlpha, out var sampleShade, out var along, out var sampleHeight);
                            var sa = sampleAlpha * Clamp01(membership * 2f);
                            if (sa > alpha)
                            {
                                shade = Lerp(shade, sampleShade, sa);
                                alpha = sa;
                                height = sampleHeight;
                                time = streaks[s].Delay + streaks[s].Duration * along;
                                leg = streaks[s].Leg.Side == "L" ? (byte)1 : (byte)2;
                            }
                        }

                        if (alpha <= 0.01f) continue;
                        var idx = y * TexSize + x;
                        var a8 = (byte)(alpha * 255f);
                        if (a8 <= colors[idx].a) continue;
                        // Shade runs 0-2: up to 1 darkens the colour, above 1 is a wet highlight toward white
                        var sb = (byte)(Math.Clamp(shade, 0f, 2f) * 127.5f);
                        colors[idx].r = sb;
                        colors[idx].g = (byte)(Clamp01(time) * 255f);
                        colors[idx].b = leg;
                        colors[idx].a = a8;
                        heights[idx] = height;
                    }
                }
            }
            var paintMs = (DateTime.Now - paintStarted).TotalMilliseconds;

            var uploadStarted = DateTime.Now;
            var blood = new BloodTexture(colors, BuildNormals(colors, heights));
            blood.Colorize(color, progress, progress);
            var uploadMs = (DateTime.Now - uploadStarted).TotalMilliseconds;

            VirginBloodPlugin.Log.LogInfo($"Blood overlay generated in {(DateTime.Now - started).TotalMilliseconds:F0} ms " +
                $"(mesh {meshMs:F0}, paint {paintMs:F0}, upload {uploadMs:F0}; {legs.Count} legs, {streaks.Length} streaks, scale {scale:F3})");
            return blood;
        }

        // How steep the blood's bumps look; heights change over a few texels, so slopes need boosting
        private const float NormalStrength = 4f;

        // Turns the heights into a tangent space normal map (x in R, y in G), flat wherever there is no blood
        private static Color32[] BuildNormals(Color32[] mask, float[] heights)
        {
            var normals = new Color32[mask.Length];
            var flat = new Color32(128, 128, 255, 255);
            for (int i = 0; i < normals.Length; i++)
                normals[i] = flat;

            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i].a == 0) continue;
                int x = i % TexSize, y = i / TexSize;
                float Height(int hx, int hy) => heights[Math.Clamp(hy, 0, TexSize - 1) * TexSize + Math.Clamp(hx, 0, TexSize - 1)];
                var dx = (Height(x + 1, y) - Height(x - 1, y)) * 0.5f * NormalStrength;
                var dy = (Height(x, y + 1) - Height(x, y - 1)) * 0.5f * NormalStrength;
                var n = Vec3.Normalize(new Vec3(-dx, -dy, 1f));
                var c = default(Color32);
                c.r = (byte)((n.X * 0.5f + 0.5f) * 255f);
                c.g = (byte)((n.Y * 0.5f + 0.5f) * 255f);
                c.b = (byte)((n.Z * 0.5f + 0.5f) * 255f);
                // Alpha 1, so shaders that read x from alpha (DXT5nm style) get the same x
                c.a = 255;
                normals[i] = c;
            }
            return normals;
        }

        /// <summary>
        /// The blood texture plus what is needed to recolour it or move the drips along without repainting.
        /// </summary>
        internal class BloodTexture
        {
            // Width of the soft front of a running drip, as a fraction of the whole flow
            private const float FlowEdge = 0.04f;

            public readonly Texture2D Texture;
            // Bumps of the drops and drips, so the wet shine follows them; it never changes, so it is uploaded once
            public readonly Texture2D Normal;
            // r = shade, g = when the flowing blood arrives (0-1), b = leg (0 stain, 1 left, 2 right), a = coverage
            private readonly Color32[] _mask;
            // Only pixels with blood are rewritten, the rest of the texture stays empty
            private readonly int[] _active;
            // Kept on the IL2CPP side, so updates don't copy the whole texture across every time
            private readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> _pixels;

            public BloodTexture(Color32[] mask, Color32[] normals)
            {
                _mask = mask;
                var active = new List<int>();
                for (int i = 0; i < mask.Length; i++)
                    if (mask[i].a > 0) active.Add(i);
                _active = active.ToArray();
                _pixels = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(mask.Length);

                Texture = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, true);
                Texture.wrapMode = TextureWrapMode.Clamp;
                Texture.filterMode = FilterMode.Trilinear;
                Texture.hideFlags = HideFlags.DontUnloadUnusedAsset;

                // Normal maps hold directions, not colours, so the texture must be linear
                Normal = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, true, true);
                Normal.wrapMode = TextureWrapMode.Clamp;
                Normal.filterMode = FilterMode.Trilinear;
                Normal.hideFlags = HideFlags.DontUnloadUnusedAsset;
                Normal.SetPixels32(normals);
                Normal.Apply(true);
            }

            // Bakes the colour into the texture, since the game's shaders don't tint it reliably.
            // Pixels the drips haven't reached yet at each leg's progress are left out
            public void Colorize(Color color, float progressLeft, float progressRight)
            {
                float r = color.r, g = color.g, b = color.b;
                foreach (var i in _active)
                {
                    var m = _mask[i];
                    var progress = m.b == 1 ? progressLeft : m.b == 2 ? progressRight : 1f;
                    var reveal = progress >= 1f ? 1f : Clamp01((progress - m.g / 255f) / FlowEdge);
                    var shade = m.r / 127.5f;
                    var highlight = Clamp01(shade - 1f);
                    shade = MathF.Min(shade, 1f);
                    var c = default(Color32);
                    c.r = (byte)(Lerp(Clamp01(r * shade), 1f, highlight) * 255f);
                    c.g = (byte)(Lerp(Clamp01(g * shade), 1f, highlight) * 255f);
                    c.b = (byte)(Lerp(Clamp01(b * shade), 1f, highlight) * 255f);
                    c.a = (byte)(m.a * reveal);
                    _pixels[i] = c;
                }
                Texture.SetPixels32(_pixels);
                Texture.Apply(true);
            }

            public void Destroy()
            {
                UnityEngine.Object.Destroy(Texture);
                UnityEngine.Object.Destroy(Normal);
            }
        }

        private class Leg
        {
            public readonly string Side;
            public readonly Vec3 Top;
            public readonly Vec3 Dir;
            public readonly Vec3 Inner;
            public readonly Vec3 Ortho;
            public readonly float Length;
            public readonly float[] Membership;

            public Leg(string side, Vec3 top, Vec3 knee, Vec3 kokan, float[] membership)
            {
                Side = side;
                Top = top;
                Membership = membership;
                Length = Vec3.Distance(top, knee);
                Dir = (knee - top) / Length;
                // "Inner" points from the thigh axis toward the crotch
                var toKokan = kokan - top;
                Inner = Vec3.Normalize(toKokan - Vec3.Dot(toKokan, Dir) * Dir);
                Ortho = Vec3.Cross(Dir, Inner);
            }

            public float Along(Vec3 p) => Vec3.Dot(p - Top, Dir);

            // Distance around the thigh surface from its inner line, and distance from the axis
            public void Around(Vec3 p, out float arc, out float radius)
            {
                var w = p - Top;
                var radial = w - Vec3.Dot(w, Dir) * Dir;
                radius = radial.Length();
                var angle = MathF.Atan2(Vec3.Dot(radial, Ortho), Vec3.Dot(radial, Inner));
                arc = angle * radius;
            }
        }

        private class Streak
        {
            public Leg Leg;
            public StreakImage Image;
            public float Length;
            public float Width;
            public float Center;
            public float Start;
            public bool Mirror;
            public float Delay;
            public float Duration;
            public Bead[] Beads;
            // How far either side to look in the image, so hair-thin drips are drawn at a visible width
            public float Widen;

            public bool Covers(Vec3 p)
            {
                var along = Leg.Along(p);
                return along > Start - Length * 0.1f && along < Start + Length * 1.1f
                    && Vec3.Distance(p, Leg.Top + Leg.Dir * along) < Leg.Length * 0.6f;
            }

            // along: how far down the streak the point is, 0 at the top
            // height: how far the blood stands off the skin, 1 at the top of a bead
            public void Sample(Vec3 p, out float alpha, out float shade, out float along, out float height)
            {
                alpha = 0f;
                shade = 1f;
                height = 0f;
                var v = (Leg.Along(p) - Start) / Length;
                along = v;
                if (v < 0f || v > 1f) return;
                Leg.Around(p, out var arc, out var radius);
                // Only the side of the thigh facing the other leg, fading out toward the front and back
                if (radius > Leg.Length * 0.6f || radius < 1e-5f) return;
                var fade = Clamp01((1.5f - MathF.Abs(arc / radius)) / 0.4f);
                if (fade <= 0f) return;
                var u = 0.5f + (arc - Center) / Width;
                if (u < 0f || u > 1f) return;
                if (Mirror) u = 1f - u;
                SampleImage(Image, u, v, out alpha, out shade);
                for (int side = -1; side <= 1; side += 2)
                {
                    SampleImage(Image, Clamp01(u + side * Widen), v, out var sideAlpha, out var sideShade);
                    if (sideAlpha > alpha)
                    {
                        alpha = sideAlpha;
                        shade = sideShade;
                    }
                }
                // Drips are low rounded ridges
                height = alpha * 0.3f;

                foreach (var bead in Beads)
                {
                    // Distance from the bead's centre in world units, then scaled to its radius
                    var du = (u - bead.U) * Width / bead.Radius;
                    var dv = (v - bead.V) * Length / bead.Radius;
                    var d = MathF.Sqrt(du * du + dv * dv);
                    if (d >= 1f) continue;
                    var beadAlpha = SmoothStep(Clamp01((1f - d) * 4f)) * 0.95f;
                    if (beadAlpha <= alpha) continue;
                    // Rounded: full colour in the middle, darker toward the rim, domed like a drop of water
                    alpha = beadAlpha;
                    shade = Lerp(1f, 0.72f, d * d);
                    // A small bright spot up and to one side, the light caught by a wet drop
                    float hx = du + 0.3f, hy = dv + 0.35f;
                    shade += 0.8f * MathF.Exp(-(hx * hx + hy * hy) / 0.04f);
                    height = MathF.Max(height, MathF.Sqrt(1f - d * d));
                }
                alpha *= fade;
                height *= fade;
            }
        }

        private static void SampleImage(StreakImage img, float u, float v, out float alpha, out float shade)
        {
            // v = 0 is the top of the image, where the streak starts
            var fx = u * (img.Width - 1);
            var fy = (1f - v) * (img.Height - 1);
            var x0 = Math.Clamp((int)fx, 0, img.Width - 2);
            var y0 = Math.Clamp((int)fy, 0, img.Height - 2);
            var tx = fx - x0;
            var ty = fy - y0;
            var i00 = y0 * img.Width + x0;
            var i10 = i00 + img.Width;
            float Lerp2(float[] d) => Lerp(Lerp(d[i00], d[i00 + 1], tx), Lerp(d[i10], d[i10 + 1], tx), ty);
            alpha = Lerp2(img.Alpha);
            shade = Lerp2(img.Shade);
        }

        private static List<StreakImage> LoadStreaks()
        {
            if (_streaks != null) return _streaks;
            _streaks = new List<StreakImage>();
            var assembly = typeof(BodyOverlay).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Resources.streak_")).OrderBy(n => n))
            {
                using var stream = assembly.GetManifestResourceStream(name);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, memory.ToArray()))
                {
                    VirginBloodPlugin.Log.LogWarning($"Could not decode {name}");
                    UnityEngine.Object.Destroy(tex);
                    continue;
                }
                var source = tex.GetPixels32();
                var pixels = new Color32[source.Length];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = source[i];
                var img = new StreakImage
                {
                    Width = tex.width,
                    Height = tex.height,
                    Alpha = new float[pixels.Length],
                    Shade = new float[pixels.Length],
                };
                UnityEngine.Object.Destroy(tex);

                // Keep the image's light/dark variation but leave the hue to the colour setting
                var maxRed = 1;
                foreach (var p in pixels)
                    if (p.a > 128 && p.r > maxRed) maxRed = p.r;
                for (int i = 0; i < pixels.Length; i++)
                {
                    img.Alpha[i] = pixels[i].a / 255f;
                    // Squeezed toward bright, the images' darkest parts would read as brown or black
                    img.Shade[i] = 0.7f + 0.3f * Clamp01((float)pixels[i].r / maxRed);
                }
                img.Tips = FindTips(img);
                _streaks.Add(img);
            }
            VirginBloodPlugin.Log.LogInfo($"Loaded {_streaks.Count} streak images");
            return _streaks;
        }

        // The point on the thigh's skin where a streak starts: the leg vertex nearest its top centre
        private static bool StreakTop(MeshData data, Streak streak, out Vec3 top)
        {
            top = default;
            var leg = streak.Leg;
            var best = float.MaxValue;
            for (int v = 0; v < data.Verts.Length && v < leg.Membership.Length; v++)
            {
                if (leg.Membership[v] < 0.5f) continue;
                var p = data.Verts[v];
                var along = MathF.Abs(leg.Along(p) - streak.Start);
                if (along > leg.Length * 0.04f) continue;
                leg.Around(p, out var arc, out var radius);
                if (radius > leg.Length * 0.6f) continue;
                var score = MathF.Abs(arc - streak.Center) + along;
                if (score < best)
                {
                    best = score;
                    top = p;
                }
            }
            return best < float.MaxValue;
        }

        // Finds where each drip ends: columns whose lowest solid pixel is lower than its neighbours'
        private static List<Vec3> FindTips(StreakImage img)
        {
            // Texture rows start at the bottom, so v = 1 - row / (height - 1)
            var lowest = new float[img.Width];
            for (int x = 0; x < img.Width; x++)
            {
                lowest[x] = -1f;
                for (int y = 0; y < img.Height; y++)
                {
                    if (img.Alpha[y * img.Width + x] > 0.5f)
                    {
                        lowest[x] = 1f - (float)y / (img.Height - 1);
                        break;
                    }
                }
            }

            var window = Math.Max(2, img.Width / 20);
            var tips = new List<Vec3>();
            for (int x = 0; x < img.Width; x++)
            {
                if (lowest[x] < 0.15f) continue;
                var isTip = true;
                for (int n = Math.Max(0, x - window); n <= Math.Min(img.Width - 1, x + window) && isTip; n++)
                    if (lowest[n] > lowest[x] || (lowest[n] == lowest[x] && n < x)) isTip = false;
                if (!isTip) continue;

                // Width of the drip a little above its tip: solid pixels either side of this column
                var row = Math.Clamp((int)((1f - (lowest[x] - 0.03f)) * (img.Height - 1)), 0, img.Height - 1);
                bool Solid(int cx) => cx >= 0 && cx < img.Width && img.Alpha[row * img.Width + cx] > 0.5f;
                var width = 0;
                for (int cx = x; Solid(cx); cx--) width++;
                for (int cx = x + 1; Solid(cx); cx++) width++;
                tips.Add(new Vec3((x + 0.5f) / img.Width, lowest[x], (float)width / img.Width));
            }
            return tips.OrderByDescending(t => t.Y).Take(5).ToList();
        }

        private static float Edge(Vec2 a, Vec2 b, Vec2 p)
        {
            return (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        private static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

        private static float SmoothStep(float t) => t * t * (3f - 2f * t);

        // Smooth 3D value noise in [0, 1]
        private static float Noise(Vec3 p, int seed)
        {
            int x0 = (int)MathF.Floor(p.X), y0 = (int)MathF.Floor(p.Y), z0 = (int)MathF.Floor(p.Z);
            float fx = SmoothStep(p.X - x0), fy = SmoothStep(p.Y - y0), fz = SmoothStep(p.Z - z0);
            float Corner(int dx, int dy, int dz) => Hash(x0 + dx, y0 + dy, z0 + dz, seed);
            var x00 = Lerp(Corner(0, 0, 0), Corner(1, 0, 0), fx);
            var x10 = Lerp(Corner(0, 1, 0), Corner(1, 1, 0), fx);
            var x01 = Lerp(Corner(0, 0, 1), Corner(1, 0, 1), fx);
            var x11 = Lerp(Corner(0, 1, 1), Corner(1, 1, 1), fx);
            return Lerp(Lerp(x00, x10, fy), Lerp(x01, x11, fy), fz);
        }

        private static float Hash(int x, int y, int z, int seed)
        {
            unchecked
            {
                var h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return h / (float)uint.MaxValue;
            }
        }
    }
}
