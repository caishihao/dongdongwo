"""阿瓜素材 v6：体型 +15% + 头围测量（供帽子缩放/锚点）。

画布 440×400（2x）：角色高 386（+15%），脚底 y=394。
anchors 输出：hatAnchor（头顶内侧）+ hatHeadWidth（头顶 25% 区域宽度，供帽围比对）。
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

CANVAS = (440, 400)
CHAR_H2X = 386          # +15%（原 336）
FOOT_Y2X = 394
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
    while q:
        x, y = q.popleft()
        c = rgb[y, x]
        for nx, ny in ((x+1,y),(x-1,y),(x,y+1),(x,y-1)):
            if 0 <= nx < w and 0 <= ny < h and not bg[ny, nx] and candidate[ny, nx]:
                if np.abs(rgb[ny, nx] - c).sum() < 42:
                    bg[ny, nx] = True; q.append((nx, ny))
    alpha = np.where(bg, 0, 255).astype(np.uint8)
    x0, y0, x1, y1 = WATERMARK
    alpha[y0:y1, x0:x1] = 0
    out = img.convert("RGBA")
    out.putalpha(Image.fromarray(alpha, "L").filter(ImageFilter.GaussianBlur(1.0)))
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


def process(style: str) -> dict:
    idle = remove_bg(Image.open(FRAMES / IDLE_SRC[style]))
    tap = remove_bg(Image.open(FRAMES / TAP_SRC[style]))
    a = np.asarray(idle)[:, :, 3]
    m = a > 60
    ys, xs = np.where(m)
    bx0, by0, bx1, by1 = xs.min(), ys.min(), xs.max(), ys.max()
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

    # 头围测量（idle 画布顶部 22% 区域）
    arr = np.asarray(Image.open(out / "idle.png"))
    alpha = arr[:, :, 3] > 60
    ys2, xs2 = np.where(alpha)
    top = ys2.min()
    head_band = alpha[top:top + int((ys2.max() - top) * 0.22), :]
    hys, hxs = np.where(head_band)
    head_w = int(hxs.max() - hxs.min()) if len(hxs) else 0
    head_center = int((hxs.min() + hxs.max()) / 2) if len(hxs) else 220
    return {"style": style, "scale": round(s, 4), "head_top2x": int(top),
            "head_w2x": head_w, "head_cx2x": head_center}


def main():
    rep = [process(s) for s in STYLES]
    (OUT / "v6-report.json").write_text(json.dumps(rep, ensure_ascii=False, indent=2), encoding="utf-8")
    # 汇总（各风格取中位数，写入 anchors 供帽子缩放）
    tops = sorted(r["head_top2x"] for r in rep)
    widths = sorted(r["head_w2x"] for r in rep)
    cx = sorted(r["head_cx2x"] for r in rep)
    summary = {"head_top_logical": round(tops[len(tops)//2] / 2, 1),
               "head_width_logical": round(widths[len(widths)//2] / 2, 1),
               "head_center_logical": round(cx[len(cx)//2] / 2, 1)}
    print(json.dumps(summary, ensure_ascii=False))
    for r in rep:
        print(r)


if __name__ == "__main__":
    main()
