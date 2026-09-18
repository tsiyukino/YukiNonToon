using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using TsiYuki.Core.Editor;
using TsiYuki.NonToon.Editor;
using UnityEditor;
using UnityEngine;

[assembly: ExportsPlugin(typeof(NonToonPlugin))]

namespace TsiYuki.NonToon.Editor
{
    /// <summary>
    /// Build-time conversion. Settings are read and the component removed early (Resolving), the
    /// conversion itself runs late (Optimizing) after Modular Avatar and TexTransTool have produced the
    /// final materials, and before Avatar Optimizer merges meshes and materials.
    /// </summary>
    public sealed class NonToonPlugin : Plugin<NonToonPlugin>
    {
        public override string QualifiedName => "moe.tsiyuki.nontoon";
        public override string DisplayName => "Yuki NonToon Converter";

        private sealed class State
        {
            public ConverterSettings Settings;
        }

        protected override void Configure()
        {
            InPhase(BuildPhase.Resolving).Run("Read NonToon settings", ctx =>
            {
                var components = ctx.AvatarRootObject.GetComponentsInChildren<YukiNonToonConverter>(true);
                if (components.Length == 0) return;
                if (components.Length > 1)
                    Debug.LogWarning("[Yuki NonToon] More than one Yuki NonToon Converter on this avatar; using the first one.", components[0]);
                ctx.GetState<State>().Settings = ConverterSettings.From(components[0]);
                foreach (var component in components) Object.DestroyImmediate(component);
            });

            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("net.rs64.tex-trans-tool")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run("Convert lilToon to NonToon", Convert).PreviewingWith(new NonToonPreview());
                });
        }

        private static void Convert(BuildContext ctx)
        {
            var settings = ctx.GetState<State>().Settings;
            if (settings == null || !settings.ConvertOnBuild) return;
            if (!NonToonShader.IsInstalled)
            {
                Debug.LogError("[Yuki NonToon] NonToon is not installed; materials were left as lilToon.");
                return;
            }
            var target = EditorUserBuildSettings.activeBuildTarget;
            if (settings.SkipOnAndroid && (target == BuildTarget.Android || target == BuildTarget.iOS)) return;

            // Generated assets are handed to NDMF, which saves everything the avatar references.
            var conversion = new AvatarConversion(settings, compressTextures: true, ownsAssets: false);

            foreach (var renderer in ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                if (!conversion.ConvertMaterials(renderer.sharedMaterials, out var materials, out var outline)) continue;
                renderer.sharedMaterials = materials;
                if (outline.Any(o => o != null))
                {
                    var mesh = conversion.Meshes.Bake(AvatarConversion.GetMesh(renderer), outline);
                    if (mesh != null) AvatarConversion.SetMesh(renderer, mesh);
                }
            }

            var animators = ctx.Extension<AnimatorServicesContext>();
            animators.AnimationIndex.RewriteObjectCurves(obj => obj is Material m ? conversion.Replace(m) : obj);

            foreach (var texture in conversion.Textures.Generated) ctx.AssetSaver.SaveAsset(texture);
            foreach (var mesh in conversion.Meshes.Generated) ctx.AssetSaver.SaveAsset(mesh);
            foreach (var converted in conversion.Converted) ctx.AssetSaver.SaveAsset(converted.Material);

            var count = conversion.Converted.Count();
            var approximate = conversion.Converted.Count(c => c.Report.Worst != YukiStatus.Ok);
            Debug.Log($"[Yuki NonToon] Converted {count} lilToon material(s) to NonToon ({approximate} approximated).");
        }
    }
}
