# Changelog

## [Unreleased]

### Added
- `Yuki NonToon Converter` component: put it on the avatar root or any object under it. lilToon materials on that avatar are converted to NonToon at build time (NDMF, after Modular Avatar and TexTransTool, before Avatar Optimizer). Source materials, textures and meshes are never modified.
- Scene preview through NDMF preview (toggle in the NDMF preview menu).
- Conversion covers base color (main 2nd/3rd layers, tone correction, alpha mask), rendering mode and queue, normal maps, shadow ramp (including SDF face shadows), rim shade, rim light (multiply rim → rim shade), MatCaps (add / multiply / normal blend, strength and blur), specular (F0 from reflectance, metallic and the reflection mask), backlight, lighten / minimum brightness, outline (color and width mask baked into vertex colors), distance fade, stencil and fur.
- Per-material report (exact / approximate / lost), per-material exclusion and property overrides.
- Export mode: write the converted materials and textures to `Assets/NonToonConverted/<avatar>` and assign them to the avatar (Undo supported, GUIDs kept on re-export).
- Checks for renderers with shadow receiving off and missing NonToon modules.
- English, Chinese and Japanese UI.

### Performance
- Textures are read at 8 bits per channel through the GPU, cached with a memory-bounded LRU and baked in parallel. A full avatar (26 materials, 4K textures) converts in about 4 seconds; the preview works at 1024 px.
- Generated textures have mip streaming enabled, as VRChat requires.
