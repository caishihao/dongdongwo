"""阿瓜素材 v5：双帧整绘（idle/tap），鼓烘焙在画面内。

v4 教训：抠鼓重绘/程序化爪永远有拼合痕迹。v5 直接用 AI 生成的两帧整绘
（抬手/敲击，构图一致），渲染层做帧切换 + 挤压 —— 真实 2D 游戏动画做法。
对位规则：同一风格的 idle/tap 必须用同一缩放偏移（以 idle bbox 标定），防换帧抖动。
"""
from __future__ import annotations

import json
from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

ROOT = Path(r"D:\BongoCat")
FRAMES = ROOT / "analysis/agua-frames/frames"
OUT = ROOT / "src/BongoCat.App/assets/species/agua"

STYLES = ["clay", "sticker", "flat", "sketch", "chibi", "papercut"]

IDLE_SRC = {
    "clay": "保持参考图中黄色小恐龙角色的全部特征与黏土质感风格_姿势改为_2026-11-09T14-27-46.png",
    "sticker": "保持参考图中黄色小恐龙角色的全部特征与贴纸风格_白色粗描边__2026-11-09T14-27-56.png",
    "flat": "保持参考图中黄色小恐龙角色的全部特征与扁平矢量风格_柔和渐变_2026-11-09T14-28-05.png",
    "sketch": "保持参考图中黄色小恐龙角色的全部特征与手绘钢笔线稿风格_斜线_2026-11-09T14-28-14.png",
    "chibi": "保持参考图中黄色小恐龙角色的全部特征与Q版厚描边卡通风_深色_2026-11-09T14-28-24.png",
    "papercut": "保持参考图中黄色小恐龙角色的全部特征与分层剪纸拼贴风格_撕纸_2026-11-09T14-28-33.png",
}
TAP_SRC = {
    "clay": "保持这张图的所有构图_角色_鼓_光影_背景完全一致_只改一个_2026-11-09T14-29-02.png",
    "sticker": "保持这张图的所有构图_角色_鼓_描边风格_背景完全一致_只改_2026-11-09T14-29-13.png",
    "flat": "保持这张图的所有构图_角色_鼓_色彩_背景完全一致_只改一个_2026-11-09T14-29-24.png",
    "sketch": "保持这张图的所有构图_角色_鼓_线稿风格_纸感背景完全一致__2026-11-09T14-29-38.png",
    "chibi": "保持这张图的所有构图_角色_鼓_厚描边风格_背景完全一致_只_2026-11-09T14-29-49.png",
    "papercut": "保持这张图的所有构图_角色_鼓_剪纸分层风格_背景完全一致__2026-11-09T14-30-01.png",
}

CANVAS = (440, 360)
CHAR_H2X = 336
FOOT_Y2X = 354
WATERMARK = (800, 880, 1024, 1024)


def remove_bg(img: Image.Image) -> Image.Image:
    rgb = np.asarray(img.convert("RGB")).astype(np.float32)
    h, w, _ = rgb.shape
    border = np.concatenate([rgb[0, :, :], rgb[-1, :, :], rgb[:, 0, :], rgb[:, -1, :]])
    ref = np.median(border, axis=0)
    dist = np.sqrt(((rgb - ref) ** 2).sum(axis=2))
    candidate = dist < 30
    bg = np.zeros((h, w), dtype=bool)
    q = deque()
    for x in range(w):
        for y in (0, h - 1):
            if candidate[y, x] and not bg[y, x]:
                bg[y, x] = True; q.append((x, y))
    for y in range(h):
        for x in (0, w - 1):
            if candidate[y, x] and not bg[y, x]:
                bg[y, x] = True; q.append((x, y))
    tol = 14
    while q:
        x, y = q.popleft()
        c = rgb[y, x]
        for nx, ny in ((x+1,y),(x-1,y),(x,y+1),(x,y-1)):
            if 0 <= nx < w and 0 <= ny < h and not bg[ny, nx] and candidate[ny, nx]:
                if np.abs(rgb[ny, nx] - c).sum() < tol * 3:
                    bg[ny, nx] = True; q.append((nx, ny))
    alpha = np.where(bg, 0, 255).astype(np.uint8)
    # 水印区强制透明
    x0, y0, x1, y1 = WATERMARK
    alpha[y0:y1, x0:x1] = 0
    a_img = Image.fromarray(alpha, "L").filter(ImageFilter.GaussianBlur(1.0))
    out = img.convert("RGBA")
    out.putalpha(a_img)
    return out


def defringe(img: Image.Image) -> Image.Image:
    arr = np.asarray(img).astype(np.uint8)
    a = arr[:, :, 3].astype(np.int16)
    # 只收紧「边缘半透明」像素；实心区保持 255（全局减 alpha 会让整只角色半透明发灰）
    a = np.where(a >= 250, 255, np.clip(a - 55, 0, 255))
    a8 = Image.fromarray(a.astype(np.uint8), "L").filter(ImageFilter.MinFilter(3))
    a_arr = np.asarray(a8).astype(np.int16)
    a_arr = np.where(arr[:, :, 3] >= 250, 255, a_arr)
    arr[:, :, 3] = a_arr.astype(np.uint8)
    rgb = arr[:, :, :3].astype(np.float32)
    alpha = arr[:, :, 3]
    solid = alpha > 240
    for _ in range(2):
        blurred = np.asarray(Image.fromarray(rgb.astype(np.uint8)).filter(ImageFilter.GaussianBlur(1))).astype(np.float32)
        edge = (~solid) & (alpha > 8)
        rgb[edge] = blurred[edge]
    arr[:, :, :3] = rgb.astype(np.uint8)
    return Image.fromarray(arr)


def bbox_of(rgba: Image.Image, exclude_watermark: bool = True) -> tuple[int, int, int, int]:
    a = np.asarray(rgba)[:, :, 3]
    m = a > 60
    if exclude_watermark:
        x0, y0, x1, y1 = WATERMARK
        m[y0:y1, x0:x1] = False
    ys, xs = np.where(m)
    return int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())


def process(style: str) -> dict:
    idle = remove_bg(Image.open(FRAMES / IDLE_SRC[style]))
    tap = remove_bg(Image.open(FRAMES / TAP_SRC[style]))
    bx0, by0, bx1, by1 = bbox_of(idle)
    s = CHAR_H2X / (by1 - by0)
    ox = int(220 - (bx0 + bx1) / 2 * s)
    oy = int(FOOT_Y2X - by1 * s)

    out = OUT / style
    out.mkdir(parents=True, exist_ok=True)
    for name, img in (("idle", idle), ("tap", tap)):
        canvas = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
        scaled = img.resize((int(img.width * s), int(img.height * s)), Image.LANCZOS)
        canvas.paste(scaled, (ox, oy), scaled)
        canvas = defringe(canvas)
        canvas.save(out / f"{name}.png")
    # 清理 v1-v4 遗留部件
    for legacy in ("body.png", "pawL.png", "pawR.png"):
        p = out / legacy
        if p.exists():
            p.unlink()
    return {"style": style, "scale": round(s, 4), "ox": ox, "oy": oy}


def preview(style: str) -> None:
    comp = Image.new("RGBA", (CANVAS[0], CANVAS[1] * 2 + 8), (245, 242, 235, 255))
    for i, name in enumerate(("idle", "tap")):
        img = Image.open(OUT / style / f"{name}.png")
        comp.alpha_composite(img, (0, i * (CANVAS[1] + 8)))
    comp.convert("RGB").save(ROOT / f"analysis/agua-frames/v5-{style}.png")


def main():
    rep = [process(s) for s in STYLES]
    preview("clay")
    (OUT / "v5-report.json").write_text(json.dumps(rep, ensure_ascii=False, indent=2), encoding="utf-8")
    for r in rep:
        print(r)


if __name__ == "__main__":
    main()
