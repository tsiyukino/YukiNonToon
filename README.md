# Yuki NonToon Converter

Non-destructive lilToon → [NonToon](https://github.com/lilxyzw/NonToon) conversion for VRChat avatars, applied at build time through [NDMF](https://github.com/bdunderscore/ndmf).

[English](#english) · [中文](#中文) · [日本語](#日本語)

## English

### Install

1. Add these VPM listings to VCC / ALCOM:
   - TsiYuki: `https://tsiyukino.github.io/vpm-repos/index.json`
   - lilxyzw (NonToon, lilToon): `https://lilxyzw.github.io/vpm-repos/vpm.json`
2. Add **Yuki NonToon Converter** to your project.

### Use

1. Add `TsiYuki/Yuki NonToon Converter` to the avatar root, or to any object under it.
2. Check the result in the Scene view (NDMF preview), and the per-material report in the inspector.
3. Upload. Your lilToon materials stay as they are; only the uploaded copy uses NonToon.

Prefer real assets? Use **Export** in the inspector to write the converted materials to `Assets/NonToonConverted/<avatar>` and assign them.

### What is converted

Base color (layers, tone correction, alpha mask), rendering mode, normal maps, shadow ramp (incl. SDF face shadows), rim shade, rim light, MatCaps, specular, backlight, minimum brightness, outline (width mask → vertex colors), distance fade, stencil, fur. Anything NonToon cannot express is listed as *approximate* or *lost* in the report. Environment reflections, audio link, dissolve, parallax and refraction/gem are not supported by NonToon.

## 中文

把 VRChat 模型上的 lilToon 材质在构建时（NDMF）转换为 NonToon，不改动原材质、贴图和网格。

1. 在 VCC / ALCOM 中添加上面两个 VPM 源，然后添加 **Yuki NonToon Converter**。
2. 把 `TsiYuki/Yuki NonToon Converter` 组件放在模型根物体或其任意子物体上。
3. 在 Scene 视图（NDMF 预览）中确认效果，在 Inspector 中查看每个材质的转换报告。
4. 直接上传即可。需要实际文件时，可在 Inspector 中使用“导出”。

## 日本語

VRChat アバターの lilToon マテリアルを、ビルド時（NDMF）に NonToon へ非破壊で変換します。元のマテリアル・テクスチャ・メッシュは変更しません。

1. 上記の VPM リポジトリを VCC / ALCOM に追加し、**Yuki NonToon Converter** をプロジェクトに追加します。
2. `TsiYuki/Yuki NonToon Converter` をアバターのルートまたはその子に追加します。
3. Scene ビュー（NDMF プレビュー）で見た目を確認し、Inspector でマテリアルごとのレポートを確認します。
4. そのままアップロードできます。実ファイルが必要な場合は Inspector の「エクスポート」を使います。

## License

MIT
