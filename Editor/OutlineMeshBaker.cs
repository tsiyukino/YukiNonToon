using System;
using System.Collections.Generic;
using UnityEngine;

namespace TsiYuki.NonToon.Editor
{
    /// <summary>
    /// NonToon reads outline width from vertex colors only (RGB = direction in tangent space around 0.5,
    /// A = Z offset). lilToon uses a width mask texture. This writes the mask into a copy of the mesh:
    /// color = lerp(0.5, (0,0,1), width), the same encoding lilOutlineSmoother uses for NonToon.
    /// </summary>
    internal sealed class OutlineMeshBaker : IDisposable
    {
        private readonly Dictionary<string, Mesh> _meshes = new Dictionary<string, Mesh>();
        private readonly bool _ownsMeshes;

        public IEnumerable<Mesh> Generated => _meshes.Values;

        /// <param name="ownsMeshes">Destroy the created meshes on Dispose (preview). A build hands them to NDMF instead.</param>
        public OutlineMeshBaker(bool ownsMeshes) { _ownsMeshes = ownsMeshes; }

        /// <summary>Returns a mesh with outline widths written, or null if no submesh needs it.</summary>
        public Mesh Bake(Mesh mesh, IReadOnlyList<OutlineMaskRequest> perSubmesh)
        {
            if (mesh == null) return null;
            var any = false;
            var keyBuilder = new System.Text.StringBuilder(mesh.GetInstanceID().ToString());
            for (var i = 0; i < perSubmesh.Count; i++)
            {
                var r = perSubmesh[i];
                keyBuilder.Append('|');
                if (r == null) continue;
                any = true;
                keyBuilder.Append(r.Mask != null ? r.Mask.GetInstanceID() : 0).Append(',').Append(r.ScaleOffset).Append(',').Append(r.UseVertexColorR);
            }
            if (!any) return null;
            var key = keyBuilder.ToString();
            if (_meshes.TryGetValue(key, out var cached) && cached != null) return cached;

            var copy = UnityEngine.Object.Instantiate(mesh);
            copy.name = mesh.name + " (NonToon outline)";
            var uv = mesh.uv;
            var original = mesh.colors;
            var colors = new Color[mesh.vertexCount];
            for (var i = 0; i < colors.Length; i++)
                colors[i] = original.Length == colors.Length ? original[i] : Color.white;

            for (var sub = 0; sub < Mathf.Min(perSubmesh.Count, mesh.subMeshCount); sub++)
            {
                var request = perSubmesh[sub];
                if (request == null) continue;
                var mask = request.Mask != null ? TextureBaker.ReadAt(request.Mask, 2048) : null;
                var so = request.ScaleOffset;
                var indices = mesh.GetIndices(sub);
                var done = new HashSet<int>();
                foreach (var index in indices)
                {
                    if (!done.Add(index)) continue;
                    var width = 1f;
                    if (mask != null && uv.Length == colors.Length)
                        width *= mask.SampleRepeat(uv[index].x * so.x + so.z, uv[index].y * so.y + so.w).r;
                    if (request.UseVertexColorR && original.Length == colors.Length)
                        width *= original[index].r;
                    width = Mathf.Clamp01(width);
                    colors[index] = new Color(0.5f, 0.5f, 0.5f + 0.5f * width, 1f);
                }
            }
            copy.colors = colors;
            _meshes[key] = copy;
            return copy;
        }

        public void Dispose()
        {
            if (_ownsMeshes)
                foreach (var mesh in _meshes.Values)
                    if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            _meshes.Clear();
        }
    }
}
