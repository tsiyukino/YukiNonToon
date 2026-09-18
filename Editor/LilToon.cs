using System;
using UnityEngine;

namespace TsiYuki.NonToon.Editor
{
    /// <summary>Typed read access to a lilToon material. Missing properties return lilToon's defaults.</summary>
    internal sealed class LilToon
    {
        public readonly Material Material;
        public readonly string ShaderName;

        public LilToon(Material material)
        {
            Material = material;
            ShaderName = material.shader != null ? material.shader.name : string.Empty;
        }

        public static bool Is(Material material) =>
            material != null && material.shader != null &&
            material.shader.name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0;

        public bool Has(string property) => Material.HasProperty(property);
        public float F(string property, float fallback = 0f) => Material.HasProperty(property) ? Material.GetFloat(property) : fallback;
        public bool On(string property) => F(property) > 0.5f;
        public int I(string property, int fallback = 0) => Material.HasProperty(property) ? Mathf.RoundToInt(Material.GetFloat(property)) : fallback;
        public Color C(string property) => Material.HasProperty(property) ? Material.GetColor(property) : Color.white;
        public Color C(string property, Color fallback) => Material.HasProperty(property) ? Material.GetColor(property) : fallback;
        public Vector4 V(string property, Vector4 fallback = default) => Material.HasProperty(property) ? Material.GetVector(property) : fallback;
        public Texture T(string property) => Material.HasProperty(property) ? Material.GetTexture(property) : null;
        public Vector2 Scale(string property) => Material.HasProperty(property) ? Material.GetTextureScale(property) : Vector2.one;
        public Vector2 Offset(string property) => Material.HasProperty(property) ? Material.GetTextureOffset(property) : Vector2.zero;

        private bool NameHas(string part) => ShaderName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>lilToonMulti keeps the mode in _TransparentMode; the other variants encode it in the shader name.</summary>
        private int MultiMode => NameHas("Multi") ? I("_TransparentMode") : -1;

        public bool IsFur => NameHas("Fur") || MultiMode == 4 || MultiMode == 5;
        public bool IsCutout => NameHas("Cutout") || MultiMode == 1 || MultiMode == 5;
        public bool IsRefractionOrGem => NameHas("Refraction") || NameHas("Gem") || MultiMode == 3 || MultiMode == 6;
        public bool IsTransparent => !IsCutout && (NameHas("Trans") || NameHas("Overlay") || IsRefractionOrGem || MultiMode == 2);
        public bool UsesOutlineShader => NameHas("Outline") || (NameHas("Multi") && On("_UseOutline"));
        public bool IsLite => NameHas("Lite");
    }
}
