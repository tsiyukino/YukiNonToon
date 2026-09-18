using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEngine;

namespace TsiYuki.NonToon.Editor
{
    /// <summary>
    /// Shows NonToon in the Scene and Game views without building: NDMF renders proxy copies of the
    /// renderers, and this filter gives the proxies converted materials (and outline meshes).
    /// Toggle it from NDMF's preview menu.
    /// </summary>
    internal sealed class NonToonPreview : IRenderFilter
    {
        internal static readonly TogglablePreviewNode Toggle =
            TogglablePreviewNode.Create(() => "Yuki NonToon", "moe.tsiyuki.nontoon/preview", true);

        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes()
        {
            yield return Toggle;
        }

        public bool IsEnabled(ComputeContext context) => context.Observe(Toggle.IsEnabled);

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            if (!NonToonShader.IsInstalled) return ImmutableList<RenderGroup>.Empty;
            var groups = ImmutableList.CreateBuilder<RenderGroup>();
            foreach (var root in context.GetAvatarRoots())
            {
                var component = context.GetComponentsInChildren<YukiNonToonConverter>(root, true).FirstOrDefault();
                if (component == null) continue;
                if (!context.Observe(component, c => c.convertOnBuild && c.enabled, (a, b) => a == b)) continue;
                var renderers = context.GetComponentsInChildren<Renderer>(root, true)
                    .Where(r => r is SkinnedMeshRenderer || r is MeshRenderer)
                    .Where(r => context.Observe(r, x => x.sharedMaterials.Any(LilToon.Is), (a, b) => a == b))
                    .ToList();
                if (renderers.Count == 0) continue;
                // One group per avatar, so a material shared by many renderers is converted once.
                groups.Add(RenderGroup.For(renderers).WithData(component, (a, b) => a == b));
            }
            return groups.ToImmutable();
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context)
        {
            var component = group.GetData<YukiNonToonConverter>();
            // Re-run when any setting of the component changes.
            context.Observe(component, c => JsonUtility.ToJson(c), (a, b) => a == b);
            var settings = ConverterSettings.From(component);
            var conversion = new AvatarConversion(settings, compressTextures: false, ownsAssets: true, maxTextureSize: 1024);
            var node = new Node(conversion);
            foreach (var (original, proxy) in proxyPairs)
            {
                foreach (var material in original.sharedMaterials)
                    if (material != null && LilToon.Is(material)) context.Observe(material);
                node.Prepare(original, proxy);
            }
            return Task.FromResult<IRenderFilterNode>(node);
        }

        private sealed class Node : IRenderFilterNode
        {
            private readonly AvatarConversion _conversion;
            private readonly Dictionary<Renderer, (Material[] materials, Mesh mesh)> _results = new Dictionary<Renderer, (Material[], Mesh)>();

            public Node(AvatarConversion conversion) { _conversion = conversion; }

            public RenderAspects WhatChanged => RenderAspects.Material | RenderAspects.Mesh | RenderAspects.Texture;

            public void Prepare(Renderer original, Renderer proxy)
            {
                if (proxy == null) return;
                if (!_conversion.ConvertMaterials(proxy.sharedMaterials, out var materials, out var outline)) return;
                Mesh mesh = null;
                if (outline.Any(o => o != null)) mesh = _conversion.Meshes.Bake(AvatarConversion.GetMesh(proxy), outline);
                _results[original] = (materials, mesh);
                Apply(proxy, materials, mesh);
            }

            private static void Apply(Renderer proxy, Material[] materials, Mesh mesh)
            {
                proxy.sharedMaterials = materials;
                if (mesh != null) AvatarConversion.SetMesh(proxy, mesh);
            }

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (proxy != null && _results.TryGetValue(original, out var result)) Apply(proxy, result.materials, result.mesh);
            }

            public void Dispose() => _conversion.Dispose();
        }
    }
}
