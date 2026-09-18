using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace TsiYuki.NonToon.Editor
{
    /// <summary>
    /// CPU copy of a texture, 8 bits per channel. Read through the GPU (Blit + ReadPixels) so the source
    /// asset and its importer are never touched and compressed or Crunch textures work. Color textures
    /// keep their sRGB encoding and are decoded to linear on access through a lookup table.
    /// </summary>
    internal sealed class Pixels
    {
        public readonly int Width;
        public readonly int Height;
        public readonly Color32[] Raw;
        public readonly bool SRGB;

        // Managed sRGB transfer functions and tables: baking runs on worker threads, where Unity's native
        // Mathf color conversions must not be relied on.
        private static readonly float[] ToLinear = BuildToLinear();
        private static readonly byte[] ToGamma = BuildToGamma();
        private const int GammaTableSize = 4096;

        private static float[] BuildToLinear()
        {
            var t = new float[256];
            for (var i = 0; i < 256; i++) t[i] = SrgbToLinear(i / 255f);
            return t;
        }

        private static byte[] BuildToGamma()
        {
            var t = new byte[GammaTableSize + 1];
            for (var i = 0; i <= GammaTableSize; i++) t[i] = Encode(LinearToSrgb(i / (float)GammaTableSize));
            return t;
        }

        public static float SrgbToLinear(float c) =>
            c <= 0.04045f ? c / 12.92f : (float)System.Math.Pow((c + 0.055f) / 1.055f, 2.4f);

        public static float LinearToSrgb(float c) =>
            c <= 0.0031308f ? c * 12.92f : 1.055f * (float)System.Math.Pow(c, 1f / 2.4f) - 0.055f;

        private static byte GammaByte(float linear)
        {
            if (!(linear > 0f)) return 0;
            if (linear >= 1f) return 255;
            return ToGamma[(int)(linear * GammaTableSize + 0.5f)];
        }

        public Pixels(int width, int height, bool srgb)
        {
            Width = Mathf.Max(1, width);
            Height = Mathf.Max(1, height);
            SRGB = srgb;
            Raw = new Color32[Width * Height];
        }

        public long ByteSize => (long)Raw.Length * 4;

        public static bool IsSRGB(Texture texture) =>
            texture != null && GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);

        public static Pixels Read(Texture texture, int maxSize)
        {
            if (texture == null) return null;
            var scale = Mathf.Min(1f, maxSize / (float)Mathf.Max(texture.width, texture.height));
            var width = Mathf.Max(1, Mathf.RoundToInt(texture.width * scale));
            var height = Mathf.Max(1, Mathf.RoundToInt(texture.height * scale));
            var srgb = IsSRGB(texture);
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                var readback = new Texture2D(width, height, TextureFormat.RGBA32, false, !srgb);
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readback.Apply(false, false);
                var result = new Pixels(width, height, srgb);
                readback.GetPixels32().CopyTo(result.Raw, 0);
                Object.DestroyImmediate(readback);
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>Linear color of pixel i.</summary>
        public Color Get(int i)
        {
            var c = Raw[i];
            if (!SRGB) return new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
            return new Color(ToLinear[c.r], ToLinear[c.g], ToLinear[c.b], c.a / 255f);
        }

        public void Set(int i, Color linear)
        {
            if (SRGB)
                Raw[i] = new Color32(GammaByte(linear.r), GammaByte(linear.g), GammaByte(linear.b), Encode(linear.a));
            else
                Raw[i] = new Color32(Encode(linear.r), Encode(linear.g), Encode(linear.b), Encode(linear.a));
        }

        private static byte Encode(float v) => v >= 1f ? (byte)255 : v > 0f ? (byte)(v * 255f + 0.5f) : (byte)0;

        /// <summary>Bilinear sample with repeat wrapping, linear result; uv already includes scale/offset.</summary>
        public Color SampleRepeat(float u, float v)
        {
            u -= Mathf.Floor(u);
            v -= Mathf.Floor(v);
            var fx = u * Width - 0.5f;
            var fy = v * Height - 0.5f;
            var x0 = Mathf.FloorToInt(fx);
            var y0 = Mathf.FloorToInt(fy);
            var tx = fx - x0;
            var ty = fy - y0;
            var x1 = Wrap(x0 + 1, Width);
            var y1 = Wrap(y0 + 1, Height);
            x0 = Wrap(x0, Width);
            y0 = Wrap(y0, Height);
            var a = Color.LerpUnclamped(Get(y0 * Width + x0), Get(y0 * Width + x1), tx);
            var b = Color.LerpUnclamped(Get(y1 * Width + x0), Get(y1 * Width + x1), tx);
            return Color.LerpUnclamped(a, b, ty);
        }

        private static int Wrap(int i, int n) => ((i % n) + n) % n;

        /// <summary>Bilinear sample with clamped edges.</summary>
        public Color SampleClamp(float u, float v) =>
            SampleRepeat(Mathf.Clamp(u, 0.5f / Width, 1f - 0.5f / Width), Mathf.Clamp(v, 0.5f / Height, 1f - 0.5f / Height));

        public Color Average()
        {
            var sum = Color.clear;
            var step = Mathf.Max(1, Raw.Length / 65536);
            var n = 0;
            for (var i = 0; i < Raw.Length; i += step) { sum += Get(i); n++; }
            return n == 0 ? Color.white : sum / n;
        }

        public bool HasTransparency()
        {
            foreach (var c in Raw) if (c.a < 255) return true;
            return false;
        }

        /// <summary>
        /// Creates a texture from the pixels, compressed to DXT1/DXT5 (BC7 when <paramref name="highQuality"/>)
        /// with mipmaps so a build does not ship uncompressed RGBA.
        /// </summary>
        public Texture2D ToTexture(string name, bool compress, bool highQuality = false,
            TextureWrapMode wrap = TextureWrapMode.Repeat, FilterMode filter = FilterMode.Bilinear, int anisoLevel = 1)
        {
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, true, !SRGB)
            {
                name = name,
                wrapMode = wrap,
                filterMode = filter,
                anisoLevel = anisoLevel,
            };
            texture.SetPixels32(Raw);
            texture.Apply(true, false);
            if (compress && Width % 4 == 0 && Height % 4 == 0)
            {
                var format = highQuality ? TextureFormat.BC7 : HasTransparency() ? TextureFormat.DXT5 : TextureFormat.DXT1;
                EditorUtility.CompressTexture(texture, format, TextureCompressionQuality.Normal);
            }
            EnableMipStreaming(texture);
            return texture;
        }

        /// <summary>VRChat requires mip streaming on every texture; generated textures have it off by default.</summary>
        public static void EnableMipStreaming(Texture2D texture)
        {
            var so = new SerializedObject(texture);
            var prop = so.FindProperty("m_StreamingMipmaps");
            if (prop == null || prop.boolValue) return;
            prop.boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
