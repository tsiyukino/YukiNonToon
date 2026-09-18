using UnityEngine;
using UnityEngine.Rendering;

namespace TsiYuki.NonToon.Editor
{
    /// <summary>Everything the converter knows about the NonToon shader and its Shader Core modules.</summary>
    internal static class NonToonShader
    {
        public const string MainShaderName = "NonToon";
        public const string FurShaderName = "NonToonFur";

        public const int ModeOpaque = 0;
        public const int ModeCutout = 1;
        public const int ModeTransparent = 2;

        public static Shader Main => Shader.Find(MainShaderName);
        public static Shader Fur => Shader.Find(FurShaderName);
        public static bool IsInstalled => Main != null;

        public static bool IsNonToon(Material material) =>
            material != null && material.shader != null &&
            (material.shader.name == MainShaderName || material.shader.name == FurShaderName);

        /// <summary>
        /// Shader Core prefixes module properties with the module's uniqueID, dots replaced by underscores:
        /// module "shade" + "ShadeGradientIndex" → <c>_jp_lilxyzw_nontoon_shade_ShadeGradientIndex</c>.
        /// Returns null when the module is not installed in the shader.
        /// </summary>
        public static string Module(Material material, string module, string property)
        {
            var name = "_jp_lilxyzw_nontoon_" + module + "_" + property;
            return material.HasProperty(name) ? name : null;
        }

        public static bool HasModule(Material material, string module, string probeProperty) =>
            Module(material, module, probeProperty) != null;

        /// <summary>Shader Core declares SC_int/SC_uint as real Integer properties; SetFloat does not reach them.</summary>
        public static void SetInt(Material material, string property, int value)
        {
            if (string.IsNullOrEmpty(property) || !material.HasProperty(property)) return;
            var index = material.shader.FindPropertyIndex(property);
            if (index >= 0 && material.shader.GetPropertyType(index) == ShaderPropertyType.Int)
                material.SetInteger(property, value);
            else
                material.SetFloat(property, value);
        }

        public static int GetInt(Material material, string property)
        {
            if (string.IsNullOrEmpty(property) || !material.HasProperty(property)) return 0;
            var index = material.shader.FindPropertyIndex(property);
            return index >= 0 && material.shader.GetPropertyType(index) == ShaderPropertyType.Int
                ? material.GetInteger(property)
                : Mathf.RoundToInt(material.GetFloat(property));
        }

        public static void SetFloat(Material material, string property, float value)
        {
            if (!string.IsNullOrEmpty(property) && material.HasProperty(property)) material.SetFloat(property, value);
        }

        public static void SetColor(Material material, string property, Color value)
        {
            if (!string.IsNullOrEmpty(property) && material.HasProperty(property)) material.SetColor(property, value);
        }

        public static void SetVector(Material material, string property, Vector4 value)
        {
            if (!string.IsNullOrEmpty(property) && material.HasProperty(property)) material.SetVector(property, value);
        }

        public static void SetTexture(Material material, string property, Texture value)
        {
            if (!string.IsNullOrEmpty(property) && material.HasProperty(property)) material.SetTexture(property, value);
        }

        /// <summary>
        /// SCConstValue turns a module's header toggle into a keyword. The integer and the generated
        /// <c>_0</c>/<c>_1</c> keyword must agree for the module to compile in.
        /// </summary>
        public static void SetModuleEnabled(Material material, string module, bool enabled)
        {
            var property = Module(material, module, "Enable");
            if (property == null) return;
            SetInt(material, property, enabled ? 1 : 0);
            var keyword = property.ToUpperInvariant();
            material.DisableKeyword(keyword + (enabled ? "_0" : "_1"));
            material.EnableKeyword(keyword + (enabled ? "_1" : "_0"));
        }

        public static void ApplyRenderingMode(Material material, int mode, int sourceQueue)
        {
            SetInt(material, "_RenderingMode", mode);
            SetInt(material, "_SrcBlend", mode == ModeTransparent ? (int)BlendMode.SrcAlpha : (int)BlendMode.One);
            SetInt(material, "_DstBlend", mode == ModeTransparent ? (int)BlendMode.OneMinusSrcAlpha : (int)BlendMode.Zero);
            SetInt(material, "_AlphaToMask", mode == ModeCutout ? 1 : 0);
            var defaultQueue = mode == ModeOpaque ? (int)RenderQueue.Geometry : mode == ModeCutout ? (int)RenderQueue.AlphaTest : (int)RenderQueue.Transparent;
            // Keep the source's custom sorting (e.g. glasses at 3200) when it is in the same range.
            var keep = sourceQueue > 0 &&
                       (mode == ModeTransparent ? sourceQueue > 2500 : mode == ModeCutout ? sourceQueue >= 2450 && sourceQueue <= 2500 : sourceQueue < 2450 && sourceQueue >= 1000);
            material.renderQueue = keep ? sourceQueue : defaultQueue;
        }
    }
}
