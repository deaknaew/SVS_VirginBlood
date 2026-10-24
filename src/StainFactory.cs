using UnityEngine;
using System;

namespace SVS_VirginBlood
{
    /// <summary>
    /// Creates and updates the transparent material the blood overlay is drawn with.
    /// </summary>
    internal static class StainFactory
    {
        private static Shader _shader;

        internal static void ApplyColor(Material material)
        {
            if (material == null) return;
            // The colour is baked into the texture, the material only adds opacity
            var color = new Color(1f, 1f, 1f, VirginBloodPlugin.Opacity.Value);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            // The game's LIF shaders tint with _Main_color_01.._04 (picked by a colour mask) and fade with _alpha
            foreach (var name in new[] { "_Main_color_01", "_Main_color_02", "_Main_color_03", "_Main_color_04" })
            {
                if (material.HasProperty(name))
                    material.SetColor(name, color);
            }
            if (material.HasProperty("_alpha"))
                material.SetFloat("_alpha", color.a);
            ApplyShine(material);
        }

        private static Texture _glossMatcap;

        // Wet shine like sweat: the LIF shaders' matcap reflection, with the game's own gloss texture
        internal static void ApplyShine(Material material)
        {
            if (material == null || !material.HasProperty("_Matcaponoff")) return;
            var shine = VirginBloodPlugin.WetShine.Value;
            if (shine <= 0f)
            {
                SetFloatIfPresent(material, "_Matcaponoff", 0f);
                return;
            }
            if (_glossMatcap == null)
                _glossMatcap = FindGlossMatcap();
            SetFloatIfPresent(material, "_Matcaponoff", 1f);
            material.SetTexture("_Mat_cap_01", _glossMatcap);
            SetFloatIfPresent(material, "_Matcap_power_01", shine);
        }

        private static Texture FindGlossMatcap()
        {
            foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Texture2D>()))
            {
                var tex = obj?.TryCast<Texture2D>();
                if (tex != null && tex.name == "t_matcap_gloss01")
                {
                    VirginBloodPlugin.Log.LogInfo("Wet shine uses the game's t_matcap_gloss01");
                    return tex;
                }
            }

            // Not loaded: a dark sphere with a soft highlight up and to the left, like a wet surface
            VirginBloodPlugin.Log.LogInfo("Wet shine uses a generated highlight, t_matcap_gloss01 is not loaded");
            const int size = 128;
            var generated = new Texture2D(size, size, TextureFormat.RGBA32, false);
            generated.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size - 0.35f, dy = (y + 0.5f) / size - 0.68f;
                    var spot = MathF.Exp(-(dx * dx + dy * dy) / 0.006f);
                    var v = (byte)(Math.Min(1f, spot) * 255f);
                    pixels[y * size + x] = new Color32(v, v, v, 255);
                }
            }
            generated.SetPixels32(pixels);
            generated.Apply(false);
            return generated;
        }

        internal static void ApplyTexture(Material material, BodyOverlay.BloodTexture blood)
        {
            if (material == null || blood == null) return;
            var texture = blood.Texture;
            // The LIF shaders' normal map makes the drops and drips catch the wet shine like sweat
            if (material.HasProperty("_Normal"))
            {
                material.SetTexture("_Normal", blood.Normal);
                SetFloatIfPresent(material, "_Normal_scale", 1f);
            }
            if (material.HasProperty("_BumpMap"))
                material.SetTexture("_BumpMap", blood.Normal);
            material.mainTexture = texture;
            if (material.HasProperty("_BaseMap"))
                material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_MainTex"))
                material.SetTexture("_MainTex", texture);
            // The game's LIF shaders read their albedo from _Main_texture
            if (material.HasProperty("_Main_texture"))
                material.SetTexture("_Main_texture", texture);
        }

        private static readonly string[] DefaultShaders =
        {
            // The game's own transparent accessory shader, lit like the character
            "LIF/lif_main_acs_alpha",
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Unlit",
            "Universal Render Pipeline/Particles/Unlit",
            "Sprites/Default",
        };

        private static System.Collections.Generic.Dictionary<string, Shader> LoadedShaders()
        {
            // Shader.Find only sees shaders in the build, so also look at everything loaded,
            // which includes the game's own character shaders from asset bundles
            var loaded = new System.Collections.Generic.Dictionary<string, Shader>();
            foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Shader>()))
            {
                var s = obj?.TryCast<Shader>();
                if (s != null && !loaded.ContainsKey(s.name))
                    loaded[s.name] = s;
            }
            return loaded;
        }

        private static Shader FindShader()
        {
            var loaded = LoadedShaders();
            foreach (var name in DefaultShaders)
            {
                if (loaded.TryGetValue(name, out var shader))
                    return shader;
                shader = Shader.Find(name);
                if (shader != null)
                    return shader;
            }
            return null;
        }

        // Lighting settings taken from the skin, so the stain is shaded like the body under it
        private static readonly string[] SkinLighting =
        {
            "_ShadowColor", "_ShadowColorON", "_Shadow_power", "_LightCancel", "_shadowextend", "_ShadowSetting",
        };

        internal static Material CreateMaterial(BodyOverlay.BloodTexture blood, Material skin)
        {
            if (_shader == null)
            {
                _shader = FindShader();
                if (_shader == null)
                {
                    VirginBloodPlugin.Log.LogWarning("No suitable stain shader found");
                    return null;
                }
            }

            var mat = new Material(_shader);
            mat.hideFlags = HideFlags.DontUnloadUnusedAsset;
            ApplyTexture(mat, blood);

            // The LIF shaders add rim light and emission on top of the texture, which washes out the red.
            // Gloss is set up by ApplyShine, through ApplyColor below
            SetFloatIfPresent(mat, "_RimLight", 0f);
            SetFloatIfPresent(mat, "_Emission", 0f);
            SetFloatIfPresent(mat, "_EmissionPower", 0f);
            SetFloatIfPresent(mat, "_Line_texture_onoff", 0f);
            // The default shadow colour is blue-grey, which turns red into brown
            CopyLighting(skin, mat);

            // URP shaders need to be switched to transparent explicitly
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 0f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.SetOverrideTag("RenderType", "Transparent");
            }
            if (mat.HasProperty("_SrcBlend"))
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend"))
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite"))
                mat.SetInt("_ZWrite", 0);
            if (mat.HasProperty("_Cull"))
                mat.SetInt("_Cull", 0);
            mat.renderQueue = 3000;

            ApplyColor(mat);
            return mat;
        }

        private static void CopyLighting(Material from, Material to)
        {
            // Warm shadow in case the skin shader has none to copy
            var warmShadow = new Color(0.75f, 0.55f, 0.55f, 1f);
            if (to.HasProperty("_ShadowColor"))
                to.SetColor("_ShadowColor", warmShadow);
            if (from == null) return;
            foreach (var name in SkinLighting)
            {
                var fromIndex = from.shader.FindPropertyIndex(name);
                var toIndex = to.shader.FindPropertyIndex(name);
                if (fromIndex < 0 || toIndex < 0) continue;
                var type = from.shader.GetPropertyType(fromIndex);
                if (type != to.shader.GetPropertyType(toIndex)) continue;
                switch (type)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        to.SetColor(name, from.GetColor(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        to.SetFloat(name, from.GetFloat(name));
                        break;
                }
            }
        }

        private static void SetFloatIfPresent(Material mat, string name, float value)
        {
            var index = mat.shader.FindPropertyIndex(name);
            if (index < 0) return;
            var type = mat.shader.GetPropertyType(index);
            if (type == UnityEngine.Rendering.ShaderPropertyType.Float || type == UnityEngine.Rendering.ShaderPropertyType.Range)
                mat.SetFloat(name, value);
        }
    }
}
