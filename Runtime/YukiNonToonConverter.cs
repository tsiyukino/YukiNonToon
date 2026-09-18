using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace TsiYuki.NonToon
{
    /// <summary>
    /// Converts every lilToon material on the avatar to NonToon when the avatar is built (Play mode or
    /// upload). Place one anywhere inside the avatar: on the root, or on its own child object like a
    /// Modular Avatar component. Nothing in the project is changed; remove the component to go back.
    /// </summary>
    [AddComponentMenu("TsiYuki/Yuki NonToon Converter")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/tsiyukino/YukiNonToon")]
    public sealed class YukiNonToonConverter : MonoBehaviour, IEditorOnly
    {
        [Tooltip("Convert when the avatar is built. Turn off to keep lilToon without removing the component.")]
        public bool convertOnBuild = true;

        [Tooltip("Keep lilToon when building for Android / Quest.")]
        public bool skipOnAndroid = true;

        [Tooltip("Materials that stay lilToon.")]
        public List<Material> excludedMaterials = new List<Material>();

        [Tooltip("Write lilToon's outline width mask into mesh vertex colors, which is the only way NonToon controls outline width.")]
        public bool outlineMaskToVertexColor = true;

        [Tooltip("Reproduce lilToon's MatCap blur by baking a blurred MatCap texture.")]
        public bool bakeMatCapBlur = true;

        [Tooltip("Extra light in shadowed areas. NonToon removes direct light in cast shadows, lilToon only tints them; raise this if shadows look too dark.")]
        [Range(0f, 1f)]
        public float shadowBrightness = 0f;

        [Tooltip("Hand-tuned values applied on top of the automatic conversion.")]
        public List<MaterialOverride> overrides = new List<MaterialOverride>();

        public MaterialOverride FindOverride(Material source)
        {
            if (source == null) return null;
            foreach (var entry in overrides)
                if (entry != null && entry.source == source) return entry;
            return null;
        }
    }

    [Serializable]
    public sealed class MaterialOverride
    {
        [Tooltip("The lilToon material these values apply to.")]
        public Material source;

        public List<PropertyOverride> properties = new List<PropertyOverride>();
    }

    public enum PropertyOverrideType
    {
        Float,
        Int,
        Color,
        Vector,
        Texture,
    }

    [Serializable]
    public sealed class PropertyOverride
    {
        public string name;
        public PropertyOverrideType type;
        public float floatValue;
        public int intValue;
        public Color colorValue = Color.white;
        public Vector4 vectorValue;
        public Texture textureValue;
    }
}
