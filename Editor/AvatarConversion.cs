using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TsiYuki.NonToon.Editor
{
    /// <summary>Snapshot of the component's settings, so a build does not depend on the component surviving.</summary>
    internal sealed class ConverterSettings
    {
        public bool ConvertOnBuild = true;
        public bool SkipOnAndroid = true;
        public HashSet<Material> Excluded = new HashSet<Material>();
        public bool OutlineMaskToVertexColor = true;
        public bool BakeMatCapBlur = true;
        public float ShadowBrightness;
        public Dictionary<Material, MaterialOverride> Overrides = new Dictionary<Material, MaterialOverride>();

        public static ConverterSettings From(YukiNonToonConverter c)
        {
            var s = new ConverterSettings
            {
                ConvertOnBuild = c.convertOnBuild,
                SkipOnAndroid = c.skipOnAndroid,
                Excluded = new HashSet<Material>(c.excludedMaterials.Where(m => m != null)),
                OutlineMaskToVertexColor = c.outlineMaskToVertexColor,
                BakeMatCapBlur = c.bakeMatCapBlur,
                ShadowBrightness = c.shadowBrightness,
            };
            foreach (var o in c.overrides)
                if (o != null && o.source != null && !s.Overrides.ContainsKey(o.source)) s.Overrides[o.source] = o;
            return s;
        }

        public ConvertSettings For(Material source) => new ConvertSettings
        {
            BakeMatCapBlur = BakeMatCapBlur,
            OutlineMaskToVertexColor = OutlineMaskToVertexColor,
            ShadowBrightness = ShadowBrightness,
            Override = Overrides.TryGetValue(source, out var o) ? o : null,
        };
    }

    /// <summary>
    /// Converts the materials of one avatar, caching each result so a material shared by many renderers
    /// is converted once. Used by the build pass, the scene preview, the inspector and the exporter.
    /// </summary>
    internal sealed class AvatarConversion : IDisposable
    {
        public readonly ConverterSettings Settings;
        public readonly TextureBaker Textures;
        public readonly OutlineMeshBaker Meshes;
        private readonly bool _ownsAssets;
        private readonly Dictionary<Material, ConvertedMaterial> _converted = new Dictionary<Material, ConvertedMaterial>();

        public IEnumerable<ConvertedMaterial> Converted => _converted.Values;

        /// <param name="ownsAssets">Destroy generated materials, textures and meshes on Dispose (preview, inspector).</param>
        public AvatarConversion(ConverterSettings settings, bool compressTextures, bool ownsAssets, int maxTextureSize = 4096)
        {
            Settings = settings;
            Textures = new TextureBaker(compressTextures, maxTextureSize);
            Meshes = new OutlineMeshBaker(ownsAssets);
            _ownsAssets = ownsAssets;
        }

        public bool ShouldConvert(Material material) =>
            material != null && LilToon.Is(material) && !Settings.Excluded.Contains(material);

        public ConvertedMaterial Get(Material source)
        {
            if (!ShouldConvert(source)) return null;
            if (_converted.TryGetValue(source, out var result)) return result;
            result = MaterialConverter.Convert(source, Settings.For(source), Textures);
            _converted[source] = result;
            return result;
        }

        public Material Replace(Material source) => Get(source)?.Material ?? source;

        /// <summary>Converts a renderer's material array. Returns true if anything changed.</summary>
        public bool ConvertMaterials(Material[] sourceMaterials, out Material[] converted, out List<OutlineMaskRequest> outlineRequests)
        {
            converted = new Material[sourceMaterials.Length];
            outlineRequests = new List<OutlineMaskRequest>(sourceMaterials.Length);
            var changed = false;
            for (var i = 0; i < sourceMaterials.Length; i++)
            {
                var result = Get(sourceMaterials[i]);
                converted[i] = result?.Material ?? sourceMaterials[i];
                outlineRequests.Add(result?.OutlineMask);
                changed |= result != null;
            }
            return changed;
        }

        public static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        public static void SetMesh(Renderer renderer, Mesh mesh)
        {
            if (renderer is SkinnedMeshRenderer smr) smr.sharedMesh = mesh;
            else
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter != null) filter.sharedMesh = mesh;
            }
        }

        public void Dispose()
        {
            if (_ownsAssets)
            {
                foreach (var c in _converted.Values)
                    if (c.Material != null) UnityEngine.Object.DestroyImmediate(c.Material);
                Textures.Dispose();
            }
            Meshes.Dispose();
            _converted.Clear();
        }
    }
}
