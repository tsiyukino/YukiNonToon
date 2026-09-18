using System.Collections.Generic;
using System.Linq;
using TsiYuki.Core.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;

namespace TsiYuki.NonToon.Editor
{
    [CustomEditor(typeof(YukiNonToonConverter))]
    internal sealed class YukiNonToonConverterEditor : UnityEditor.Editor
    {
        internal static readonly YukiLocalizer L = new YukiLocalizer("moe.tsiyuki.nontoon");

        private static string _version;
        private AvatarConversion _analysis;
        private List<MaterialRow> _rows;
        private readonly HashSet<Material> _expanded = new HashSet<Material>();
        private string _exportFolder;

        private sealed class MaterialRow
        {
            public Material Source;
            public ConvertedMaterial Result;
            public int RendererCount;
        }

        private YukiNonToonConverter Target => (YukiNonToonConverter)target;

        private static string Version
        {
            get
            {
                if (_version != null) return _version;
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(YukiNonToonConverterEditor).Assembly);
                return _version = info != null ? "v" + info.version : "";
            }
        }

        private void OnEnable() => YukiLanguage.Changed += Repaint;

        private void OnDisable()
        {
            YukiLanguage.Changed -= Repaint;
            ClearAnalysis();
        }

        private void ClearAnalysis()
        {
            _analysis?.Dispose();
            _analysis = null;
            _rows = null;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            YukiGUI.Header("Yuki NonToon Converter", Version);
            EditorGUILayout.LabelField(L["ui.intro"], YukiGUI.WrapMini);

            if (!NonToonShader.IsInstalled)
            {
                EditorGUILayout.HelpBox(L["ui.nontoon_missing"], MessageType.Error);
                return;
            }
            var avatar = Target.GetComponentInParent<VRCAvatarDescriptor>(true);
            if (avatar == null)
            {
                EditorGUILayout.HelpBox(L["ui.no_avatar"], MessageType.Error);
                return;
            }
            var all = avatar.GetComponentsInChildren<YukiNonToonConverter>(true);
            if (all.Length > 1 && all[0] != Target)
                EditorGUILayout.HelpBox(L["ui.duplicate"], MessageType.Warning);

            DrawSettings();
            DrawMaterials(avatar);
            DrawChecks(avatar);
            DrawExport(avatar);
            serializedObject.ApplyModifiedProperties();
        }

        // ------------------------------------------------------------------ settings

        private void DrawSettings()
        {
            YukiGUI.Section(L["ui.settings"]);
            Toggle("convertOnBuild", "ui.convert_on_build");
            Toggle("skipOnAndroid", "ui.skip_android");
            Toggle("outlineMaskToVertexColor", "ui.outline_mask");
            Toggle("bakeMatCapBlur", "ui.matcap_blur");
            var brightness = serializedObject.FindProperty("shadowBrightness");
            EditorGUILayout.Slider(brightness, 0f, 1f, new GUIContent(L["ui.shadow_brightness"], L["ui.shadow_brightness.tip"]));
            EditorGUILayout.LabelField(L["ui.preview_hint"], YukiGUI.WrapMini);
        }

        private void Toggle(string property, string key)
        {
            var p = serializedObject.FindProperty(property);
            EditorGUILayout.PropertyField(p, new GUIContent(L[key], L.Has(key + ".tip") ? L[key + ".tip"] : null));
        }

        // ------------------------------------------------------------------ materials

        private void Analyze(VRCAvatarDescriptor avatar)
        {
            ClearAnalysis();
            _analysis = new AvatarConversion(ConverterSettings.From(Target), compressTextures: false, ownsAssets: true, maxTextureSize: 256);
            var counts = new Dictionary<Material, int>();
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
                foreach (var m in renderer.sharedMaterials.Distinct())
                    if (m != null && LilToon.Is(m)) counts[m] = counts.TryGetValue(m, out var n) ? n + 1 : 1;
            _rows = new List<MaterialRow>();
            try
            {
                var i = 0;
                foreach (var pair in counts.OrderBy(p => p.Key.name))
                {
                    EditorUtility.DisplayProgressBar("Yuki NonToon", pair.Key.name, (float)i++ / counts.Count);
                    _rows.Add(new MaterialRow { Source = pair.Key, RendererCount = pair.Value, Result = _analysis.Get(pair.Key) });
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private void DrawMaterials(VRCAvatarDescriptor avatar)
        {
            YukiGUI.Section(L["ui.materials"]);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(L["ui.materials.hint"], YukiGUI.WrapMini);
                if (GUILayout.Button(_rows == null ? L["ui.analyze"] : L["ui.reanalyze"], GUILayout.Width(110))) Analyze(avatar);
            }
            if (_rows == null) return;
            if (_rows.Count == 0) { EditorGUILayout.HelpBox(L["ui.no_liltoon"], MessageType.Info); return; }

            var excluded = serializedObject.FindProperty("excludedMaterials");
            foreach (var row in _rows)
            {
                var isExcluded = Target.excludedMaterials.Contains(row.Source);
                var status = isExcluded ? YukiStatus.None : row.Result?.Report.Worst ?? YukiStatus.Problem;
                using (new EditorGUILayout.HorizontalScope())
                {
                    var open = _expanded.Contains(row.Source);
                    var nowOpen = GUILayout.Toggle(open, open ? "▾" : "▸", EditorStyles.label, GUILayout.Width(14));
                    YukiGUI.StatusLabel(status, row.Source.name + (row.RendererCount > 1 ? $"  ×{row.RendererCount}" : ""), GUILayout.MinWidth(80));
                    if (nowOpen != open) { if (nowOpen) _expanded.Add(row.Source); else _expanded.Remove(row.Source); }
                    if (GUILayout.Button(L["ui.select"], EditorStyles.miniButton, GUILayout.Width(48))) EditorGUIUtility.PingObject(row.Source);
                    var keep = GUILayout.Toggle(isExcluded, L["ui.keep_liltoon"], EditorStyles.miniButton, GUILayout.Width(90));
                    if (keep != isExcluded)
                    {
                        Undo.RecordObject(Target, "Exclude material");
                        if (keep) Target.excludedMaterials.Add(row.Source); else Target.excludedMaterials.Remove(row.Source);
                        EditorUtility.SetDirty(Target);
                        serializedObject.Update();
                    }
                }
                if (!_expanded.Contains(row.Source) || row.Result == null || isExcluded) continue;
                using (new EditorGUI.IndentLevelScope(2))
                {
                    foreach (var line in row.Result.Report.Lines)
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(30);
                            YukiGUI.StatusLabel(line.Status, line.Text(L));
                        }
                    DrawOverrides(row);
                }
            }
            var counts = _rows.Where(r => !Target.excludedMaterials.Contains(r.Source)).GroupBy(r => r.Result?.Report.Worst ?? YukiStatus.Problem).ToDictionary(g => g.Key, g => g.Count());
            EditorGUILayout.LabelField(L.Tr("ui.summary",
                counts.TryGetValue(YukiStatus.Ok, out var ok) ? ok : 0,
                counts.TryGetValue(YukiStatus.Approximate, out var ap) ? ap : 0,
                counts.TryGetValue(YukiStatus.Problem, out var pr) ? pr : 0), YukiGUI.WrapMini);
        }

        private void DrawOverrides(MaterialRow row)
        {
            var material = row.Result.Material;
            var entry = Target.FindOverride(row.Source);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(30);
                GUILayout.Label(L["ui.overrides"], EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                var names = EditablePropertyNames(material);
                var picked = EditorGUILayout.Popup(0, names.Select(n => new GUIContent(PrettyName(n))).Prepend(new GUIContent(L["ui.add_override"])).ToArray(), GUILayout.Width(200));
                if (picked > 0)
                {
                    Undo.RecordObject(Target, "Add NonToon override");
                    if (entry == null) { entry = new MaterialOverride { source = row.Source }; Target.overrides.Add(entry); }
                    entry.properties.Add(CreateOverride(material, names[picked - 1]));
                    EditorUtility.SetDirty(Target);
                }
            }
            if (entry == null) return;
            PropertyOverride remove = null;
            foreach (var p in entry.properties)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(30);
                    EditorGUI.BeginChangeCheck();
                    var label = new GUIContent(PrettyName(p.name), p.name);
                    switch (p.type)
                    {
                        case PropertyOverrideType.Float: var f = EditorGUILayout.FloatField(label, p.floatValue); if (EditorGUI.EndChangeCheck()) Set(() => p.floatValue = f); break;
                        case PropertyOverrideType.Int: var i = EditorGUILayout.IntField(label, p.intValue); if (EditorGUI.EndChangeCheck()) Set(() => p.intValue = i); break;
                        case PropertyOverrideType.Color: var c = EditorGUILayout.ColorField(label, p.colorValue, true, true, true); if (EditorGUI.EndChangeCheck()) Set(() => p.colorValue = c); break;
                        case PropertyOverrideType.Vector: var v = EditorGUILayout.Vector4Field(label, p.vectorValue); if (EditorGUI.EndChangeCheck()) Set(() => p.vectorValue = v); break;
                        case PropertyOverrideType.Texture: var t = (Texture)EditorGUILayout.ObjectField(label, p.textureValue, typeof(Texture), false); if (EditorGUI.EndChangeCheck()) Set(() => p.textureValue = t); break;
                    }
                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22))) remove = p;
                }
            }
            if (remove != null)
            {
                Undo.RecordObject(Target, "Remove NonToon override");
                entry.properties.Remove(remove);
                if (entry.properties.Count == 0) Target.overrides.Remove(entry);
                EditorUtility.SetDirty(Target);
            }
        }

        private void Set(System.Action apply)
        {
            Undo.RecordObject(Target, "Edit NonToon override");
            apply();
            EditorUtility.SetDirty(Target);
        }

        private static string[] EditablePropertyNames(Material material)
        {
            var shader = material.shader;
            var names = new List<string>();
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                if ((shader.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0) continue;
                names.Add(shader.GetPropertyName(i));
            }
            return names.ToArray();
        }

        private static string PrettyName(string property) =>
            property.Replace("_jp_lilxyzw_nontoon_", "").TrimStart('_');

        private static PropertyOverride CreateOverride(Material material, string name)
        {
            var shader = material.shader;
            var index = shader.FindPropertyIndex(name);
            var p = new PropertyOverride { name = name };
            switch (shader.GetPropertyType(index))
            {
                case ShaderPropertyType.Color: p.type = PropertyOverrideType.Color; p.colorValue = material.GetColor(name); break;
                case ShaderPropertyType.Vector: p.type = PropertyOverrideType.Vector; p.vectorValue = material.GetVector(name); break;
                case ShaderPropertyType.Texture: p.type = PropertyOverrideType.Texture; p.textureValue = material.GetTexture(name); break;
                case ShaderPropertyType.Int: p.type = PropertyOverrideType.Int; p.intValue = NonToonShader.GetInt(material, name); break;
                default: p.type = PropertyOverrideType.Float; p.floatValue = material.GetFloat(name); break;
            }
            return p;
        }

        // ------------------------------------------------------------------ checks

        private void DrawChecks(VRCAvatarDescriptor avatar)
        {
            var noShadow = avatar.GetComponentsInChildren<Renderer>(true)
                .Where(r => !r.receiveShadows && r.sharedMaterials.Any(m => m != null && (LilToon.Is(m) || NonToonShader.IsNonToon(m))))
                .ToList();
            var missingModules = new List<string>();
            var probe = new Material(NonToonShader.Main);
            foreach (var (module, prop) in new[] { ("shade", "ShadeGradientIndex"), ("matcaps", "MatCapAdd"), ("rimlight", "RimLightColor"), ("rimshade", "RimShadeGradientIndex"), ("specular", "SpecularColor"), ("lighten", "LightBoost"), ("details", "DetailMask"), ("distancefade", "DistanceFade") })
                if (!NonToonShader.HasModule(probe, module, prop)) missingModules.Add(module);
            DestroyImmediate(probe);
            if (noShadow.Count == 0 && missingModules.Count == 0) return;

            YukiGUI.Section(L["ui.checks"]);
            if (missingModules.Count > 0)
                EditorGUILayout.HelpBox(L.Tr("ui.check_modules", string.Join(", ", missingModules)), MessageType.Warning);
            foreach (var renderer in noShadow)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(L.Tr("ui.check_receive_shadows", renderer.name), YukiGUI.WrapMini);
                    if (GUILayout.Button(L["ui.fix"], GUILayout.Width(60)))
                    {
                        Undo.RecordObject(renderer, "Receive shadows");
                        renderer.receiveShadows = true;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ export

        private void DrawExport(VRCAvatarDescriptor avatar)
        {
            YukiGUI.Section(L["ui.export"]);
            EditorGUILayout.LabelField(L["ui.export.hint"], YukiGUI.WrapMini);
            _exportFolder ??= NonToonExporter.DefaultFolder(avatar);
            _exportFolder = EditorGUILayout.TextField(L["ui.export.folder"], _exportFolder);
            if (GUILayout.Button(L["ui.export.button"]) &&
                EditorUtility.DisplayDialog("Yuki NonToon", L.Tr("ui.export.confirm", avatar.name, _exportFolder), L["ui.ok"], L["ui.cancel"]))
            {
                var count = NonToonExporter.Export(Target, avatar, _exportFolder.TrimEnd('/'));
                EditorUtility.DisplayDialog("Yuki NonToon", L.Tr("ui.export.done", count, _exportFolder), L["ui.ok"]);
                ClearAnalysis();
            }
        }
    }
}
