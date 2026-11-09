# -*- coding: utf-8 -*-
"""阿瓜（agua）sprite 资产加工管线。

输入：analysis/style-previews/ai-agua/agua-{style}.png（1024×1024，浅米底 + 右下角水印）
输出：src/BongoCat.App/assets/species/agua/{style}/{body,pawL,pawR}.png（440×360，2x 精度）
      src/BongoCat.App/assets/species/agua/anchors.json（爪轴/帽锚/鼓框，220×180 逻辑坐标）

管线（每风格）：
  1) flood-fill 抠图：从四边种子扩散，判据 R-B 暖色差（背景浅米/近白，角色黄色饱和），
     边缘高斯羽化；右下角"AI生成"水印位于背景连通区，随抠图消失。
  2) 拆层：pawL/pawR 区域矩形裁切（边缘羽化）→ body 擦除该区并用框上缘颜色逐列垂直延伸
     修补（alpha 保留原样：鼓/爪后的背景间隙保持透明）；鼓区整体擦除 + 修补
     （双鼓不拆——程序化邦戈鼓层按 anchors.json 的 bongoFrame 对齐原图鼓位）。
  3) 统一画布：角色（擦鼓/爪后的 body）缩放对齐 440×360（=220×180 逻辑 × 2），
     底缘对齐 y≈354、水平居中；爪图与鼓框用同一仿射映射（爪贴合原位）。

用法：python scripts/agua_assets.py [--check]（--check 输出区域标注诊断图，不入库）
"""
from __future__ import annotations

import argparse
import json
import os
from collections import deque

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

SRC = "analysis/style-previews/ai-agua"
DST = "src/BongoCat.App/assets/species/agua"
STYLES = ["clay", "sticker", "flat", "sketch", "chibi", "papercut"]

# 目标画布（2x 精度）：220×180 逻辑 × 2
CANVAS_W, CANVAS_H = 440, 360
# 角色底缘落位（2x 画布 y；显示 180 高时脚底约 y177）
FOOT_Y = 354
# 角色目标高度（2x；不含贴纸白边溢出，预留帽子/回弹余量）
BODY_TARGET_H = 336

# ── 每风格参数（原图 1024×1024 坐标；实测迭代调整）──────────────────────
# bg_rb：flood-fill 背景判据阈值（R-B > thr 视为背景可扩散；实测背景 rb≤43、角色≥73）
# bg_lum：白纸边判据（亮度 > lum 亦为背景可扩散；仅 sticker/papercut 需要）
# wm_roi：水印清除区（直接置透明；角色 bbox 右缘 ≤830 不进入该区）
# paws：(x0,y0,x1,y1) 左/右爪裁切矩形（含小臂；边缘羽化）
# drum：(x0,y0,x1,y1) 鼓 bbox（擦除区略放大；同时映射为 anchors 的 bongoFrame）
PARAMS = {
    "clay": {
        "bg_rb": 30,
        "paws": {"L": (285, 615, 465, 800), "R": (560, 615, 740, 800)},
        "drum": (330, 750, 700, 1004),
    },
    "sticker": {
        "bg_rb": 28,
        "bg_lum": 235,
        "paws": {"L": (340, 630, 485, 782), "R": (542, 630, 685, 782)},
        "drum": (378, 733, 662, 977),
    },
    "flat": {
        "bg_rb": 28,
        "paws": {"L": (320, 595, 500, 795), "R": (530, 595, 715, 795)},
        "drum": (405, 773, 633, 987),
    },
    "sketch": {
        "bg_rb": 3,
        "paws": {"L": (360, 628, 505, 790), "R": (525, 628, 668, 790)},
        "drum": (398, 737, 642, 987),
    },
    "chibi": {
        "bg_rb": 28,
        "paws": {"L": (358, 632, 502, 788), "R": (526, 632, 670, 788)},
        "drum": (382, 727, 652, 977),
    },
    "papercut": {
        "bg_rb": 22,
        "bg_lum": 238,
        "paws": {"L": (345, 608, 490, 790), "R": (535, 608, 685, 790)},
        "drum": (388, 722, 658, 982),
    },
}

# 右下角"AI生成 WORKBUDDY"水印区（六风格通用；角色均未延伸到该区）
WM_ROI = (840, 920, 1024, 1024)


def flood_background(rgb: np.ndarray, thr: int, lum_thr: int = 9999) -> np.ndarray:
    """从四边 flood fill 标记背景：R-B > thr（暖色低饱和底）或亮度 > lum_thr（白纸边）。
    返回 bool mask（True=背景）。角色黄色 rb≥73、肚皮 rb≥73 与背景（rb≤43）分离靠 thr。"""
    h, w, _ = rgb.shape
    r = rgb[:, :, 0].astype(np.int32)
    b = rgb[:, :, 2].astype(np.int32)
    g = rgb[:, :, 1].astype(np.int32)
    lum = (r * 299 + g * 587 + b * 114) // 1000
    rb = r - b
    # 滞回式判据：背景浅米 rb∈[6,43] lum≥190；角色黄 rb≥73 强排除（rb<100 上限）；
    # 暗色前景（腿/鼓/爪尖棕）lum<150 排除；白纸边用 lum_thr 补充。连通性防渗入内部浅色。
    cand = ((rb > thr) & (rb < 100) & (lum > 150)) | (lum > lum_thr)
    bg = np.zeros((h, w), dtype=bool)
    dq = deque()
    for x in range(w):
        for y in (0, h - 1):
            if cand[y, x] and not bg[y, x]:
                bg[y, x] = True
                dq.append((y, x))
    for y in range(h):
        for x in (0, w - 1):
            if cand[y, x] and not bg[y, x]:
                bg[y, x] = True
                dq.append((y, x))
    while dq:
        y, x = dq.popleft()
        for ny, nx in ((y - 1, x), (y + 1, x), (y, x - 1), (y, x + 1)):
            if 0 <= ny < h and 0 <= nx < w and cand[ny, nx] and not bg[ny, nx]:
                bg[ny, nx] = True
                dq.append((ny, nx))
    return bg


def drop_small_components(alpha: np.ndarray, min_px: int = 400) -> np.ndarray:
    """前景清理：只保留最大连通域（角色主体必然最大），其余孤立块（纸纹噪点、
    贴纸纸边残留竖条等）全部清零。"""
    h, w = alpha.shape
    mask = alpha > 8
    visited = np.zeros((h, w), dtype=bool)
    best_comp = []
    for y0 in range(h):
        for x0 in range(w):
            if mask[y0, x0] and not visited[y0, x0]:
                comp = []
                dq = deque([(y0, x0)])
                visited[y0, x0] = True
                while dq:
                    y, x = dq.popleft()
                    comp.append((y, x))
                    for ny, nx in ((y - 1, x), (y + 1, x), (y, x - 1), (y, x + 1)):
                        if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not visited[ny, nx]:
                            visited[ny, nx] = True
                            dq.append((ny, nx))
                if len(comp) > len(best_comp):
                    best_comp = comp
    out = np.zeros_like(alpha)
    if best_comp:
        ys = [c[0] for c in best_comp]
        xs = [c[1] for c in best_comp]
        out[ys, xs] = alpha[ys, xs]
    return out


def alpha_from(rgb: np.ndarray, thr: int, lum_thr: int = 9999) -> np.ndarray:
    """抠图：背景 → 0，前景 → 255，边缘羽化；水印 ROI 置透明；清除小残块。"""
    bg = flood_background(rgb, thr, lum_thr)
    alpha = np.where(bg, 0, 255).astype(np.uint8)
    alpha = drop_small_components(alpha)
    wx0, wy0, wx1, wy1 = WM_ROI
    alpha[wy0:wy1, wx0:wx1] = 0
    a_img = Image.fromarray(alpha, "L").filter(ImageFilter.GaussianBlur(1.5))
    a2 = np.array(a_img)
    return np.maximum(alpha, a2)  # 羽化只作用于边缘，内部保持不透明


def feather_box_mask(shape: tuple, feather: float = 6.0) -> np.ndarray:
    """矩形边缘羽化带（内部 255，边界线性渐隐），用于爪图裁切（贴鼓处残片渐隐）。"""
    h, w = shape
    band = np.full((h, w), 255, dtype=np.uint8)
    b = max(int(feather), 1)
    ramp = np.linspace(0, 255, b).astype(np.uint8)
    for i in range(b):
        v = ramp[i]
        band[i, :] = np.minimum(band[i, :], v)
        band[h - 1 - i, :] = np.minimum(band[h - 1 - i, :], v)
        band[:, i] = np.minimum(band[:, i], v)
        band[:, w - 1 - i] = np.minimum(band[:, w - 1 - i], v)
    return band


def repair_hybrid(rgb: np.ndarray, alpha: np.ndarray, box: tuple, margin: int = 6):
    """擦除 box 并修补：行内取左右邻域中位数水平插值（腿色从两侧合拢），
    与框上缘颜色的垂直延伸按行位置加权混合（顶部延续腹部，向下渐弱）；
    轻噪声模拟黏土/纸面纹理。alpha 保持原样（鼓区后的背景间隙保持透明）。"""
    x0, y0, x1, y1 = box
    h, w, _ = rgb.shape
    x0c, x1c = max(0, x0), min(w, x1)
    if x1c <= x0c or y0 >= min(y1, h):
        return
    top = np.median(rgb[max(0, y0 - margin):y0, x0c:x1c], axis=0).astype(np.float32) \
        if y0 > 0 else np.full((x1c - x0c, 3), 235.0)
    span = x1c - x0c
    for y in range(y0, min(y1, h)):
        left = np.median(rgb[y, max(0, x0c - 4):x0c], axis=0) if x0c >= 4 else top[0]
        right = np.median(rgb[y, x1c:min(w, x1c + 4)], axis=0) if x1c + 4 <= w else top[-1]
        t = np.linspace(0.0, 1.0, span, dtype=np.float32)[:, None]
        horiz = np.asarray(left, np.float32)[None, :] * (1 - t) + np.asarray(right, np.float32)[None, :] * t
        vt = max(0.0, 1.0 - (y - y0) / max(1.0, (y1 - y0) * 0.6))  # 垂直权重随行衰减
        col = horiz * (1 - vt) + top * vt
        n = np.random.default_rng(y * 131 + x0).normal(0, 2.5, (span, 3))
        rgb[y, x0c:x1c] = np.clip(col + n, 0, 255).astype(np.uint8)


def fit_transform(bbox: tuple) -> tuple:
    """原 1024 图 → 440×360 画布的仿射参数（等比缩放 + 居中 + 底缘对齐）。
    返回 (scale, offset_x, offset_y)。"""
    x0, y0, x1, y1 = bbox
    bw, bh = x1 - x0, y1 - y0
    s = BODY_TARGET_H / bh
    ox = (CANVAS_W - bw * s) / 2 - x0 * s
    oy = FOOT_Y - y1 * s
    return s, ox, oy


def to_canvas(rgb: np.ndarray, alpha: np.ndarray, tr: tuple) -> Image:
    """按 (s, ox, oy) 仿射贴到 440×360 RGBA 画布。"""
    s, ox, oy = tr
    img = Image.fromarray(np.dstack([rgb, alpha]), "RGBA")
    out = Image.new("RGBA", (CANVAS_W, CANVAS_H), (0, 0, 0, 0))
    rw, rh = int(img.width * s), int(img.height * s)
    resized = img.resize((rw, rh), Image.LANCZOS)
    out.paste(resized, (int(ox), int(oy)), resized)
    return out


def map_point(tr: tuple, x: float, y: float) -> tuple:
    s, ox, oy = tr
    return (x * s + ox, y * s + oy)


def process(style: str, check: bool):
    p = PARAMS[style]
    rgb = np.array(Image.open(os.path.join(SRC, f"agua-{style}.png")).convert("RGB")).copy()
    alpha = alpha_from(rgb, p["bg_rb"], p.get("bg_lum", 9999))

    work_rgb = rgb.copy()
    work_alpha = alpha.copy()

    # ① 鼓区擦除 + 修补（先鼓后爪：爪框上缘颜色取自身体，不受鼓修补影响）
    drum_pad = (p["drum"][0] - 6, p["drum"][1] - 6, p["drum"][2] + 6, p["drum"][3] + 6)
    repair_hybrid(work_rgb, work_alpha, drum_pad)

    # ② 拆爪：爪图 = 原区裁切（原 alpha + 矩形边缘羽化）；body 擦除爪区 + 修补
    paw_canvases = {}
    for side in ("L", "R"):
        x0, y0, x1, y1 = p["paws"][side]
        prgb = rgb[y0:y1, x0:x1].copy()
        pa = np.minimum(alpha[y0:y1, x0:x1], feather_box_mask((y1 - y0, x1 - x0), 5.0))
        paw_canvases[side] = (prgb, pa)
        # body 擦爪（略收缩擦除区，保留爪图边缘羽化带遮住接缝）→ 用框上方颜色修补
        erase = (x0 + 4, y0 + 4, x1 - 4, y1 - 4)
        repair_hybrid(work_rgb, work_alpha, erase)

    # ③ body bbox（擦鼓/爪后的 alpha bbox）
    ys, xs = np.nonzero(work_alpha > 8)
    bbox = (xs.min(), ys.min(), xs.max() + 1, ys.max() + 1)
    tr = fit_transform(bbox)

    if check:
        vis = Image.fromarray(rgb)
        d = ImageDraw.Draw(vis)
        for side in ("L", "R"):
            d.rectangle(p["paws"][side], outline=(255, 0, 0), width=4)
        d.rectangle(drum_pad, outline=(0, 90, 255), width=4)
        d.rectangle(bbox, outline=(0, 200, 0), width=4)
        vis.save(f"{SRC}/check-{style}-regions.png")
        Image.fromarray(alpha, "L").save(f"{SRC}/check-{style}-alpha.png")

        # 输出合成预览（body+爪，不含鼓）与爪单层
        body_img = to_canvas(work_rgb, work_alpha, tr)
        comp = body_img.copy()
        for side in ("L", "R"):
            prgb, pa = paw_canvases[side]
            paw = Image.fromarray(np.dstack([prgb, pa]), "RGBA")
            pw, ph = int(paw.width * tr[0]), int(paw.height * tr[0])
            paw = paw.resize((pw, ph), Image.LANCZOS)
            px, py = map_point(tr, p["paws"][side][0], p["paws"][side][1])
            comp.paste(paw, (int(px), int(py)), paw)
        comp.save(f"{SRC}/check-{style}-composite.png")

        anchors = build_anchors(style, p, tr, bbox)
        print(f"[{style}] bbox={bbox} tr={tr[0]:.4f} anchors={json.dumps(anchors, ensure_ascii=False)}")
        return (anchors, tr, bbox) if False else None

    # ④ 正式产出
    out_dir = os.path.join(DST, style)
    os.makedirs(out_dir, exist_ok=True)
    to_canvas(work_rgb, work_alpha, tr).save(os.path.join(out_dir, "body.png"))
    for side, name in (("L", "pawL"), ("R", "pawR")):
        prgb, pa = paw_canvases[side]
        paw = Image.fromarray(np.dstack([prgb, pa]), "RGBA")
        pw, ph = int(paw.width * tr[0]), int(paw.height * tr[0])
        paw = paw.resize((pw, ph), Image.LANCZOS)
        canvas = Image.new("RGBA", (CANVAS_W, CANVAS_H), (0, 0, 0, 0))
        px, py = map_point(tr, p["paws"][side][0], p["paws"][side][1])
        canvas.paste(paw, (int(px), int(py)), paw)
        canvas.save(os.path.join(out_dir, f"{name}.png"))

    anchors = build_anchors(style, p, tr, bbox)
    return anchors


def build_anchors(style: str, p: dict, tr: tuple, bbox: tuple) -> dict:
    """anchors.json 单风格条目（220×180 逻辑坐标 = 2x 画布坐标 ÷ 2）。"""
    def logical(x: float, y: float) -> dict:
        mx, my = map_point(tr, x, y)
        return {"x": round(mx / 2, 1), "y": round(my / 2, 1)}

    # 爪旋转轴：爪框内靠上中心（臂根切口中心，旋转时爪尖下压自然）
    axis = {}
    for side in ("L", "R"):
        x0, y0, x1, y1 = p["paws"][side]
        cx, cy = (x0 + x1) / 2, y0 + (y1 - y0) * 0.30
        axis[f"pawAxis{side}"] = logical(cx, cy)

    # 帽锚：头顶最高点（bbox 顶部中心 x，bbox 顶 y + 2px 嵌入）
    hat = logical((bbox[0] + bbox[2]) / 2, bbox[1] + 2)

    return {
        "pawAxisL": axis["pawAxisL"],
        "pawAxisR": axis["pawAxisR"],
        "hatAnchor": hat,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="输出区域标注/合成诊断图")
    args = ap.parse_args()

    all_anchors = {"version": 1, "_comment": "阿瓜 sprite 锚点（220×180 逻辑坐标；爪轴/帽锚。鼓共用程序化层，无鼓参数）", "styles": {}}
    for st in STYLES:
        anchors = process(st, args.check)
        if anchors is not None:
            all_anchors["styles"][st] = anchors

    if not args.check:
        os.makedirs(DST, exist_ok=True)
        with open(os.path.join(DST, "anchors.json"), "w", encoding="utf-8") as f:
            json.dump(all_anchors, f, ensure_ascii=False, indent=2)
        print("anchors.json written")


if __name__ == "__main__":
    main()
