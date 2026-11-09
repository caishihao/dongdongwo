"""多物种素材管线（v6 规格通用化）：为每个 sprite 物种生成 idle/tap 双帧资产。

输入：analysis/style-previews/ai-<species>/drum-{style}.png（抬手帧）+ tap-{style}.png（敲击帧）
输出：src/BongoCat.App/assets/species/<species>/{style}/{idle,tap}.png（440×400）
"""
from __future__ import annotations

from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

ROOT = Path(r"D:\BongoCat")
PREVIEWS = ROOT / "analysis/style-previews"
OUT_ROOT = ROOT / "src/BongoCat.App/assets/species"

STYLES = ["clay", "sticker", "flat", "sketch", "chibi", "papercut"]
SPECIES = {"xiaohuang": "ai-xiaohuang", "xiaobai": "ai-xiaobai"}

CANVAS = (440, 400)
CHAR_H2X = 386
FOOT_Y2X = 394
WATERMARK = (760, 860, 1024, 1024)


BG_TOL = {"xiaohuang": (30, 42), "xiaobai": (13, 22)}


def remove_bg(img: Image.Image, species: str) -> Image.Image:
    cand_th, prop_tol = BG_TOL.get(species, (30, 42))
    rgb = np.asarray(img.convert("RGB")).astype(np.float32)
    h, w, _ = rgb.shape

    # 白色角色专用（白身与白底不可分，颜色法必失效）：
    #   ① 有线稿（深色占比≥2%）→ 黑描边封闭+填洞
    #   ② 软边无描边（clay/flat）→ 暖色判据（R-B>4 | 深色）封闭+填洞
    if species == "xiaobai":
        gray = rgb.mean(axis=2)
        dark = gray < 110
        warm = (rgb[:, :, 0] - rgb[:, :, 2]) > 4
        m = ((warm | dark).astype(np.uint8) * 255)
        alpha = _outline_silhouette(m, h, w, ksize=9)
        out = img.convert("RGBA")
        out.putalpha(Image.fromarray(alpha, "L"))
        return out
    border = np.concatenate([rgb[0, :, :], rgb[-1, :, :], rgb[:, 0, :], rgb[:, -1, :]])
    ref = np.median(border, axis=0)
    dist = np.sqrt(((rgb - ref) ** 2).sum(axis=2))
    # 边缘屏障：高梯度像素（黑描边/强边界）不可穿越——防白色角色身体被当背景抠掉
    gray = rgb.mean(axis=2)
    gy, gx = np.gradient(gray)
    grad = np.sqrt(gx ** 2 + gy ** 2)
    barrier = grad > 18
    candidate = (dist < cand_th) & ~barrier
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
                if np.abs(rgb[ny, nx] - c).sum() < prop_tol and not barrier[ny, nx]:
                    bg[ny, nx] = True; q.append((nx, ny))
    alpha = np.where(bg, 0, 255).astype(np.uint8)
    x0, y0, x1, y1 = WATERMARK
    alpha[y0:y1, x0:x1] = 0
    alpha = keep_largest_component(alpha, rgb.astype(np.float32))
    out = img.convert("RGBA")
    out.putalpha(Image.fromarray(alpha, "L").filter(ImageFilter.GaussianBlur(1.0)))
    return out


def _outline_silhouette(dark: np.ndarray, h: int, w: int, ksize: int = 25) -> np.ndarray:
    """描边封闭（MaxFilter）+ 填洞 → 实心剪影 alpha。"""
    img = Image.fromarray(dark.astype(np.uint8), "L")
    for _ in range(2):
        img = img.filter(ImageFilter.MaxFilter(ksize))
    for _ in range(2):
        img = img.filter(ImageFilter.MinFilter(ksize))
    closed = np.asarray(img) > 60
    free = ~closed
    visited = np.zeros((h, w), dtype=bool)
    q = deque()
    for x in range(w):
        for y in (0, h - 1):
            if free[y, x] and not visited[y, x]:
                visited[y, x] = True
                q.append((x, y))
    for y in range(h):
        for x in (0, w - 1):
            if free[y, x] and not visited[y, x]:
                visited[y, x] = True
                q.append((x, y))
    while q:
        x, y = q.popleft()
        for nx, ny in ((x+1,y),(x-1,y),(x,y+1),(x,y-1)):
            if 0 <= nx < w and 0 <= ny < h and free[ny, nx] and not visited[ny, nx]:
                visited[ny, nx] = True
                q.append((nx, ny))
    filled = closed | (free & ~visited)
    return (filled * 255).astype(np.uint8)


def solidify_silhouette(alpha: np.ndarray) -> np.ndarray:
    """形态学封闭（封轮廓开口）+ 填洞（身体内部），得到实心剪影。"""
    m = (alpha > 60).astype(np.uint8) * 255
    img = Image.fromarray(m, "L")
    closed = img
    for _ in range(2):
        closed = closed.filter(ImageFilter.MaxFilter(9))
    for _ in range(2):
        closed = closed.filter(ImageFilter.MinFilter(9))
    closed_arr = np.asarray(closed) > 60
    h, w = closed_arr.shape
    free = ~closed_arr
    visited = np.zeros((h, w), dtype=bool)
    q = deque()
    for x in range(w):
        for y in (0, h - 1):
            if free[y, x] and not visited[y, x]:
                visited[y, x] = True
                q.append((x, y))
    for y in range(h):
        for x in (0, w - 1):
            if free[y, x] and not visited[y, x]:
                visited[y, x] = True
                q.append((x, y))
    while q:
        x, y = q.popleft()
        for nx, ny in ((x+1,y),(x-1,y),(x,y+1),(x,y-1)):
            if 0 <= nx < w and 0 <= ny < h and free[ny, nx] and not visited[ny, nx]:
                visited[ny, nx] = True
                q.append((nx, ny))
    filled = closed_arr | (free & ~visited)
    out = np.where(filled, 255, 0).astype(np.uint8)
    # 保留内部原始像素（用封闭掩码裁剪回原图数据）
    return out


def keep_largest_component(alpha: np.ndarray, rgb: np.ndarray) -> np.ndarray:
    """连通域过滤：保留最大组件 + 彩色组件（饱和度≥22），剔除灰色地面阴影/残影。"""
    h, w = alpha.shape
    mask = alpha > 60
    labels = np.zeros((h, w), dtype=np.int32)
    cur = 0
    sizes: dict[int, int] = {}
    sats: dict[int, float] = {}
    for y, x in np.argwhere(mask):
        if labels[y, x]:
            continue
        cur += 1
        q = deque([(int(x), int(y))])
        labels[y, x] = cur
        size = 0
        sat_sum = 0.0
        while q:
            cx, cy = q.popleft()
            px = rgb[cy, cx]
            sat_sum += float(px.max() - px.min())
            size += 1
            for nx, ny in ((cx+1,cy),(cx-1,cy),(cx,cy+1),(cx,cy-1)):
                if 0 <= nx < w and 0 <= ny < h and mask[ny, nx] and not labels[ny, nx]:
                    labels[ny, nx] = cur
                    q.append((nx, ny))
        sizes[cur] = size
        sats[cur] = sat_sum / max(size, 1)
    if not sizes:
        return alpha
    best = max(sizes, key=lambda k: sizes[k])
    keep = np.zeros_like(mask)
    for k in sizes:
        if k == best or sats[k] >= 22:
            keep |= labels == k
    out = alpha.copy()
    out[~keep] = 0
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


def process_species(species: str, folder: str) -> list[dict]:
    base = PREVIEWS / folder
    reports = []
    for style in STYLES:
        idle_src = Image.open(base / f"drum-{style}.png")
        tap_src = Image.open(base / f"tap-{style}.png")
        idle = remove_bg(idle_src, species)
        tap = remove_bg(tap_src, species)

        a = np.asarray(idle)[:, :, 3]
        ys, xs = np.where(a > 60)
        bx0, by0, bx1, by1 = xs.min(), ys.min(), xs.max(), ys.max()
        s = CHAR_H2X / (by1 - by0)
        ox = int(220 - (bx0 + bx1) / 2 * s)
        oy = int(FOOT_Y2X - by1 * s)

        out = OUT_ROOT / species / style
        out.mkdir(parents=True, exist_ok=True)
        for name, img in (("idle", idle), ("tap", tap)):
            canvas = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
            scaled = img.resize((int(img.width * s), int(img.height * s)), Image.LANCZOS)
            canvas.paste(scaled, (ox, oy), scaled)
            defringe(canvas).save(out / f"{name}.png")
        reports.append({"species": species, "style": style, "scale": round(s, 4)})
    return reports


def main():
    for species, folder in SPECIES.items():
        for r in process_species(species, folder):
            print(r)


if __name__ == "__main__":
    main()
