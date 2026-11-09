"""阿瓜素材 v3：真实手臂切取路线。

v2 教训：程序化色块爪与黏土身体质感断裂（棕色圆球+细黄棒），观感廉价。
v3：从 ref 原图直接切出真实黏土手臂+棕爪（ref 中无遮挡），以肩为轴旋转到鼓面；
    身体侧区修补；边缘去白晕（defringe）。
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

# 归一化参数（bbox 相对）。clay 手调，其余风格按同角色构图移植，生成后目检微调。
PARAMS = {
    "clay": {
        # 手臂切取多边形（含肩球+爪，bbox 归一化）：沿体侧的外轮廓向内
        "armL_poly": [(0.02, 0.42), (0.26, 0.50), (0.30, 0.58), (0.30, 0.70), (0.14, 0.74), (0.02, 0.62)],
        "armR_poly": [(0.98, 0.42), (0.74, 0.50), (0.70, 0.58), (0.70, 0.70), (0.86, 0.74), (0.98, 0.62)],
        # 身体上要移除的手臂区（比切取区略小，避免修补吃掉过多体侧）
        "bodyL_poly": [(0.00, 0.44), (0.24, 0.52), (0.26, 0.66), (0.10, 0.70), (0.00, 0.60)],
        "bodyR_poly": [(1.00, 0.44), (0.76, 0.52), (0.74, 0.66), (0.90, 0.70), (1.00, 0.60)],
        # 肩轴（bbox 归一化）与旋转角（度，屏幕坐标；左臂向身体内下旋）
        "shoulderL": (0.10, 0.50), "shoulderR": (0.90, 0.50),
        "armRotL": 38, "armRotR": -38,
        # 目标爪位（220×180 逻辑，鼓面）
        "clawL": (55.0, 127.0), "clawR": (165.0, 127.0),
    },
}
for s in STYLES[1:]:
    PARAMS[s] = dict(PARAMS["clay"])

CANVAS = (440, 360)
CHAR_H2X = 336
HAT_ANCHOR = (110.0, 9.4)
PAW_AXIS = {"L": (76.0, 104.0), "R": (144.0, 104.0)}


def remove_bg(img: Image.Image):
    arr = np.asarray(img.convert("RGB")).astype(np.int16)
    h, w, _ = arr.shape
    lum = arr.mean(axis=2)
    sat = arr.max(axis=2) - arr.min(axis=2)
    bg = (lum > 205) & (sat < 26)
    alpha = np.where(bg, 0, 255).astype(np.uint8)
    a_img = Image.fromarray(alpha, "L").filter(ImageFilter.GaussianBlur(1.2))
    out = img.convert("RGBA")
    out.putalpha(a_img)
    a_arr = np.asarray(a_img)
    solid = (a_arr > 128) & ~((sat < 30) & (lum <= 205))
    ys, xs = np.where(solid)
    return out, (int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max()))


def defringe(img: Image.Image, erode_px: int = 1) -> Image.Image:
    """去白晕：alpha 收 1px + 半透明边缘色向邻近实心色回填。"""
    arr = np.asarray(img).astype(np.uint8)
    a = arr[:, :, 3].astype(np.int16)
    a = np.clip(a - 60, 0, 255)  # 收紧
    a8 = Image.fromarray(a.astype(np.uint8), "L").filter(ImageFilter.MinFilter(3))
    arr[:, :, 3] = np.asarray(a8)
    out = Image.fromarray(arr)
    # 边缘色渗：对 0<c<255 的像素取邻近实心色
    arr = np.asarray(out).astype(np.float32)
    alpha = arr[:, :, 3]
    rgb = arr[:, :, :3]
    solid_mask = alpha > 240
    if solid_mask.any():
        for _ in range(2):
            blurred = np.asarray(Image.fromarray(rgb.astype(np.uint8)).filter(ImageFilter.GaussianBlur(1))).astype(np.float32)
            edge = (~solid_mask) & (alpha > 8)
            rgb[edge] = blurred[edge]
            arr[:, :, :3] = rgb
    return Image.fromarray(arr.astype(np.uint8))


def poly_mask(size, poly, bbox):
    x0, y0, x1, y1 = bbox
    w, h = x1 - x0, y1 - y0
    pts = [(px * w + x0, py * h + y0) for px, py in poly]
    m = Image.new("L", size, 0)
    ImageDraw.Draw(m).polygon(pts, fill=255)
    return np.asarray(m) > 0


def inpaint_vertical(arr: np.ndarray, mask: np.ndarray) -> None:
    alpha = arr[:, :, 3]
    h, w = mask.shape
    rng = np.random.default_rng(7)
    for x in range(w):
        col = mask[:, x] & (alpha[:, x] > 10)
        if not col.any():
            continue
        ys = np.where(col)[0]
        yt, yb = ys.min(), ys.max()
        ty, by = yt - 5, min(yb + 5, h - 1)
        top = arr[ty, x, :3] if ty >= 0 and alpha[ty, x] > 10 else None
        bot = arr[by, x, :3] if alpha[by, x] > 10 else None
        if top is None and bot is None:
            continue
        n = yb - yt + 1
        for i, y in enumerate(range(yt, yb + 1)):
            t = i / max(n - 1, 1)
            base = (top * (1 - t) + bot * t) if (top is not None and bot is not None) else (top if top is not None else bot)
            arr[y, x, :3] = np.clip(base.astype(np.float32) + rng.normal(0, 1.8, 3), 0, 255)
        alpha[yt:yb + 1, x] = 255


def cut_arm(rgba: Image.Image, bbox, poly) -> tuple[Image.Image, tuple[float, float]]:
    """按多边形切出手臂（保留真实像素）；返回 (手臂图, 肩轴在手臂图内的坐标)。"""
    mask = poly_mask(rgba.size, poly, bbox)
    arr = np.asarray(rgba).copy()
    alpha = arr[:, :, 3].copy()
    alpha[~mask] = 0
    arr[:, :, 3] = alpha
    arm = Image.fromarray(arr)
    ys, xs = np.where(mask & (alpha > 10))
    x0, y0, x1, y1 = xs.min(), ys.min(), xs.max(), ys.max()
    arm = arm.crop((x0, y0, x1 + 1, y1 + 1))
    return arm, (x0, y0)


def process_style(style: str) -> dict:
    ref = Image.open(REF_DIR / REF_FILES[style]).convert("RGB")
    rgba, bbox = remove_bg(ref)
    p = PARAMS[style]
    W, H = rgba.size
    x0, y0, x1, y1 = bbox
    bw, bh = x1 - x0, y1 - y0

    # ① 切手臂（切取发生在修补之前——从原图取真实像素）
    armL_img, armL_off = cut_arm(rgba, bbox, p["armL_poly"])
    armR_img, armR_off = cut_arm(rgba, bbox, p["armR_poly"])

    # ② 身体修补（移除手臂区）
    arr = np.asarray(rgba).copy()
    m = poly_mask(rgba.size, p["bodyL_poly"], bbox) | poly_mask(rgba.size, p["bodyR_poly"], bbox)
    inpaint_vertical(arr, m)
    body = Image.fromarray(arr).filter(ImageFilter.GaussianBlur(0.5))

    # ③ 统一画布缩放
    ys, xs = np.where(np.asarray(body)[:, :, 3] > 10)
    bb = (xs.min(), ys.min(), xs.max(), ys.max())
    s = CHAR_H2X / (bb[3] - bb[1])
    body_c = body.crop((bb[0], bb[1], bb[2] + 1, bb[3] + 1)).resize(
        (int((bb[2] - bb[0]) * s), CHAR_H2X), Image.LANCZOS)
    ox = (CANVAS[0] - body_c.width) // 2
    oy = 354 - CHAR_H2X
    canvas = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
    canvas.paste(body_c, (ox, oy), body_c)

    # ④ 手臂旋转放置：肩轴 → PAW_AXIS 目标位，爪落鼓面
    def place_arm(arm_img: Image.Image, off, poly_key: str, shoulder_nrm, rot_deg: float, side: str) -> Image.Image:
        sw = arm_img.copy()
        # 缩放到画布比例
        sw = sw.resize((max(1, int(sw.width * s)), max(1, int(sw.height * s))), Image.LANCZOS)
        sx = (shoulder_nrm[0] * bw + x0 - off[0]) * s
        sy = (shoulder_nrm[1] * bh + y0 - off[1]) * s
        rot = sw.rotate(rot_deg, resample=Image.BICUBIC, expand=True, center=(sx, sy))
        # 旋转后肩轴位置
        rad = math.radians(rot_deg)
        dx, dy = sx - rot.width / 2, sy - rot.height / 2
        nx = rot.width / 2 + dx * math.cos(rad) - dy * math.sin(rad)
        ny = rot.height / 2 + dx * math.sin(rad) + dy * math.cos(rad)
        target_x = PAW_AXIS[side][0] * 2
        target_y = PAW_AXIS[side][1] * 2
        layer = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
        layer.paste(rot, (int(target_x - nx), int(target_y - ny)), rot)
        return layer

    pawL = place_arm(armL_img, armL_off, "armL_poly", p["shoulderL"], p["armRotL"], "L")
    pawR = place_arm(armR_img, armR_off, "armR_poly", p["shoulderR"], p["armRotR"], "R")

    # ⑤ 去白晕
    canvas = defringe(canvas)
    pawL = defringe(pawL)
    pawR = defringe(pawR)

    out = OUT_DIR / style
    out.mkdir(parents=True, exist_ok=True)
    canvas.save(out / "body.png")
    pawL.save(out / "pawL.png")
    pawR.save(out / "pawR.png")
    return {"style": style, "char_w2x": body_c.width, "offset_x2x": ox,
            "armL_size": armL_img.size, "armR_size": armR_img.size, "scale": round(s, 3)}


def main():
    rep = [process_style(s) for s in STYLES]
    (OUT_DIR / "v3-report.json").write_text(json.dumps(rep, ensure_ascii=False, indent=2), encoding="utf-8")
    for r in rep:
        print(r)


if __name__ == "__main__":
    main()
