using System;
using System.Collections.Generic;
using System.Linq;
using TsiYuki.Core.Editor;
using UnityEngine;

namespace TsiYuki.NonToon.Editor
{
    internal sealed class ConvertSettings
    {
        public bool BakeMatCapBlur = true;
        public bool OutlineMaskToVertexColor = true;
        public float ShadowBrightness;
        public MaterialOverride Override;

        public static ConvertSettings From(YukiNonToonConverter component, Material source) => new ConvertSettings
        {
            BakeMatCapBlur = component == null || component.bakeMatCapBlur,
            OutlineMaskToVertexColor = component == null || component.outlineMaskToVertexColor,
            ShadowBrightness = component != null ? component.shadowBrightness : 0f,
            Override = component != null ? component.FindOverride(source) : null,
        };
    }

    /// <summary>A request to write lilToon's outline width mask into the vertex colors of meshes using this material.</summary>
    internal sealed class OutlineMaskRequest
    {
        public Texture Mask;
        public Vector4 ScaleOffset = new Vector4(1, 1, 0, 0);
        public bool UseVertexColorR;   // lilToon _OutlineVertexR2Width == 1
    }

    internal sealed class ConvertedMaterial
    {
        public Material Source;
        public Material Material;
        public MaterialReport Report;
        public OutlineMaskRequest OutlineMask;
    }

    /// <summary>
    /// Converts one lilToon material into a new in-memory NonToon material. The source is only read.
    /// Every mapping follows the lilToon shader code; where NonToon has no equivalent the closest
    /// approximation is used and reported.
    /// </summary>
    internal static class MaterialConverter
    {
        // Shared Mask priorities: lower number wins a channel when more than four masks compete.
        private const int PrioMatCapAdd = 0, PrioSpecular = 1, PrioRimShade = 2, PrioMatCapMul = 3, PrioLighten = 4, PrioRimLight = 5, PrioBacklight = 6;
        private const float F0NonToon = 0.04f;

        public static ConvertedMaterial Convert(Material source, ConvertSettings settings, TextureBaker baker)
        {
            var lil = new LilToon(source);
            var report = new MaterialReport { Source = source };
            var shader = lil.IsFur ? NonToonShader.Fur : NonToonShader.Main;
            if (shader == null)
            {
                shader = NonToonShader.Main;
                if (lil.IsFur) report.Approx("feature.fur_missing");
            }
            var dst = new Material(shader) { name = source.name + " (NonToon)" };
            var ctx = new Context(lil, dst, report, settings, baker);

            ctx.Base();
            ctx.Rendering();
            ctx.Normals();
            ctx.Shading();
            ctx.RimShade();
            ctx.RimLight();
            ctx.MatCaps();
            ctx.Specular();
            ctx.Backlight();
            ctx.Lighten();
            ctx.Outline();
            ctx.DistanceFade();
            ctx.Stencil();
            if (lil.IsFur) ctx.Fur();
            ctx.Unsupported();
            ctx.Finish();
            ctx.ApplyOverride();

            return new ConvertedMaterial { Source = source, Material = dst, Report = report, OutlineMask = ctx.OutlineMask };
        }

        private sealed class Context
        {
            private readonly LilToon _l;
            private readonly Material _m;
            private readonly MaterialReport _r;
            private readonly ConvertSettings _s;
            private readonly TextureBaker _baker;
            private readonly List<(MaskSource source, int priority, Action<int> assign)> _masks = new List<(MaskSource, int, Action<int>)>();
            private readonly List<Func<float, Color>> _ramps = new List<Func<float, Color>>();
            private readonly List<string> _rampKeys = new List<string>();
            private Color? _albedo;
            private float _metallicDiffuse = 1f;
            public OutlineMaskRequest OutlineMask;

            public Context(LilToon lil, Material dst, MaterialReport report, ConvertSettings settings, TextureBaker baker)
            {
                _l = lil; _m = dst; _r = report; _s = settings; _baker = baker;
            }

            // ---------------------------------------------------------------- helpers

            private string Mod(string module, string property) => NonToonShader.Module(_m, module, property);

            private static Vector4 ST(LilToon l, string texture)
            {
                var s = l.Scale(texture); var o = l.Offset(texture);
                return new Vector4(s.x, s.y, o.x, o.y);
            }

            /// <summary>Average linear albedo (main texture × color), used where lilToon multiplies by albedo per pixel.</summary>
            private Color Albedo
            {
                get
                {
                    if (_albedo.HasValue) return _albedo.Value;
                    var color = _l.C("_Color").linear;
                    var main = _l.T("_MainTex");
                    var avg = main != null ? TextureBaker.Average(main) : Color.white;
                    _albedo = avg * color;
                    return _albedo.Value;
                }
            }

            private Color MaskedAlbedo(Texture mask)
            {
                if (mask == null) return Albedo;
                var main = _l.T("_MainTex");
                return TextureBaker.MaskedAverage(main, mask) * _l.C("_Color").linear;
            }

            private static float Gray(Color c) => (c.r + c.g + c.b) / 3f;
            private static Color Rgb(Color c, float a = 1f) => new Color(c.r, c.g, c.b, a);
            private static Color Scale(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, 1f);

            /// <summary>lilTooningScale without anti-aliasing: maps value through the [border ± blur/2] window.</summary>
            private static float Toon(float value, float border, float blur, float range = 0f)
            {
                var min = Mathf.Clamp01(border - blur * 0.5f - range);
                var max = Mathf.Clamp01(border + blur * 0.5f);
                var width = Mathf.Clamp01(max - min);
                if (width < 1e-4f) return value >= border ? 1f : 0f;
                return Mathf.Clamp01((value - min) / width);
            }

            private void RequestMask(MaskSource source, int priority, Action<int> assign) => _masks.Add((source, priority, assign));

            private int AddRamp(string key, Func<float, Color> ramp)
            {
                var existing = _rampKeys.IndexOf(key);
                if (existing >= 0) return existing;
                _rampKeys.Add(key);
                _ramps.Add(ramp);
                return _ramps.Count - 1;
            }

            // ---------------------------------------------------------------- base color

            public void Base()
            {
                var spec = new BaseBakeSpec
                {
                    Name = _l.Material.name + " Base",
                    MainTexture = _l.T("_MainTex"),
                    Color = _l.C("_Color"),
                };

                var hsvg = _l.V("_MainTexHSVG", new Vector4(0, 1, 1, 1));
                if (!_l.Has("_MainTexHSVG")) hsvg = new Vector4(0, 1, 1, 1);
                if (Mathf.Abs(hsvg.x) > 1e-4f || Mathf.Abs(hsvg.y - 1f) > 1e-4f || Mathf.Abs(hsvg.z - 1f) > 1e-4f || Mathf.Abs(hsvg.w - 1f) > 1e-4f)
                {
                    spec.ToneCorrection = true;
                    spec.HSVG = hsvg;
                    if (_l.T("_MainColorAdjustMask") != null) _r.Approx("feature.tone_mask");
                    else _r.Ok("feature.tone");
                }
                if (_l.F("_MainGradationStrength") > 0.001f && _l.T("_MainGradationTex") != null) _r.Lost("feature.gradation");

                AddLayer(spec, "_Main2nd", "feature.main2nd");
                AddLayer(spec, "_Main3rd", "feature.main3rd");

                var alphaMode = _l.I("_AlphaMaskMode");
                if (alphaMode != 0)
                {
                    spec.AlphaMaskMode = alphaMode;
                    spec.AlphaMask = _l.T("_AlphaMask");
                    spec.AlphaMaskScaleOffset = ST(_l, "_AlphaMask");
                    spec.AlphaMaskScale = _l.F("_AlphaMaskScale", 1f);
                    spec.AlphaMaskValue = _l.F("_AlphaMaskValue");
                    _r.Ok(spec.AlphaMask != null ? "feature.alphamask" : "feature.alphamask_const");
                }

                // lilToon Reflection with metallic darkens the diffuse color: col -= metallic * col.
                if (_l.On("_UseReflection"))
                {
                    var metallic = _l.F("_Metallic");
                    if (_l.T("_MetallicGlossMap") != null) _r.Approx("feature.metallic_map");
                    else if (metallic > 0.001f)
                    {
                        _metallicDiffuse = 1f - Mathf.Clamp01(metallic);
                        spec.Color = new Color(spec.Color.r * Mathf.LinearToGammaSpace(_metallicDiffuse), spec.Color.g * Mathf.LinearToGammaSpace(_metallicDiffuse), spec.Color.b * Mathf.LinearToGammaSpace(_metallicDiffuse), spec.Color.a);
                    }
                }

                var trivial = spec.MainTexture == null && spec.Layers.Count == 0 && spec.AlphaMaskMode == 0 &&
                              Mathf.Abs(spec.Color.r - 1f) < 1e-4f && Mathf.Abs(spec.Color.g - 1f) < 1e-4f && Mathf.Abs(spec.Color.b - 1f) < 1e-4f && Mathf.Abs(spec.Color.a - 1f) < 1e-4f;
                var texture = trivial ? null : _baker.BakeBase(spec, out var baked);
                _m.SetTexture("_BaseTexture", texture);
                if (spec.MainTexture != null)
                {
                    _m.SetTextureScale("_BaseTexture", _l.Scale("_MainTex"));
                    _m.SetTextureOffset("_BaseTexture", _l.Offset("_MainTex"));
                }
                _r.Ok("feature.base");
            }

            private void AddLayer(BaseBakeSpec spec, string prefix, string reportKey)
            {
                if (!_l.On("_Use" + prefix.Substring(1) + "Tex")) return;
                var tex = prefix + "Tex";
                var layer = new BaseBakeSpec.Layer
                {
                    Texture = _l.T(tex),
                    Scale = _l.Scale(tex),
                    Offset = _l.Offset(tex),
                    Color = _l.C("_Color" + prefix.Substring(5)),
                    BlendMask = _l.T(prefix + "BlendMask"),
                    BlendMode = _l.I(tex + "BlendMode"),
                    AlphaMode = _l.I(tex + "AlphaMode"),
                    EnableLighting = _l.F(prefix + "EnableLighting", 1f),
                    IsDecal = _l.On(tex + "IsDecal"),
                    ShouldCopy = _l.On(tex + "ShouldCopy"),
                    ShouldFlipCopy = _l.On(tex + "ShouldFlipCopy"),
                    Angle = _l.F(tex + "Angle"),
                };
                spec.Layers.Add(layer);
                var approximate = _l.I(tex + "_UVMode") != 0 || _l.On(tex + "IsLeftOnly") || _l.On(tex + "IsRightOnly") ||
                                  _l.On(tex + "ShouldFlipMirror") || _l.On(tex + "IsMSDF") || _l.T(prefix + "DissolveMask") != null ||
                                  _l.V(prefix + "DissolveParams").x > 0.5f || _l.V(tex + "_ScrollRotate").sqrMagnitude > 1e-8f;
                if (approximate) _r.Approx(reportKey + "_approx");
                else _r.Ok(reportKey);
            }

            // ---------------------------------------------------------------- rendering

            public void Rendering()
            {
                if (_l.IsFur)
                {
                    _m.renderQueue = _l.Material.renderQueue;
                    return;
                }
                var mode = _l.IsCutout ? NonToonShader.ModeCutout : _l.IsTransparent ? NonToonShader.ModeTransparent : NonToonShader.ModeOpaque;
                NonToonShader.ApplyRenderingMode(_m, mode, _l.Material.renderQueue);
                if (_l.Has("_Cutoff")) NonToonShader.SetFloat(_m, "_Cutoff", Mathf.Clamp(_l.F("_Cutoff", 0.5f), -0.001f, 1.001f));
                if (_l.Has("_Cull")) NonToonShader.SetInt(_m, "_Cull", _l.I("_Cull", 2));
                if (_l.Has("_ZWrite")) NonToonShader.SetInt(_m, "_ZWrite", _l.I("_ZWrite", 1));
                if (_l.IsRefractionOrGem) _r.Approx("feature.refraction");
                else _r.Ok(mode == NonToonShader.ModeOpaque ? "feature.mode_opaque" : mode == NonToonShader.ModeCutout ? "feature.mode_cutout" : "feature.mode_transparent");
                if (_l.ShaderName.IndexOf("TwoPass", StringComparison.OrdinalIgnoreCase) >= 0) _r.Approx("feature.twopass");
            }

            // ---------------------------------------------------------------- normals

            public void Normals()
            {
                var bump = _l.T("_BumpMap");
                var useBump = bump != null && (!_l.Has("_UseBumpMap") || _l.On("_UseBumpMap"));
                if (useBump)
                {
                    _m.SetTexture("_NormalMap", bump);
                    _m.SetTextureScale("_NormalMap", _l.Scale("_BumpMap"));
                    _m.SetTextureOffset("_NormalMap", _l.Offset("_BumpMap"));
                    NonToonShader.SetFloat(_m, "_NormalScale", _l.F("_BumpScale", 1f));
                    _r.Ok("feature.normal");
                }
                else
                {
                    _m.SetTexture("_NormalMap", null);
                }

                var bump2 = _l.T("_Bump2ndMap");
                if (_l.On("_UseBump2ndMap") && bump2 != null)
                {
                    var mapProp = Mod("details", "Detail0NormalMap");
                    if (mapProp == null) { _r.Lost("feature.module_missing", "Details"); return; }
                    _m.SetTexture(mapProp, bump2);
                    var texProp = Mod("details", "Detail0Texture");
                    if (texProp != null)
                    {
                        _m.SetTextureScale(texProp, _l.Scale("_Bump2ndMap"));
                        _m.SetTextureOffset(texProp, _l.Offset("_Bump2ndMap"));
                    }
                    NonToonShader.SetFloat(_m, Mod("details", "Detail0NormalScale"), _l.F("_Bump2ndScale", 1f));
                    NonToonShader.SetInt(_m, Mod("details", "Detail0UV"), Mathf.Clamp(_l.I("_Bump2ndMap_UVMode"), 0, 3));
                    var scaleMask = _l.T("_Bump2ndScaleMask");
                    if (scaleMask != null)
                        _m.SetTexture(Mod("details", "DetailMask"), _baker.PackMask(_l.Material.name + " Detail Mask", new[]
                        {
                            MaskSource.From(scaleMask, ST(_l, "_Bump2ndScaleMask")), MaskSource.Black, MaskSource.Black, MaskSource.Black,
                        }));
                    else
                        _m.SetTexture(Mod("details", "DetailMask"), null);
                    // Channels G/B/A of the detail mask must not apply detail textures: set their boosts neutral.
                    NonToonShader.SetModuleEnabled(_m, "details", true);
                    _r.Ok("feature.normal2");
                }
                else
                {
                    NonToonShader.SetModuleEnabled(_m, "details", false);
                }
            }

            // ---------------------------------------------------------------- shading

            public void Shading()
            {
                var indexProp = Mod("shade", "ShadeGradientIndex");
                if (!_l.On("_UseShadow"))
                {
                    NonToonShader.SetInt(_m, indexProp, -1);
                    return;
                }
                if (indexProp == null) { _r.Lost("feature.module_missing", "Shade"); return; }

                var strength = Mathf.Clamp01(_l.F("_ShadowStrength", 1f));
                var c1 = _l.C("_ShadowColor", new Color(0.82f, 0.76f, 0.85f)).linear;
                var c2 = _l.C("_Shadow2ndColor", new Color(0.68f, 0.66f, 0.79f, 0f));
                var c3 = _l.C("_Shadow3rdColor", new Color(0, 0, 0, 0));
                var c2a = c2.a; var c3a = c3.a;
                c2 = c2.linear; c3 = c3.linear;
                var b1 = _l.F("_ShadowBorder", 0.5f); var bl1 = _l.F("_ShadowBlur", 0.1f);
                var b2 = _l.F("_Shadow2ndBorder", 0.15f); var bl2 = _l.F("_Shadow2ndBlur", 0.1f);
                var b3 = _l.F("_Shadow3rdBorder", 0.25f); var bl3 = _l.F("_Shadow3rdBlur", 0.1f);
                var borderColor = _l.C("_ShadowBorderColor", new Color(1, 0.1f, 0)).linear;
                var borderRange = _l.F("_ShadowBorderRange");
                var mainStrength = Mathf.Clamp01(_l.F("_ShadowMainStrength"));
                var albedo = mainStrength > 0.001f ? Albedo : Color.white;
                var has3rd = _l.Has("_Shadow3rdColor");

                Color Ramp(float t)
                {
                    var ind = c1;
                    var l2 = c2a - Toon(t, b2, bl2) * c2a;
                    ind = Color.LerpUnclamped(ind, c2, l2);
                    if (has3rd)
                    {
                        var l3 = c3a - Toon(t, b3, bl3) * c3a;
                        ind = Color.LerpUnclamped(ind, c3, l3);
                    }
                    ind = Color.LerpUnclamped(ind, ind * albedo, mainStrength);
                    ind = new Color(Mathf.Min(ind.r, 1f), Mathf.Min(ind.g, 1f), Mathf.Min(ind.b, 1f), 1f);
                    var w = Toon(t, b1, bl1, borderRange);
                    ind = new Color(Mathf.Lerp(ind.r, 1f, w * borderColor.r), Mathf.Lerp(ind.g, 1f, w * borderColor.g), Mathf.Lerp(ind.b, 1f, w * borderColor.b), 1f);
                    var lx = Mathf.Lerp(1f, Toon(t, b1, bl1), strength);
                    return Color.LerpUnclamped(ind, Color.white, lx);
                }

                var key = TextureBaker.Key("shade", c1, c2, c3, c2a.ToString("R"), c3a.ToString("R"), b1, bl1, b2, bl2, b3, bl3, borderColor, borderRange, strength, mainStrength, albedo);
                var index = AddRamp(key, Ramp);
                NonToonShader.SetInt(_m, indexProp, index);
                NonToonShader.SetVector(_m, Mod("shade", "ShadeGradientRange"), new Vector4(0, 1, 0, 0));
                _r.Ok("feature.shadow");

                var maskType = _l.I("_ShadowMaskType");
                var strengthMask = _l.T("_ShadowStrengthMask");
                if (maskType == 2 && strengthMask != null)
                {
                    // lilToon's face SDF uses the same R/G (left/right) and B (blend) channels as NonToon's SDF.
                    NonToonShader.SetInt(_m, Mod("shade", "SDFType"), 1);
                    NonToonShader.SetTexture(_m, Mod("shade", "SDFMap"), strengthMask);
                    NonToonShader.SetFloat(_m, Mod("shade", "SDFBlendVertical"), Mathf.Clamp01(_l.F("_ShadowFlatBlur", 1f)));
                    _r.Approx("feature.shadow_sdf");
                }
                else
                {
                    NonToonShader.SetInt(_m, Mod("shade", "SDFType"), 0);
                    if (maskType == 1) _r.Approx("feature.shadow_flat");
                    else if (strengthMask != null && !IsMostlyWhite(strengthMask)) _r.Approx("feature.shadow_strength_mask");
                }
                if (_l.T("_ShadowBorderMask") != null) _r.Approx("feature.shadow_ao");
                if (_l.T("_ShadowColorTex") != null || _l.T("_Shadow2ndColorTex") != null) _r.Approx("feature.shadow_colortex");
                if (_l.T("_ShadowBlurMask") != null) _r.Approx("feature.shadow_blurmask");
            }

            private static bool IsMostlyWhite(Texture texture)
            {
                var avg = TextureBaker.Average(texture);
                return avg.r > 0.97f;
            }

            // ---------------------------------------------------------------- rim shade

            private bool _rimShadeUsed;

            public void RimShade()
            {
                if (!_l.On("_UseRimShade")) { NonToonShader.SetInt(_m, Mod("rimshade", "RimShadeGradientIndex"), -1); return; }
                var indexProp = Mod("rimshade", "RimShadeGradientIndex");
                if (indexProp == null) { _r.Lost("feature.module_missing", "RimShade"); return; }
                var color = _l.C("_RimShadeColor", new Color(0.5f, 0.5f, 0.5f));
                AddRimMultiplyRamp(indexProp, color.linear, color.a, _l.F("_RimShadeBorder", 0.5f), _l.F("_RimShadeBlur", 1f), Mathf.Max(0.01f, _l.F("_RimShadeFresnelPower", 1f)), "rimshade");
                var mask = _l.T("_RimShadeMask");
                RequestMask(mask != null ? MaskSource.From(mask, ST(_l, "_RimShadeMask")) : MaskSource.White, PrioRimShade,
                    ch => NonToonShader.SetInt(_m, Mod("rimshade", "RimShadeMaskChannel"), ch));
                _rimShadeUsed = true;
                _r.Ok("feature.rimshade");
            }

            /// <summary>
            /// NonToon samples the rim-shade ramp at x = 1 - mask·(1 - N·V). lilToon multiplies the color by
            /// lerp(1, color, toon(pow(1 - N·V, P)) · a). With mask = 1 this is a function of x = N·V.
            /// </summary>
            private void AddRimMultiplyRamp(string indexProp, Color color, float alpha, float border, float blur, float power, string tag)
            {
                Color Ramp(float x)
                {
                    var rim = Toon(Mathf.Pow(Mathf.Clamp01(1f - x), power), border, blur) * alpha;
                    return Color.LerpUnclamped(Color.white, Rgb(color), rim);
                }
                var index = AddRamp(TextureBaker.Key(tag, color, alpha, border, blur, power), Ramp);
                NonToonShader.SetInt(_m, indexProp, index);
            }

            // ---------------------------------------------------------------- rim light

            public void RimLight()
            {
                var colorProp = Mod("rimlight", "RimLightColor");
                if (!_l.On("_UseRim"))
                {
                    NonToonShader.SetColor(_m, colorProp, Color.black);
                    return;
                }
                var color = _l.C("_RimColor", new Color(0.66f, 0.5f, 0.48f));
                var mode = _l.I("_RimBlendMode", 1);
                var border = _l.F("_RimBorder", 0.5f);
                var blur = _l.F("_RimBlur", 0.65f);
                var power = Mathf.Max(0.01f, _l.F("_RimFresnelPower", 3.5f));

                if (mode == 3)
                {
                    // Multiply rim darkens edges: the same shape as lilToon rim shade, so use NonToon RimShade.
                    NonToonShader.SetColor(_m, colorProp, Color.black);
                    var indexProp = Mod("rimshade", "RimShadeGradientIndex");
                    if (!_rimShadeUsed && indexProp != null)
                    {
                        AddRimMultiplyRamp(indexProp, color.linear, color.a, border, blur, power, "rimmul");
                        var tex = _l.T("_RimColorTex");
                        RequestMask(tex != null ? MaskSource.From(tex, ST(_l, "_RimColorTex"), 1f, true) : MaskSource.White, PrioRimShade,
                            ch => NonToonShader.SetInt(_m, Mod("rimshade", "RimShadeMaskChannel"), ch));
                        _rimShadeUsed = true;
                        _r.Approx("feature.rim_multiply");
                    }
                    else _r.Lost("feature.rim_multiply_lost");
                    return;
                }

                if (colorProp == null) { _r.Lost("feature.module_missing", "RimLight"); return; }
                // lilToon thresholds pow(1 - N·V, P); NonToon thresholds 1 - N·V directly.
                var min = Mathf.Clamp01(border - blur * 0.5f);
                var max = Mathf.Clamp01(border + blur * 0.5f);
                var range = new Vector4(Mathf.Pow(min, 1f / power), Mathf.Max(Mathf.Pow(max, 1f / power), Mathf.Pow(min, 1f / power) + 0.001f), 0, 0);
                NonToonShader.SetColor(_m, colorProp, Scale(color.linear, color.a).gamma);
                NonToonShader.SetFloat(_m, Mod("rimlight", "RimLightMultiplyAlbedo"), Mathf.Clamp01(_l.F("_RimMainStrength")));
                NonToonShader.SetVector(_m, Mod("rimlight", "RimLightRange"), range);
                var rimTex = _l.T("_RimColorTex");
                RequestMask(rimTex != null ? MaskSource.From(rimTex, ST(_l, "_RimColorTex"), 1f, true) : MaskSource.White, PrioRimLight,
                    ch => NonToonShader.SetInt(_m, Mod("rimlight", "RimLightMaskChannel"), ch));
                if (mode == 0 || mode == 2 || rimTex != null) _r.Approx("feature.rim_approx");
                else _r.Ok("feature.rim");
            }

            // ---------------------------------------------------------------- MatCaps

            public void MatCaps()
            {
                var first = _l.On("_UseMatCap");
                var second = _l.On("_UseMatCap2nd");
                if (!first && !second) { NonToonShader.SetModuleEnabled(_m, "matcaps", false); return; }
                if (Mod("matcaps", "MatCapAdd") == null) { _r.Lost("feature.module_missing", "MatCaps"); return; }

                string addSource = null, mulSource = null;
                foreach (var prefix in new[] { "_MatCap", "_MatCap2nd" })
                {
                    if (!_l.On("_Use" + prefix.Substring(1))) continue;
                    var mode = _l.I(prefix + "BlendMode", 1);
                    if (mode == 3) { if (mulSource == null) mulSource = prefix; else _r.Lost("feature.matcap_extra", prefix); }
                    else { if (addSource == null) addSource = prefix; else _r.Lost("feature.matcap_extra", prefix); }
                }

                NonToonShader.SetModuleEnabled(_m, "matcaps", true);
                // Neutral defaults: nothing added, nothing multiplied.
                NonToonShader.SetTexture(_m, Mod("matcaps", "MatCapAdd"), null);
                NonToonShader.SetColor(_m, Mod("matcaps", "MatCapAddColor"), Color.black);
                NonToonShader.SetTexture(_m, Mod("matcaps", "MatCapMultiply"), null);
                NonToonShader.SetColor(_m, Mod("matcaps", "MatCapMultiplyColor"), Color.white);

                if (addSource != null) MatCapLayer(addSource, "MatCapAdd", PrioMatCapAdd);
                if (mulSource != null) MatCapLayer(mulSource, "MatCapMultiply", PrioMatCapMul);

                // lilToon "Normal" = lerp(col, matcap, a): the add slot carries matcap·a, and when the
                // multiply slot is free it dims the base by (1 - a) on the same mask.
                if (addSource != null && mulSource == null && _l.I(addSource + "BlendMode", 1) == 0)
                {
                    var a = MatCapOpacity(addSource);
                    NonToonShader.SetColor(_m, Mod("matcaps", "MatCapMultiplyColor"), new Color(1f - a, 1f - a, 1f - a, 1f).gamma);
                    RequestMask(MatCapMask(addSource), PrioMatCapAdd, ch => NonToonShader.SetInt(_m, Mod("matcaps", "MatCapMultiplyMaskChannel"), ch));
                }
            }

            private float MatCapOpacity(string prefix) => Mathf.Clamp01(_l.C(prefix + "Color").a * _l.F(prefix + "Blend", 1f));

            private MaskSource MatCapMask(string prefix)
            {
                var mask = _l.T(prefix + "BlendMask");
                return mask != null ? MaskSource.From(mask, ST(_l, prefix + "BlendMask")) : MaskSource.White;
            }

            private void MatCapLayer(string prefix, string slot, int priority)
            {
                var mode = _l.I(prefix + "BlendMode", 1);
                var color = _l.C(prefix + "Color");
                var opacity = MatCapOpacity(prefix);
                var mainStrength = Mathf.Clamp01(_l.F(prefix + "MainStrength"));
                var albedo = mainStrength > 0.001f || mode == 2 ? Albedo : Color.white;
                var lin = color.linear;
                var tint = new Color(
                    lin.r * Mathf.Lerp(1f, albedo.r, mainStrength),
                    lin.g * Mathf.Lerp(1f, albedo.g, mainStrength),
                    lin.b * Mathf.Lerp(1f, albedo.b, mainStrength), 1f);

                Color value;
                if (slot == "MatCapMultiply")
                    value = new Color(Mathf.Lerp(1f, tint.r, opacity), Mathf.Lerp(1f, tint.g, opacity), Mathf.Lerp(1f, tint.b, opacity), 1f);
                else
                {
                    var strength = opacity;
                    if (mode == 2) strength *= Mathf.Clamp01(1f - Gray(albedo)); // screen adds less on bright surfaces
                    value = Scale(tint, strength);
                }

                var texture = _l.T(prefix + "Tex");
                var lod = _l.F(prefix + "Lod");
                if (_s.BakeMatCapBlur && lod >= 0.5f) texture = _baker.BlurMatCap(texture, lod);
                NonToonShader.SetTexture(_m, Mod("matcaps", slot), texture);
                NonToonShader.SetColor(_m, Mod("matcaps", slot + "Color"), value.gamma);
                NonToonShader.SetFloat(_m, Mod("matcaps", slot + "Detail"), 1f);
                RequestMask(MatCapMask(prefix), priority, ch => NonToonShader.SetInt(_m, Mod("matcaps", slot + "MaskChannel"), ch));

                var exact = (mode == 1 && slot == "MatCapAdd") || (mode == 3 && slot == "MatCapMultiply");
                if (_l.On(prefix + "CustomNormal") || !exact || _l.F(prefix + "ShadowMask") > 0.01f) _r.Approx("feature.matcap_approx", prefix);
                else _r.Ok("feature.matcap", prefix);
            }

            // ---------------------------------------------------------------- specular

            public void Specular()
            {
                var colorProp = Mod("specular", "SpecularColor");
                var roughnessFrom = Mathf.Clamp01(_l.F("_Smoothness", 1f));
                NonToonShader.SetFloat(_m, "_Roughness", Mathf.Clamp((1f - roughnessFrom) * (1f - roughnessFrom), 0.002f, 1f));

                var reflection = _l.On("_UseReflection");
                var applySpecular = !_l.Has("_ApplySpecular") || _l.On("_ApplySpecular");
                if (!reflection || !applySpecular)
                {
                    NonToonShader.SetColor(_m, colorProp, Color.black);
                    if (reflection && _l.On("_ApplyReflection")) _r.Lost("feature.env_reflection");
                    return;
                }
                if (colorProp == null) { _r.Lost("feature.module_missing", "Specular"); return; }

                var color = _l.C("_ReflectionColor");
                var mask = _l.T("_ReflectionColorTex");
                // Metallic and albedo are estimated where the reflection is visible (inside its mask), so a
                // silver zipper on a black jacket keeps a silver F0 instead of the jacket's average.
                var metallicMap = _l.T("_MetallicGlossMap");
                var metallic = Mathf.Clamp01(_l.F("_Metallic"));
                if (metallicMap != null) metallic *= Mathf.Clamp01(TextureBaker.MaskedAverage(metallicMap, mask).r);
                var reflectance = Mathf.Clamp01(_l.F("_Reflectance", 0.04f));
                // lilToon F0 = lerp(Reflectance, albedo, Metallic); NonToon's Fresnel uses a fixed 0.04.
                var albedo = metallic > 0.001f ? MaskedAlbedo(mask) : Color.white;
                var f0 = new Color(Mathf.Lerp(reflectance, albedo.r, metallic), Mathf.Lerp(reflectance, albedo.g, metallic), Mathf.Lerp(reflectance, albedo.b, metallic));
                var lin = color.linear;
                var spec = new Color(
                    Mathf.Min(lin.r * color.a * f0.r / F0NonToon, 16f),
                    Mathf.Min(lin.g * color.a * f0.g / F0NonToon, 16f),
                    Mathf.Min(lin.b * color.a * f0.b / F0NonToon, 16f), 1f);
                NonToonShader.SetColor(_m, colorProp, spec.gamma);
                NonToonShader.SetFloat(_m, Mod("specular", "SpecularMultiplyAlbedo"), 0f);
                RequestMask(mask != null ? MaskSource.From(mask, ST(_l, "_ReflectionColorTex"), 1f, true) : MaskSource.White, PrioSpecular,
                    ch => NonToonShader.SetInt(_m, Mod("specular", "SpecularMaskChannel"), ch));

                if (_l.On("_SpecularToon")) _r.Approx("feature.specular_toon");
                else if (_l.T("_SmoothnessTex") != null) _r.Approx("feature.smoothness_map");
                else _r.Ok("feature.specular");
                if (_l.On("_ApplyReflection")) _r.Lost("feature.env_reflection");
            }

            // ---------------------------------------------------------------- backlight

            public void Backlight()
            {
                if (!_l.On("_UseBacklight"))
                {
                    // Pushes NonToon's backlight rim out of range; the base back-light falloff stays NonToon's own.
                    NonToonShader.SetFloat(_m, "_BacklightRange", -10f);
                    return;
                }
                var border = _l.F("_BacklightBorder", 0.35f);
                var blur = Mathf.Max(0.01f, _l.F("_BacklightBlur", 0.05f));
                NonToonShader.SetFloat(_m, "_BacklightRange", Mathf.Clamp(1f - border, 0f, 2f));
                NonToonShader.SetFloat(_m, "_BacklightSharpness", Mathf.Clamp(0.5f / blur, 0.5f, 20f));
                var mask = _l.T("_BacklightColorTex");
                RequestMask(mask != null ? MaskSource.From(mask, ST(_l, "_BacklightColorTex"), 1f, true) : MaskSource.White, PrioBacklight,
                    ch => NonToonShader.SetInt(_m, "_BacklightMaskChannel", ch));
                _r.Approx("feature.backlight");
            }

            // ---------------------------------------------------------------- lighten (emission / shadow brightness)

            public void Lighten()
            {
                var boostProp = Mod("lighten", "LightBoost");
                var emission = _l.On("_UseEmission");
                if (emission)
                {
                    if (boostProp == null) { _r.Lost("feature.module_missing", "Lighten"); return; }
                    var color = _l.C("_EmissionColor", Color.black);
                    var blend = _l.F("_EmissionBlend", 1f);
                    var strength = Mathf.Max(color.linear.r, color.linear.g, color.linear.b) * color.a * blend;
                    if (strength < 0.01f) { NonToonShader.SetFloat(_m, boostProp, 1f); NonToonShader.SetInt(_m, Mod("lighten", "LightBoostAsEmission"), 0); return; }
                    NonToonShader.SetFloat(_m, boostProp, Mathf.Clamp(strength, 0f, 10f));
                    NonToonShader.SetInt(_m, Mod("lighten", "LightBoostAsEmission"), 1);
                    var map = _l.T("_EmissionMap");
                    var blendMask = _l.T("_EmissionBlendMask");
                    var source = map != null ? MaskSource.From(map, ST(_l, "_EmissionMap"), 1f, true)
                        : blendMask != null ? MaskSource.From(blendMask, ST(_l, "_EmissionBlendMask"), 1f, true)
                        : MaskSource.White;
                    RequestMask(source, PrioLighten, ch => NonToonShader.SetInt(_m, Mod("lighten", "LightBoostMaskChannel"), ch));
                    _r.Approx("feature.emission");
                    if (map != null && blendMask != null) _r.Approx("feature.emission_two_masks");
                    if (_l.On("_UseEmission2nd")) _r.Lost("feature.emission2nd");
                    return;
                }

                if (_s.ShadowBrightness > 0.001f && boostProp != null)
                {
                    // Floor for the light color, so cast shadows keep some of lilToon's softness.
                    NonToonShader.SetFloat(_m, boostProp, _s.ShadowBrightness);
                    NonToonShader.SetInt(_m, Mod("lighten", "LightBoostAsEmission"), 1);
                    RequestMask(MaskSource.White, PrioLighten, ch => NonToonShader.SetInt(_m, Mod("lighten", "LightBoostMaskChannel"), ch));
                }
                else
                {
                    NonToonShader.SetFloat(_m, boostProp, 1f);
                    NonToonShader.SetInt(_m, Mod("lighten", "LightBoostAsEmission"), 0);
                }
            }

            // ---------------------------------------------------------------- outline

            public void Outline()
            {
                if (!_l.UsesOutlineShader)
                {
                    NonToonShader.SetFloat(_m, "_OutlineWidth", 0f);
                    return;
                }
                var width = _l.F("_OutlineWidth", 0.08f);
                NonToonShader.SetFloat(_m, "_OutlineWidth", width);
                NonToonShader.SetFloat(_m, "_OutlineZOffset", _l.F("_OutlineZBias"));

                // NonToon multiplies the shaded surface color by _OutlineColor; lilToon draws the color itself.
                var color = _l.C("_OutlineColor", new Color(0.6f, 0.56f, 0.73f)).linear;
                var outlineTex = _l.T("_OutlineTex");
                if (outlineTex != null) color *= TextureBaker.Average(outlineTex);
                var albedo = Albedo;
                Color Ratio(float c, float a) => new Color(Mathf.Clamp(c / Mathf.Max(a, 0.02f), 0f, 8f), 0, 0);
                var outline = new Color(Ratio(color.r, albedo.r).r, Ratio(color.g, albedo.g).r, Ratio(color.b, albedo.b).r, 1f);
                NonToonShader.SetColor(_m, "_OutlineColor", outline.gamma);
                _r.Approx("feature.outline_color");

                var widthMask = _l.T("_OutlineWidthMask");
                var vertexMode = _l.I("_OutlineVertexR2Width");
                if ((widthMask != null || vertexMode == 1) && _s.OutlineMaskToVertexColor)
                {
                    OutlineMask = new OutlineMaskRequest { Mask = widthMask, ScaleOffset = ST(_l, "_OutlineWidthMask"), UseVertexColorR = vertexMode == 1 };
                    NonToonShader.SetInt(_m, "_OutlineFromVertexColor", 1);
                    _r.Ok("feature.outline_mask");
                }
                else
                {
                    NonToonShader.SetInt(_m, "_OutlineFromVertexColor", 0);
                    if (widthMask != null) _r.Lost("feature.outline_mask_off");
                }
                if (vertexMode == 2) _r.Approx("feature.outline_vertex_rgba");
                if (_l.T("_OutlineVectorTex") != null) _r.Approx("feature.outline_vector");
                if (_l.F("_OutlineFixWidth") > 0.5f) _r.Approx("feature.outline_fixwidth");
            }

            // ---------------------------------------------------------------- distance fade

            public void DistanceFade()
            {
                var fade = _l.V("_DistanceFade", new Vector4(0.1f, 0.01f, 0, 0));
                var strengthProp = Mod("distancefade", "DistanceFadeStrength");
                if (fade.z < 0.001f) { NonToonShader.SetFloat(_m, strengthProp, 0f); return; }
                if (strengthProp == null) { _r.Lost("feature.module_missing", "DistanceFade"); return; }
                // lilToon fades toward _DistanceFadeColor as depth moves from x to y; NonToon fades to black
                // below its x..y range. lilToon's usual setup (x > y) is the same direction with ends swapped.
                var near = Mathf.Min(fade.x, fade.y);
                var far = Mathf.Max(fade.x, fade.y);
                NonToonShader.SetVector(_m, Mod("distancefade", "DistanceFade"), new Vector4(near, Mathf.Max(far, near + 0.001f), 0, 0));
                NonToonShader.SetFloat(_m, strengthProp, Mathf.Clamp01(fade.z));
                var fadeColor = _l.C("_DistanceFadeColor", Color.black);
                if (fadeColor.r > 0.02f || fadeColor.g > 0.02f || fadeColor.b > 0.02f || _l.On("_DistanceFadeMode") || fade.x < fade.y)
                    _r.Approx("feature.distance_fade_approx");
                else _r.Ok("feature.distance_fade");
            }

            // ---------------------------------------------------------------- stencil

            public void Stencil()
            {
                foreach (var p in new[] { "_StencilRef", "_StencilComp", "_StencilPass", "_OutlineStencilRef", "_OutlineStencilComp", "_OutlineStencilPass" })
                    if (_l.Has(p)) NonToonShader.SetInt(_m, p, _l.I(p));
                var custom = _l.I("_StencilRef") != 0 || (_l.Has("_StencilComp") && _l.I("_StencilComp") != 8);
                if (!custom) return;
                var unsupported = (_l.Has("_StencilReadMask") && _l.I("_StencilReadMask", 255) != 255) ||
                                  (_l.Has("_StencilWriteMask") && _l.I("_StencilWriteMask", 255) != 255) ||
                                  _l.I("_StencilFail") != 0 || _l.I("_StencilZFail") != 0;
                if (unsupported) _r.Approx("feature.stencil_approx");
                else _r.Ok("feature.stencil");
            }

            // ---------------------------------------------------------------- fur

            public void Fur()
            {
                if (!_m.HasProperty("_FurNoiseMask")) return;
                var noise = _l.T("_FurNoiseMask");
                if (noise != null)
                {
                    _m.SetTexture("_FurNoiseMask", noise);
                    var scale = _l.Scale("_FurNoiseMask");
                    NonToonShader.SetFloat(_m, "_FurNoiseTiling", Mathf.Max(0.0001f, Mathf.Sqrt(Mathf.Abs(scale.x * scale.y))));
                }
                if (_l.Has("_FurLayerNum")) NonToonShader.SetInt(_m, "_FurSubdivision", Mathf.Clamp(_l.I("_FurLayerNum", 2), 1, 3));
                var v = _l.V("_FurVector", new Vector4(0, 0, 1, 0.02f));
                var dir = new Vector3(v.x, v.y, v.z);
                var disp = dir.sqrMagnitude > 1e-7f ? dir.normalized * v.w : Vector3.zero;
                NonToonShader.SetVector(_m, "_FurVector", new Vector4(disp.x, disp.y, disp.z, 0));
                _r.Approx("feature.fur");
            }

            // ---------------------------------------------------------------- unsupported

            public void Unsupported()
            {
                if (_l.On("_UseGlitter")) _r.Lost("feature.glitter");
                if (_l.On("_UseDissolve") || _l.V("_DissolveParams").x > 0.5f) _r.Lost("feature.dissolve");
                if (_l.On("_UseAudioLink")) _r.Lost("feature.audiolink");
                if (_l.On("_UseParallax") || _l.On("_UsePOM")) _r.Lost("feature.parallax");
                if (_l.On("_UseAnisotropy")) _r.Lost("feature.anisotropy");
                if (_l.V("_MainTex_ScrollRotate").sqrMagnitude > 1e-8f) _r.Lost("feature.uv_animation");
                if (_l.On("_UseClippingCanceller")) _r.Approx("feature.clipping_canceller");
            }

            // ---------------------------------------------------------------- shared mask & gradients

            public void Finish()
            {
                // Textured masks first, by priority; identical sources share a channel.
                var textured = _masks.Where(m => m.source.Texture != null).OrderBy(m => m.priority).ToList();
                var channels = new List<MaskSource>();
                var assignments = new List<(Action<int> assign, int channel)>();
                foreach (var request in textured)
                {
                    var existing = channels.FindIndex(c => c.Key == request.source.Key);
                    if (existing < 0)
                    {
                        if (channels.Count >= 4) { _r.Approx("feature.mask_full"); assignments.Add((request.assign, -1)); continue; }
                        channels.Add(request.source);
                        existing = channels.Count - 1;
                    }
                    assignments.Add((request.assign, existing));
                }
                var whiteChannel = channels.Count < 4 ? channels.Count : -1;
                foreach (var request in _masks.Where(m => m.source.Texture == null))
                    assignments.Add((request.assign, whiteChannel));

                if (channels.Count > 0)
                {
                    var packed = new MaskSource[4];
                    for (var i = 0; i < 4; i++) packed[i] = i < channels.Count ? channels[i] : MaskSource.White;
                    _m.SetTexture("_SharedMask", _baker.PackMask(_l.Material.name + " Shared Mask", packed));
                }
                else _m.SetTexture("_SharedMask", null);

                foreach (var (assign, channel) in assignments)
                    assign(channel >= 0 ? channel : (whiteChannel >= 0 ? whiteChannel : 3));

                if (_ramps.Count > 0)
                    _m.SetTexture("_SharedGradients", _baker.BuildGradients(_l.Material.name + " Gradients", _ramps, string.Join("||", _rampKeys)));
                else
                    _m.SetTexture("_SharedGradients", null);
            }

            public void ApplyOverride()
            {
                var o = _s.Override;
                if (o == null) return;
                foreach (var p in o.properties)
                {
                    if (p == null || string.IsNullOrEmpty(p.name) || !_m.HasProperty(p.name)) continue;
                    switch (p.type)
                    {
                        case PropertyOverrideType.Float: _m.SetFloat(p.name, p.floatValue); break;
                        case PropertyOverrideType.Int: NonToonShader.SetInt(_m, p.name, p.intValue); break;
                        case PropertyOverrideType.Color: _m.SetColor(p.name, p.colorValue); break;
                        case PropertyOverrideType.Vector: _m.SetVector(p.name, p.vectorValue); break;
                        case PropertyOverrideType.Texture: _m.SetTexture(p.name, p.textureValue); break;
                    }
                }
                if (o.properties.Count > 0) _r.Ok("feature.override", o.properties.Count);
            }
        }
    }
}
