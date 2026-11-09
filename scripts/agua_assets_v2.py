"""阿瓜素材管线 v2：从 ref 原图（无鼓）生成干净素材。

路线修正（v1 从敲鼓生成图抠爪失败）：
- body  ：ref 原图去背景后，移除双臂（肩以下侧区），躯干侧面逐行插值修补
- paw   ：程序化重绘"搭在鼓面的前臂+三指爪"（配色采样自原图），绘制在鼓面绝对位置
- 姿势语法与矢量物种一致：爪常驻鼓面，敲击时 8px 下压（CatTapAnimator 零改动）

每风格参数 = 角色bbox归一化坐标（clay 手调，其余风格按 bbox 映射后微调）。
"""
from __future__ import annotations

import json
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(r"D:\BongoCat")
REF_DIR = ROOT / "analysis/style-previews/ai-agua/ref"
OUT_DIR = ROOT / "src/BongoCat.App/assets/species/agua"

STYLES = ["clay", "sticker", "flat", "sketch", "chibi", "papercut"]

REF_FILES = {
    "clay": "agua-clay-ref.jpg",
    "sticker": "agua-sticker-ref.png",
    "flat": "agua-flat-ref.png",
    "sketch": "agua-sketch-ref.jpg",
    "chibi": "agua-chibi-ref.jpg",
    "papercut": "agua-papercut-ref.jpg",
}

# 每风格参数：臂区（bbox 归一化多边形）+ 色彩修正。
# 归一化坐标：x,y ∈ [0,1] 相对角色 bbox（x*W+bbox.x0）。clay 手调基准。
PARAMS: dict[str, dict] = {
    "clay": {
        "armL_poly": [(0.00, 0.46), (0.34, 0.54), (0.34, 0.76), (0.12, 0.78), (0.00, 0.66)],
        "armR_poly": [(1.00, 0.46), (0.66, 0.54), (0.66, 0.76), (0.88, 0.78), (1.00, 0.66)],
        "claw_color": None,  # 自动采样
    },
}
# 其余风格先用 clay 参数按 bbox 映射（同角色构图相近），生成后目检再调。
for s in STYLES[1:]:
    PARAMS[s] = dict(PARAMS["clay"])

# 程序化爪的几何（220×180 逻辑坐标 → 绘制时 ×2）
PAW_AXIS_L = (76.0, 104.0)    # 左肘（身体轮廓内侧）
PAW_AXIS_R = (144.0, 104.0)
CLAW_L = (53.0, 128.0)        # 左鼓面落点（鼓 x12-82，鼓面 y≈120-128）
CLAW_R = (167.0, 128.0)
ARM_W = 12.0                  # 臂宽（逻辑 px）
CLAW_R_LEN = 10.0             # 爪端三指团半径


def remove_bg(img: Image.Image) -> tuple[Image.Image, tuple[int, int, int, int]]:
    """近白背景 → 透明；返回 (RGBA 图, 角色 bbox)。"""
    arr = np.asarray(img.convert("RGB")).astype(np.int16)
    h, w, _ = arr.shape
    lum = arr.mean(axis=2)
    # 背景判定：高亮度 + 低饱和（RGB 三通道接近）
    sat = arr.max(axis=2) - arr.min(axis=2)
    bg = (lum > 205) & (sat < 26)
    alpha = np.where(bg, 0, 255).astype(np.uint8)
    # 收边：边缘 2px 渐变
    a_img = Image.fromarray(alpha, "L").filter(ImageFilter.GaussianBlur(1.2))
    out = img.convert("RGBA")
    out.putalpha(a_img)
    # bbox 排除灰色地面阴影（低饱和+中低亮度），避免 bbox 膨胀
    a_arr = np.asarray(a_img)
    solid = (a_arr > 128) & ~((sat < 30) & (lum <= 205))
    ys, xs = np.where(solid)
    x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
    return out, (int(x0), int(y0), int(x1), int(y1))


def inpaint_horizontal(arr: np.ndarray, alpha: np.ndarray, poly) -> None:
    """把多边形区域内的角色像素按「列方向垂直插值」回填（上缘肩色 → 下缘腿色，同色系）。"""
    h, w, _ = arr.shape
    ys, xs = np.where(alpha > 10)
    x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
    pw, ph = x1 - x0, y1 - y0
    pts = [(int(px * pw + x0), int(py * ph + y0)) for px, py in poly]
    poly_img = Image.new("L", (w, h), 0)
    ImageDraw.Draw(poly_img).polygon(pts, fill=255)
    mask = (np.asarray(poly_img) > 0) & (alpha > 10)
    rng = np.random.default_rng(7)
    for x in range(w):
        col = mask[:, x]
        if not col.any():
            continue
        ys_m = np.where(col)[0]
        yt, yb = ys_m.min(), ys_m.max()
        top_y = yt - 4
        bot_y = min(yb + 4, h - 1)
        top = arr[top_y, x, :3].astype(np.float32) if top_y >= 0 and alpha[top_y, x] > 10 else None
        bot = arr[bot_y, x, :3].astype(np.float32) if alpha[bot_y, x] > 10 else None
        if top is None and bot is None:
            continue
        n = yb - yt + 1
        for i, y in enumerate(range(yt, yb + 1)):
            t = i / max(n - 1, 1)
            if top is not None and bot is not None:
                base = top * (1 - t) + bot * t
            else:
                base = top if top is not None else bot
            noise = rng.normal(0, 2.0, 3)
            arr[y, x, :3] = np.clip(base + noise, 0, 255)
        alpha[yt:yb + 1, x] = np.where(alpha[yt:yb + 1, x] > 10, 255, alpha[yt:yb + 1, x])


def sample_color_bbox(rgba: Image.Image, bbox, fx: float, fy: float) -> tuple[int, int, int]:
    """角色 bbox 归一化位置取色（3×3 均值）。"""
    x0, y0, x1, y1 = bbox
    px, py = int(fx * (x1 - x0) + x0), int(fy * (y1 - y0) + y0)
    arr = np.asarray(rgba.convert("RGB"))
    patch = arr[max(0, py - 1):py + 2, max(0, px - 1):px + 2]
    m = patch.reshape(-1, 3).mean(axis=0)
    return tuple(int(v) for v in m)


def sample_color(arr: np.ndarray, alpha: np.ndarray, fx: float, fy: float) -> tuple[int, int, int]:
    """全图归一化位置取色（3×3 均值）。"""
    h, w, _ = arr.shape
    px, py = int(fx * w), int(fy * h)
    patch = arr[max(0, py - 1):py + 2, max(0, px - 1):px + 2]
    m = patch.reshape(-1, 3).mean(axis=0)
    return tuple(int(v) for v in m)


def draw_paw(canvas: Image.Image, axis_xy: tuple[float, float], claw_xy: tuple[float, float],
             arm_color, claw_color, scale: float = 2.0):
    """在 440×360 画布上画「前臂+三指爪」，轴点=肘，落点=鼓面。"""
    d = ImageDraw.Draw(canvas)
    ax, ay = axis_xy[0] * scale, axis_xy[1] * scale
    cx, cy = claw_xy[0] * scale, claw_xy[1] * scale
    lw = int(ARM_W * scale)
    # 前臂：粗圆头线段
    d.line([(ax, ay), (cx, cy)], fill=arm_color, width=lw)
    r = lw / 2
    d.ellipse([ax - r, ay - r, ax + r, ay + r], fill=arm_color)
    # 三指爪：掌团 + 三枚棕色爪尖
    pr = CLAW_R_LEN * scale
    d.ellipse([cx - pr, cy - pr * 0.85, cx + pr, cy + pr * 0.85], fill=claw_color)
    finger_r = pr * 0.42
    for ang in (-38, 0, 38):
        a = math.radians(ang)
        fx = cx + math.sin(a) * pr * 0.82
        fy = cy + math.cos(a) * pr * 0.82
        d.ellipse([fx - finger_r, fy - finger_r, fx + finger_r, fy + finger_r], fill=claw_color)
    # 掌团高光（上缘一条浅色弧）
    hl = tuple(min(255, int(c * 1.18)) for c in arm_color)
    d.arc([ax - r * 0.7, ay - r * 0.7, ax + r * 0.7, ay + r * 0.7], 200, 320, fill=hl, width=max(2, int(scale)))


def process_style(style: str) -> dict:
    ref = Image.open(REF_DIR / REF_FILES[style]).convert("RGB")
    rgba, bbox = remove_bg(ref)
    arr = np.asarray(rgba).copy()
    alpha = arr[:, :, 3]
    p = PARAMS[style]

    # 采色：臂=头顶下方躯干黄（bbox 相对，头部不受阴影影响）；爪=固定棕
    arm_color = sample_color_bbox(rgba, bbox, 0.50, 0.16)
    claw_color = p.get("claw_color") or (133, 78, 37)

    # 移除双臂
    inpaint_horizontal(arr, alpha, p["armL_poly"])
    inpaint_horizontal(arr, alpha, p["armR_poly"])

    body = Image.fromarray(arr.astype(np.uint8))
    # 修补区轻模糊弱化色带
    body = body.filter(ImageFilter.GaussianBlur(0.6))

    # 统一画布：440×360，角色高 336，脚底 y=354
    ys, xs = np.where(np.asarray(body)[:, :, 3] > 10)
    bx0, bx1, by0, by1 = xs.min(), xs.max(), ys.min(), ys.max()
    ch = by1 - by0
    target_h = 336
    s = target_h / ch
    cw = int((bx1 - bx0) * s)
    cropped = body.crop((bx0, by0, bx1 + 1, by1 + 1))
    scaled = cropped.resize((cw, target_h), Image.LANCZOS)
    canvas = Image.new("RGBA", (440, 360), (0, 0, 0, 0))
    ox = (440 - cw) // 2
    canvas.paste(scaled, (ox, 354 - target_h), scaled)

    # 爪（逻辑坐标 ×2）
    paw_l = Image.new("RGBA", (440, 360), (0, 0, 0, 0))
    draw_paw(paw_l, PAW_AXIS_L, CLAW_L, arm_color, claw_color)
    paw_r = Image.new("RGBA", (440, 360), (0, 0, 0, 0))
    draw_paw(paw_r, PAW_AXIS_R, CLAW_R, arm_color, claw_color)

    out = OUT_DIR / style
    out.mkdir(parents=True, exist_ok=True)
    canvas.save(out / "body.png")
    paw_l.save(out / "pawL.png")
    paw_r.save(out / "pawR.png")

    return {
        "style": style,
        "arm_color": "#%02x%02x%02x" % arm_color,
        "claw_color": "#%02x%02x%02x" % claw_color,
        "char_w2x": cw,
        "offset_x2x": ox,
    }


def main():
    report = [process_style(s) for s in STYLES]
    (OUT_DIR / "v2-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    for r in report:
        print(r)


if __name__ == "__main__":
    main()
