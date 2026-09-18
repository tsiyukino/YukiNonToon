using System;
using System.Collections.Generic;
using UnityEngine;

namespace TsiYuki.NonToon.Editor
{
    /// <summary>
    /// Produces the textures NonToon needs (baked base color, shared mask, blurred MatCaps, gradient array).
    /// One instance per build or preview session: generated textures are owned by that session and
    /// destroyed with it, so a preview texture is never saved into a build and vice versa.
    /// Source pixels are cached across sessions, keyed by texture instance and update count.
    /// </summary>
    internal sealed class TextureBaker : IDisposable
    {
        // LRU cache of source pixels shared by all sessions, bounded by memory rather than count.
        private const long CacheBudget = 768L * 1024 * 1024;
        private static readonly Dictionary<(int, uint, int), LinkedListNode<((int, uint, int) key, Pixels pixels)>> CacheIndex =
            new Dictionary<(int, uint, int), LinkedListNode<((int, uint, int) key, Pixels pixels)>>();
        private static readonly LinkedList<((int, uint, int) key, Pixels pixels)> CacheOrder = new LinkedList<((int, uint, int) key, Pixels pixels)>();
        private static long _cacheBytes;
        private static readonly Dictionary<(int, uint), Color> AverageCache = new Dictionary<(int, uint), Color>();

        private readonly Dictionary<string, Texture> _generated = new Dictionary<string, Texture>();
        private readonly bool _compress;
        private readonly int _maxSize;

        /// <summary>All textures this session created (for disposal or export).</summary>
        public IEnumerable<Texture> Generated => _generated.Values;

        /// <param name="maxSize">Largest edge of any baked texture (preview uses a small value to stay responsive).</param>
        public TextureBaker(bool compress, int maxSize = 4096)
        {
            _compress = compress;
            _maxSize = Mathf.Max(16, maxSize);
        }

        public int MaxSize => _maxSize;

        /// <summary>Reads a texture at most <paramref name="maxSize"/> pixels on its longest edge, cached.</summary>
        public static Pixels ReadAt(Texture texture, int maxSize)
        {
            if (texture == null) return null;
            var key = (texture.GetInstanceID(), texture.updateCount, maxSize);
            if (CacheIndex.TryGetValue(key, out var node))
            {
                CacheOrder.Remove(node);
                CacheOrder.AddFirst(node);
                return node.Value.pixels;
            }
            var pixels = Pixels.Read(texture, maxSize);
            if (pixels == null) return null;
            node = CacheOrder.AddFirst((key, pixels));
            CacheIndex[key] = node;
            _cacheBytes += pixels.ByteSize;
            while (_cacheBytes > CacheBudget && CacheOrder.Count > 1)
            {
                var last = CacheOrder.Last;
                CacheOrder.RemoveLast();
                CacheIndex.Remove(last.Value.key);
                _cacheBytes -= last.Value.pixels.ByteSize;
            }
            return pixels;
        }

        private Pixels Read(Texture texture) => ReadAt(texture, _maxSize);

        /// <summary>Average linear color, from a small read so it is cheap.</summary>
        public static Color Average(Texture texture)
        {
            if (texture == null) return Color.white;
            var key = (texture.GetInstanceID(), texture.updateCount);
            if (AverageCache.TryGetValue(key, out var avg)) return avg;
            avg = ReadAt(texture, 128)?.Average() ?? Color.white;
            AverageCache[key] = avg;
            return avg;
        }

        /// <summary>
        /// Average linear color of <paramref name="texture"/> weighted by the luminance of <paramref name="mask"/>
        /// (same UVs), so a property that only matters inside a mask is estimated from those pixels.
        /// </summary>
        public static Color MaskedAverage(Texture texture, Texture mask)
        {
            if (mask == null) return Average(texture);
            var m = ReadAt(mask, 128);
            var t = texture != null ? ReadAt(texture, 128) : null;
            if (m == null) return Average(texture);
            var sum = Color.clear;
            var weight = 0f;
            for (var y = 0; y < m.Height; y++)
            for (var x = 0; x < m.Width; x++)
            {
                var mc = m.Get(y * m.Width + x);
                var w = (mc.r + mc.g + mc.b) / 3f;
                if (w <= 0.001f) continue;
                var c = t == null ? Color.white : t.SampleRepeat((x + 0.5f) / m.Width, (y + 0.5f) / m.Height);
                sum += c * w;
                weight += w;
            }
            return weight > 0.001f ? sum / weight : Average(texture);
        }

        public static void ClearCaches()
        {
            CacheIndex.Clear();
            CacheOrder.Clear();
            _cacheBytes = 0;
            AverageCache.Clear();
        }

        private Texture Cached(string key, Func<Texture> create)
        {
            if (_generated.TryGetValue(key, out var texture) && texture != null) return texture;
            texture = create();
            if (texture != null) _generated[key] = texture;
            return texture;
        }

        public void Dispose()
        {
            foreach (var texture in _generated.Values)
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            _generated.Clear();
        }

        public static string Key(params object[] parts)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                if (part is UnityEngine.Object obj) sb.Append(obj != null ? obj.GetInstanceID().ToString() : "null");
                else if (part is Color c) sb.Append(c.r.ToString("R")).Append(',').Append(c.g.ToString("R")).Append(',').Append(c.b.ToString("R")).Append(',').Append(c.a.ToString("R"));
                else sb.Append(part);
                sb.Append('|');
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ base color

        /// <summary>
        /// lilToon base color: main texture → tone correction (HSVG) → × _Color → Main 2nd/3rd layers →
        /// alpha mask, all in linear space like the shader. Returns the source texture untouched when
        /// nothing needs baking.
        /// </summary>
        public Texture BakeBase(BaseBakeSpec spec, out bool baked)
        {
            baked = spec.NeedsBake;
            if (!baked) return spec.MainTexture;
            return Cached(spec.Key, () =>
            {
                var main = spec.MainTexture != null ? Read(spec.MainTexture) : null;
                var width = main?.Width ?? 4;
                var height = main?.Height ?? 4;
                foreach (var layer in spec.Layers)
                {
                    var t = layer.Texture != null ? Read(layer.Texture) : null;
                    if (t != null) { width = Mathf.Max(width, t.Width); height = Mathf.Max(height, t.Height); }
                }
                if (spec.AlphaMask != null)
                {
                    var am = Read(spec.AlphaMask);
                    if (am != null) { width = Mathf.Max(width, am.Width); height = Mathf.Max(height, am.Height); }
                }
                var sameSize = main != null && main.Width == width && main.Height == height;

                var result = new Pixels(width, height, true);
                var color = spec.Color.linear;
                var layerPixels = new List<(BaseBakeSpec.Layer layer, Pixels tex, Pixels mask)>();
                foreach (var layer in spec.Layers)
                    layerPixels.Add((layer, layer.Texture != null ? Read(layer.Texture) : null, layer.BlendMask != null ? Read(layer.BlendMask) : null));
                var alphaMask = spec.AlphaMask != null ? Read(spec.AlphaMask) : null;
                var layerColors = spec.Layers.ConvertAll(l => l.Color.linear);

                System.Threading.Tasks.Parallel.For(0, height, y =>
                {
                    for (var x = 0; x < width; x++)
                    {
                    var u = (x + 0.5f) / width;
                    var v = (y + 0.5f) / height;
                    var c = main == null ? Color.white : sameSize ? main.Get(y * width + x) : main.SampleRepeat(u, v);
                    if (spec.ToneCorrection) c = ToneCorrection(c, spec.HSVG);
                    c *= color;
                    for (var li = 0; li < layerPixels.Count; li++)
                    {
                        var (layer, tex, mask) = layerPixels[li];
                        var src = layerColors[li];
                        var luv = layer.TransformUV(u, v, out var inside);
                        if (tex != null) src *= layer.IsDecal ? tex.SampleClamp(luv.x, luv.y) : tex.SampleRepeat(luv.x, luv.y);
                        if (layer.IsDecal && !inside) src.a = 0f;
                        if (mask != null) src.a *= mask.SampleRepeat(u, v).r;
                        if (layer.AlphaMode != 0)
                        {
                            if (layer.AlphaMode == 1) c.a = src.a;
                            else if (layer.AlphaMode == 2) c.a *= src.a;
                            else if (layer.AlphaMode == 3) c.a = Mathf.Clamp01(c.a + src.a);
                            else if (layer.AlphaMode == 4) c.a = Mathf.Clamp01(c.a - src.a);
                            src.a = 1f;
                        }
                        c = BlendColor(c, src, src.a * layer.EnableLighting, layer.BlendMode);
                    }
                    if (spec.AlphaMaskMode != 0)
                    {
                        var m = alphaMask != null ? alphaMask.SampleRepeat(u * spec.AlphaMaskScaleOffset.x + spec.AlphaMaskScaleOffset.z, v * spec.AlphaMaskScaleOffset.y + spec.AlphaMaskScaleOffset.w).r : 1f;
                        c.a = ApplyAlphaMask(spec.AlphaMaskMode, c.a, Mathf.Clamp01(m * spec.AlphaMaskScale + spec.AlphaMaskValue));
                    }
                    result.Set(y * width + x, c);
                    }
                });
                return result.ToTexture(spec.Name, _compress, false, TextureWrapMode.Repeat, FilterMode.Bilinear, spec.MainTexture != null ? Mathf.Max(1, spec.MainTexture.anisoLevel) : 1);
            });
        }

        public static float ApplyAlphaMask(int mode, float alpha, float value)
        {
            switch (mode)
            {
                case 1: return value;
                case 2: return alpha * value;
                case 3: return Mathf.Clamp01(alpha + value);
                case 4: return Mathf.Clamp01(alpha - value);
                default: return alpha;
            }
        }

        /// <summary>lilBlendColor: 0 normal, 1 add, 2 screen, 3 multiply. Alpha is preserved.</summary>
        public static Color BlendColor(Color dst, Color src, float srcAlpha, int mode)
        {
            Color outCol;
            switch (mode)
            {
                case 1: outCol = dst + src; break;
                case 2:
                    var ad = dst + src; var mu = dst * src;
                    outCol = new Color(Mathf.Max(ad.r - mu.r, dst.r), Mathf.Max(ad.g - mu.g, dst.g), Mathf.Max(ad.b - mu.b, dst.b));
                    break;
                case 3: outCol = dst * src; break;
                default: outCol = src; break;
            }
            var a = Mathf.Clamp01(srcAlpha);
            return new Color(Mathf.LerpUnclamped(dst.r, outCol.r, a), Mathf.LerpUnclamped(dst.g, outCol.g, a), Mathf.LerpUnclamped(dst.b, outCol.b, a), dst.a);
        }

        /// <summary>Port of lilToneCorrection (gamma, then HSV shift/scale).</summary>
        public static Color ToneCorrection(Color c, Vector4 hsvg)
        {
            var r = Mathf.Pow(Mathf.Abs(c.r), hsvg.w);
            var g = Mathf.Pow(Mathf.Abs(c.g), hsvg.w);
            var b = Mathf.Pow(Mathf.Abs(c.b), hsvg.w);
            Vector4 p = b > g ? new Vector4(b, g, -1f, 2f / 3f) : new Vector4(g, b, 0f, -1f / 3f);
            Vector4 q = p.x > r ? new Vector4(p.x, p.y, p.w, r) : new Vector4(r, p.y, p.z, p.x);
            var d = q.x - Mathf.Min(q.w, q.y);
            const float e = 1e-10f;
            var h = Mathf.Abs(q.z + (q.w - q.y) / (6f * d + e));
            var s = d / (q.x + e);
            var val = q.x;
            h += hsvg.x;
            s = Mathf.Clamp01(s * hsvg.y);
            val = Mathf.Clamp01(val * hsvg.z);
            float Channel(float offset)
            {
                var f = h + offset;
                f -= Mathf.Floor(f);
                return Mathf.Clamp01(Mathf.Abs(f * 6f - 3f) - 1f);
            }
            return new Color(
                val - val * s + val * s * Channel(1f),
                val - val * s + val * s * Channel(2f / 3f),
                val - val * s + val * s * Channel(1f / 3f),
                c.a);
        }

        // ------------------------------------------------------------------ shared mask

        /// <summary>Packs up to four grayscale sources into one RGBA mask. A null channel is white.</summary>
        public Texture PackMask(string name, MaskSource[] channels)
        {
            var key = "mask|" + string.Join("|", Array.ConvertAll(channels, c => c.Key));
            return Cached(key, () =>
            {
                var sources = new Pixels[4];
                int width = 4, height = 4;
                for (var i = 0; i < 4; i++)
                {
                    if (channels[i].Texture == null) continue;
                    sources[i] = Read(channels[i].Texture);
                    if (sources[i] == null) continue;
                    width = Mathf.Max(width, sources[i].Width);
                    height = Mathf.Max(height, sources[i].Height);
                }
                var result = new Pixels(width, height, false);
                System.Threading.Tasks.Parallel.For(0, height, y =>
                {
                    var value = new float[4];
                    for (var x = 0; x < width; x++)
                    {
                    var u = (x + 0.5f) / width;
                    var v = (y + 0.5f) / height;
                    for (var i = 0; i < 4; i++)
                    {
                        var ch = channels[i];
                        if (sources[i] == null) { value[i] = ch.Constant; continue; }
                        var s = sources[i].SampleRepeat(u * ch.ScaleOffset.x + ch.ScaleOffset.z, v * ch.ScaleOffset.y + ch.ScaleOffset.w);
                        var m = ch.UseLuminance ? (s.r + s.g + s.b) / 3f : s.r;
                        value[i] = Mathf.Clamp01(m * ch.Constant);
                    }
                    result.Set(y * width + x, new Color(value[0], value[1], value[2], value[3]));
                    }
                });
                return result.ToTexture(name, _compress, true);
            });
        }

        // ------------------------------------------------------------------ MatCap blur

        /// <summary>lilToon samples MatCaps at mip level _MatCapLod; NonToon always uses mip 0.</summary>
        public Texture BlurMatCap(Texture source, float lod)
        {
            if (source == null || lod < 0.5f) return source;
            return Cached(Key("matcapblur", source, source.updateCount, lod.ToString("F2")), () =>
            {
                var src = ReadAt(source, 512);
                if (src == null) return null;
                var srgb = src.SRGB;
                var divisor = Mathf.Pow(2f, lod);
                var w = Mathf.Max(2, Mathf.RoundToInt(src.Width / divisor));
                var h = Mathf.Max(2, Mathf.RoundToInt(src.Height / divisor));
                var small = new Pixels(w, h, false);
                var smallData = new Color[w * h];
                var bx = src.Width / (float)w;
                var by = src.Height / (float)h;
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var sum = Color.clear; var n = 0;
                    var x0 = Mathf.FloorToInt(x * bx); var x1 = Mathf.Max(x0 + 1, Mathf.FloorToInt((x + 1) * bx));
                    var y0 = Mathf.FloorToInt(y * by); var y1 = Mathf.Max(y0 + 1, Mathf.FloorToInt((y + 1) * by));
                    for (var sy = y0; sy < y1 && sy < src.Height; sy++)
                    for (var sx = x0; sx < x1 && sx < src.Width; sx++) { sum += src.Get(sy * src.Width + sx); n++; }
                    smallData[y * w + x] = n == 0 ? Color.black : sum / n;
                }
                var outW = Mathf.Clamp(src.Width, 4, 64);
                var outH = Mathf.Clamp(src.Height, 4, 64);
                var output = new Pixels(outW, outH, srgb);
                for (var y = 0; y < outH; y++)
                for (var x = 0; x < outW; x++)
                {
                    var u = (x + 0.5f) / outW * w - 0.5f;
                    var v = (y + 0.5f) / outH * h - 0.5f;
                    var ix = Mathf.Clamp(Mathf.FloorToInt(u), 0, w - 1); var iy = Mathf.Clamp(Mathf.FloorToInt(v), 0, h - 1);
                    var ix1 = Mathf.Min(ix + 1, w - 1); var iy1 = Mathf.Min(iy + 1, h - 1);
                    var fx = Mathf.Clamp01(u - ix); var fy = Mathf.Clamp01(v - iy);
                    var a = Color.Lerp(smallData[iy * w + ix], smallData[iy * w + ix1], fx);
                    var b = Color.Lerp(smallData[iy1 * w + ix], smallData[iy1 * w + ix1], fx);
                    output.Set(y * outW + x, Color.Lerp(a, b, fy));
                }
                return output.ToTexture(source.name + " (blur)", false, false, TextureWrapMode.Clamp);
            });
        }

        // ------------------------------------------------------------------ gradients

        /// <summary>
        /// Builds the Texture2DArray bound to _SharedGradients, one 128×1 slice per ramp, matching Shader
        /// Core's .scgradients importer. Ramps are functions of t ∈ [0,1] returning linear colors.
        /// </summary>
        public Texture BuildGradients(string name, IReadOnlyList<Func<float, Color>> ramps, string key)
        {
            if (ramps.Count == 0) return null;
            return Cached("gradients|" + key, () =>
            {
                const int size = 128;
                var texture = new Texture2DArray(size, 1, ramps.Count, TextureFormat.RGBA32, true, false)
                {
                    name = name,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                for (var index = 0; index < ramps.Count; index++)
                {
                    var pixels = new Color32[size];
                    for (var i = 0; i < size; i++)
                    {
                        var c = ramps[index](i / (float)size);
                        pixels[i] = new Color(Mathf.LinearToGammaSpace(Mathf.Clamp01(c.r)), Mathf.LinearToGammaSpace(Mathf.Clamp01(c.g)), Mathf.LinearToGammaSpace(Mathf.Clamp01(c.b)), 1f);
                    }
                    texture.SetPixels32(pixels, index, 0);
                }
                texture.Apply(true, false);
                return texture;
            });
        }
    }

    internal struct MaskSource
    {
        public Texture Texture;
        public Vector4 ScaleOffset;
        public float Constant;     // multiplier when textured, value when not
        public bool UseLuminance;

        public static MaskSource White => new MaskSource { Constant = 1f, ScaleOffset = new Vector4(1, 1, 0, 0) };
        public static MaskSource Black => new MaskSource { Constant = 0f, ScaleOffset = new Vector4(1, 1, 0, 0) };

        public static MaskSource From(Texture texture, Vector4 scaleOffset, float multiplier = 1f, bool luminance = false) =>
            new MaskSource { Texture = texture, ScaleOffset = scaleOffset, Constant = multiplier, UseLuminance = luminance };

        public string Key => TextureBaker.Key(Texture, Texture != null ? Texture.updateCount : 0u, ScaleOffset, Constant.ToString("R"), UseLuminance);
        public bool IsWhite => Texture == null && Constant >= 0.999f;
    }

    internal sealed class BaseBakeSpec
    {
        public string Name;
        public Texture MainTexture;
        public Color Color = Color.white;
        public bool ToneCorrection;
        public Vector4 HSVG = new Vector4(0, 1, 1, 1);
        public List<Layer> Layers = new List<Layer>();
        public int AlphaMaskMode;
        public Texture AlphaMask;
        public Vector4 AlphaMaskScaleOffset = new Vector4(1, 1, 0, 0);
        public float AlphaMaskScale = 1f;
        public float AlphaMaskValue;

        public sealed class Layer
        {
            public Texture Texture;
            public Vector2 Scale = Vector2.one;
            public Vector2 Offset;
            public Color Color = Color.white;
            public Texture BlendMask;
            public int BlendMode;
            public int AlphaMode;
            public float EnableLighting = 1f;
            public bool IsDecal;
            public bool ShouldCopy;
            public bool ShouldFlipCopy;
            public float Angle;

            /// <summary>Port of lilCalcDecalUV without the hand-dependent options.</summary>
            public Vector2 TransformUV(float u, float v, out bool inside)
            {
                var x = u; var y = v;
                if (ShouldCopy) x = Mathf.Abs(x - 0.5f) + 0.5f;
                x = x * Scale.x + Offset.x;
                y = y * Scale.y + Offset.y;
                if (ShouldFlipCopy && u < 0.5f) x = 1f - x;
                if (Mathf.Abs(Angle) > 1e-5f && Mathf.Abs(Scale.x) > 1e-6f && Mathf.Abs(Scale.y) > 1e-6f)
                {
                    var rx = (x - Offset.x) / Scale.x - 0.5f;
                    var ry = (y - Offset.y) / Scale.y - 0.5f;
                    var si = Mathf.Sin(Angle); var co = Mathf.Cos(Angle);
                    var nx = rx * co - ry * si + 0.5f;
                    var ny = rx * si + ry * co + 0.5f;
                    x = nx * Scale.x + Offset.x;
                    y = ny * Scale.y + Offset.y;
                }
                inside = x >= 0f && x <= 1f && y >= 0f && y <= 1f;
                return new Vector2(x, y);
            }
        }

        public bool NeedsBake =>
            MainTexture == null || !IsWhite(Color) || ToneCorrection || Layers.Count > 0 || AlphaMaskMode != 0;

        private static bool IsWhite(Color c) => Mathf.Abs(c.r - 1f) < 1e-4f && Mathf.Abs(c.g - 1f) < 1e-4f && Mathf.Abs(c.b - 1f) < 1e-4f && Mathf.Abs(c.a - 1f) < 1e-4f;

        public string Key
        {
            get
            {
                var sb = new System.Text.StringBuilder("base|");
                sb.Append(TextureBaker.Key(MainTexture, MainTexture != null ? MainTexture.updateCount : 0u, Color, ToneCorrection, HSVG, AlphaMaskMode, AlphaMask, AlphaMaskScaleOffset, AlphaMaskScale.ToString("R"), AlphaMaskValue.ToString("R")));
                foreach (var l in Layers)
                    sb.Append(TextureBaker.Key(l.Texture, l.Texture != null ? l.Texture.updateCount : 0u, l.Scale, l.Offset, l.Color, l.BlendMask, l.BlendMode, l.AlphaMode, l.EnableLighting.ToString("R"), l.IsDecal, l.ShouldCopy, l.ShouldFlipCopy, l.Angle.ToString("R")));
                return sb.ToString();
            }
        }
    }
}
