using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace TsiYuki.NonToon.Editor
{
    /// <summary>
    /// The optional destructive mode: writes converted materials, textures and meshes into a folder and
    /// assigns them to the avatar in the scene. Re-exporting updates the same files in place, so their
    /// GUIDs and every reference to them stay valid.
    /// </summary>
    internal static class NonToonExporter
    {
        public static string DefaultFolder(VRCAvatarDescriptor avatar) =>
            "Assets/NonToonConverted/" + Sanitize(avatar.gameObject.name);

        public static int Export(YukiNonToonConverter component, VRCAvatarDescriptor avatar, string folder)
        {
            EnsureFolder(folder);
            var settings = ConverterSettings.From(component);
            using (var conversion = new AvatarConversion(settings, compressTextures: false, ownsAssets: true))
            {
                var renderers = avatar.GetComponentsInChildren<Renderer>(true);
                var sources = renderers.SelectMany(r => r.sharedMaterials).Where(conversion.ShouldConvert).Distinct().ToList();
                var saved = new Dictionary<Object, Object>();
                var usedNames = new HashSet<string>();

                try
                {
                    for (var i = 0; i < sources.Count; i++)
                    {
                        EditorUtility.DisplayProgressBar("Yuki NonToon", sources[i].name, (float)i / sources.Count);
                        conversion.Get(sources[i]);
                    }

                    var pngPaths = new Dictionary<Texture, string>();
                    AssetDatabase.StartAssetEditing();
                    try
                    {
                        foreach (var texture in conversion.Textures.Generated)
                        {
                            if (texture is Texture2D t2d)
                            {
                                var path = UniquePath(folder, texture.name, ".png", usedNames);
                                File.WriteAllBytes(path, t2d.EncodeToPNG());
                                pngPaths[texture] = path;
                            }
                        }
                    }
                    finally { AssetDatabase.StopAssetEditing(); }
                    foreach (var pair in pngPaths) saved[pair.Key] = ConfigureTexture(pair.Value, pair.Key);
                    foreach (var texture in conversion.Textures.Generated)
                    {
                        if (texture is Texture2D) continue;
                        // Texture arrays (gradients) are saved as Unity assets.
                        var copy = Object.Instantiate(texture);
                        copy.name = texture.name;
                        saved[texture] = ReplaceAsset(copy, UniquePath(folder, texture.name, ".asset", usedNames));
                    }

                    var materialMap = new Dictionary<Material, Material>();
                    foreach (var converted in conversion.Converted)
                    {
                        var material = converted.Material;
                        foreach (var name in material.GetTexturePropertyNames())
                        {
                            var texture = material.GetTexture(name);
                            if (texture != null && saved.TryGetValue(texture, out var asset)) material.SetTexture(name, (Texture)asset);
                        }
                        materialMap[converted.Source] = SaveMaterial(material, folder, converted.Source.name, usedNames);
                    }

                    foreach (var renderer in renderers)
                    {
                        if (!conversion.ConvertMaterials(renderer.sharedMaterials, out _, out var outline)) continue;
                        Undo.RecordObject(renderer, "Export NonToon");
                        renderer.sharedMaterials = renderer.sharedMaterials.Select(m => m != null && materialMap.TryGetValue(m, out var n) ? n : m).ToArray();
                        if (outline.Any(o => o != null))
                        {
                            var sourceMesh = AvatarConversion.GetMesh(renderer);
                            var mesh = conversion.Meshes.Bake(sourceMesh, outline);
                            if (mesh != null)
                            {
                                var meshAsset = SaveMesh(mesh, folder, sourceMesh.name, usedNames);
                                if (renderer is SkinnedMeshRenderer) Undo.RecordObject(renderer, "Export NonToon");
                                else Undo.RecordObject(renderer.GetComponent<MeshFilter>(), "Export NonToon");
                                AvatarConversion.SetMesh(renderer, meshAsset);
                            }
                        }
                        PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                    }
                    AssetDatabase.SaveAssets();
                    return materialMap.Count;
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "Avatar" : name.Trim();
        }

        private static string UniquePath(string folder, string name, string extension, HashSet<string> used)
        {
            var baseName = Sanitize(name);
            var candidate = baseName;
            for (var i = 2; !used.Add(candidate.ToLowerInvariant() + extension); i++) candidate = baseName + " " + i;
            return folder + "/" + candidate + extension;
        }

        private static void EnsureFolder(string folder)
        {
            var parts = folder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static Object ConfigureTexture(string path, Texture source)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.sRGBTexture = !(source is Texture2D t2 && IsLinear(t2));
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = importer.sRGBTexture;
                importer.mipmapEnabled = true;
                importer.streamingMipmaps = true; // required by VRChat
                importer.wrapMode = source.wrapMode;
                importer.filterMode = source.filterMode;
                importer.anisoLevel = source.anisoLevel;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static bool IsLinear(Texture2D texture) => !UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);

        private static Material SaveMaterial(Material material, string folder, string sourceName, HashSet<string> used)
        {
            var path = UniquePath(folder, sourceName + " (NonToon)", ".mat", used);
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                existing.shader = material.shader;
                existing.CopyPropertiesFromMaterial(material);
                existing.shaderKeywords = material.shaderKeywords;
                existing.renderQueue = material.renderQueue;
                EditorUtility.SetDirty(existing);
                return existing;
            }
            var copy = new Material(material) { name = Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(copy, path);
            return copy;
        }

        private static Mesh SaveMesh(Mesh mesh, string folder, string sourceName, HashSet<string> used)
        {
            var path = UniquePath(folder, sourceName + " (NonToon outline)", ".asset", used);
            var copy = Object.Instantiate(mesh);
            copy.name = Path.GetFileNameWithoutExtension(path);
            return (Mesh)ReplaceAsset(copy, path);
        }

        /// <summary>Updates an existing asset in place (keeping its GUID) or creates it.</summary>
        private static Object ReplaceAsset(Object asset, string path)
        {
            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing != null && existing.GetType() == asset.GetType())
            {
                EditorUtility.CopySerialized(asset, existing);
                Object.DestroyImmediate(asset);
                EditorUtility.SetDirty(existing);
                return existing;
            }
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }
    }
}
