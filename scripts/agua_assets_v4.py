"""阿瓜素材 v4：全身敲鼓姿势 + 居中鼓布局。

架构（Design E）：
- body = 敲鼓生成图全身（手臂/爪已搭在鼓上的完美姿势），把鼓区从身体层修掉（爪保留）
- 鼓 = 程序化绘制（居中布局，阿瓜专属），画在身体前方，正好接住爪底
- 爪 = 烘焙在 body 里（无独立爪层）→ 零拼合、零协调问题
- 动画 = 身体挤压 + 鼓下沉（BongoSink 通道）+ 帽子弹动

坐标映射：图像 1024 → 画布 440×360(2x)，角色高 336，脚底 y=354。
"""
from __future__ import annotations

import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(r"D:\BongoCat")
REF_DIR = ROOT / "analysis/style-previews/ai-agua/ref"
GEN_DIR = ROOT / "analysis/style-previews/ai-agua"
OUT_DIR = ROOT / "src/BongoCat.App/assets/species/agua"

STYLES = ["clay", "sticker", "flat", "sketch", "chibi", "papercut"]
GEN_FILES = {s: GEN_DIR / f"agua-{s}.png" for s in STYLES}

# 鼓区（1024 坐标）：鼓组外框 + 爪多边形（爪像素要保留，不参与修补）
DRUM_RECT = (246, 744, 594, 937)
CLAW_L = [(248, 682), (352, 686), (354, 802), (248, 802)]
CLAW_R = [(578, 682), (692, 686), (692, 802), (578, 802)]

CHAR_TOP = 40      # 头顶
CHAR_BOTTOM = 935  # 脚底
CHAR_CX = 455      # 角色中心 x

CANVAS = (440, 360)
CHAR_H2X = 336
FOOT_Y2X = 354

# 阿瓜专属鼓布局（220×180 逻辑坐标）——鼓顶边=爪底下沿（爪搭鼓上，不遮挡）
BONGOS = {
    "left": {"x": 68.0, "y": 152.0, "w": 44.0, "h": 25.0},
    "right": {"x": 112.0, "y": 152.0, "w": 44.0, "h": 25.0},
}


def remove_bg(img: Image.Image) -> Image.Image:
    """全边界泛洪抠图：以边界均色为参考，欧氏距离 < 阈值即背景（适合偏暗米色/渐变底）。"""
    from collections import deque

    rgb = np.asarray(img.convert("RGB")).astype(np.float32)
    h, w, _ = rgb.shape
    border = np.concatenate([rgb[0, :, :], rgb[-1, :, :], rgb[:, 0, :], rgb[:, -1, :]])
    ref = np.median(border, axis=0)
    dist = np.sqrt(((rgb - ref) ** 2).sum(axis=2))
    candidate = dist < 38
    bg = np.zeros((h, w), dtype=bool)
    q = deque()
    for x in range(w):
        for y in (0, h - 1):
            if candidate[y, x] and not bg[y, x]:
                bg[y, x] = True
                q.append((x, y))
    for y in range(h):
        for x in (0, w - 1):
            if candidate[y, x] and not bg[y, x]:
                bg[y, x] = True
                q.append((x, y))
    tol = 16
    while q:
        x, y = q.popleft()
        c = rgb[y, x]
        for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
            if 0 <= nx < w and 0 <= ny < h and not bg[ny, nx] and candidate[ny, nx]:
                if np.abs(rgb[ny, nx] - c).sum() < tol * 3:
                    bg[ny, nx] = True
                    q.append((nx, ny))
    alpha = np.where(bg, 0, 255).astype(np.uint8)
    a_img = Image.fromarray(alpha, "L").filter(ImageFilter.GaussianBlur(1.2))
    out = img.convert("RGBA")
    out.putalpha(a_img)
    return out


def inpaint_drums(arr: np.ndarray) -> None:
    """鼓区（矩形减去爪多边形）逐列修补：取上方身体色，向下渐变加深（体积感）。"""
    h, w, _ = arr.shape
    mask = np.zeros((h, w), dtype=bool)
    x0, y0, x1, y1 = DRUM_RECT
    mask[y0:y1, x0:x1] = True
    for poly in (CLAW_L, CLAW_R):
        keep = Image.new("L", (w, h), 0)
        ImageDraw.Draw(keep).polygon(poly, fill=255)
        mask &= ~(np.asarray(keep) > 0)
    alpha = arr[:, :, 3]
    rng = np.random.default_rng(11)
    for x in range(x0, x1):
        col = mask[y0:y1, x]
        if not col.any():
            continue
        ys = np.where(col)[0] + y0
        yt, yb = ys.min(), ys.max()
        src_y = max(0, yt - 6)
        base = arr[src_y, x, :3].astype(np.float32)
        n = yb - yt + 1
        for i, y in enumerate(range(yt, yb + 1)):
            t = i / max(n - 1, 1)
            shade = 1.0 - 0.18 * t  # 向下渐深（阴影体积）
            arr[y, x, :3] = np.clip(base * shade + rng.normal(0, 1.6, 3), 0, 255)
        alpha[yt:yb + 1, x] = 255


def defringe(img: Image.Image) -> Image.Image:
    arr = np.asarray(img).astype(np.uint8)
    a = arr[:, :, 3].astype(np.int16)
    a = np.clip(a - 55, 0, 255)
    a8 = Image.fromarray(a.astype(np.uint8), "L").filter(ImageFilter.MinFilter(3))
    arr[:, :, 3] = np.asarray(a8)
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
    src = Image.open(GEN_FILES[style]).convert("RGB")
    rgba = remove_bg(src)
    arr = np.asarray(rgba).copy()
    inpaint_drums(arr)
    body = Image.fromarray(arr)

    # 统一画布
    s = CHAR_H2X / (CHAR_BOTTOM - CHAR_TOP)
    canvas = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
    left2x = int(CHAR_CX * s) - (220)          # 角色中心对画布中心（2x:220）
    body_scaled = body.resize((int(body.width * s), int(body.height * s)), Image.LANCZOS)
    ox = 220 - int(CHAR_CX * s)
    oy = FOOT_Y2X - int(CHAR_BOTTOM * s)
    canvas.paste(body_scaled, (ox, oy), body_scaled)
    canvas = defringe(canvas)

    out = OUT_DIR / style
    out.mkdir(parents=True, exist_ok=True)
    canvas.save(out / "body.png")
    # 空爪层（v4：爪已烘焙在 body，不单独渲染）
    empty = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
    empty.save(out / "pawL.png")
    empty.save(out / "pawR.png")

    return {"style": style, "scale": round(s, 4), "ox": ox, "oy": oy,
            "canvas_w": body_scaled.width}


def preview(style: str, path: Path) -> None:
    """合成预览：body + 阿瓜鼓布局（近似程序化鼓）便于目检爪鼓接触。"""
    body = Image.open(OUT_DIR / style / "body.png")
    comp = Image.new("RGBA", CANVAS, (245, 242, 235, 255))
    comp.alpha_composite(body)
    d = ImageDraw.Draw(comp)
    for key in ("left", "right"):
        b = BONGOS[key]
        rect = [b["x"] * 2, b["y"] * 2, (b["x"] + b["w"]) * 2 - 1, (b["y"] + b["h"]) * 2 - 1]
        d.rounded_rectangle(rect, radius=14, fill=(150, 88, 52, 255))
        d.rounded_rectangle([rect[0], rect[1], rect[2], rect[1] + 18], radius=10, fill=(120, 66, 38, 255))
    comp.convert("RGB").resize((880, 720), Image.LANCZOS).save(path)


def main():
    rep = [process(s) for s in STYLES]
    preview("clay", ROOT / "analysis/agua-frames/clay-v4-preview.png")
    (OUT_DIR / "v4-report.json").write_text(json.dumps({"styles": rep, "bongos": BONGOS}, ensure_ascii=False, indent=2), encoding="utf-8")
    for r in rep:
        print(r)


if __name__ == "__main__":
    main()
