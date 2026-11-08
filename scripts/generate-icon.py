#!/usr/bin/env python3
"""Рисует app.ico без внешних ассетов. Запуск: python3 scripts/generate-icon.py"""
from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 256
img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Скруглённый тёмно-синий фон.
d.rounded_rectangle((8, 8, 248, 248), radius=54, fill=(9, 23, 42, 255))
d.rounded_rectangle((8, 8, 248, 248), radius=54, outline=(30, 67, 112, 255), width=5)

# Интерактивная панель.
d.rounded_rectangle((34, 46, 222, 174), radius=18, fill=(232, 244, 255, 255))
d.rounded_rectangle((48, 59, 208, 157), radius=10, fill=(24, 99, 214, 255))
d.rounded_rectangle((58, 68, 198, 148), radius=7, fill=(52, 140, 255, 255))

# Дублирование: два белых «экрана» и стрелка.
d.rounded_rectangle((78, 86, 132, 130), radius=6, outline=(255, 255, 255, 255), width=6)
d.rounded_rectangle((112, 99, 166, 143), radius=6, outline=(255, 255, 255, 255), width=6)
d.line((99, 108, 145, 108), fill=(255, 255, 255, 255), width=5)
d.polygon([(145, 108), (135, 101), (135, 115)], fill=(255, 255, 255, 255))

# Подставка.
d.rounded_rectangle((116, 174, 140, 204), radius=4, fill=(205, 225, 250, 255))
d.rounded_rectangle((81, 202, 175, 217), radius=7, fill=(205, 225, 250, 255))

# Зелёная галочка «всё работает».
d.ellipse((168, 160, 232, 224), fill=(11, 107, 61, 255), outline=(89, 241, 155, 255), width=4)
d.line((184, 193, 197, 206), fill=(255, 255, 255, 255), width=8)
d.line((197, 206, 219, 177), fill=(255, 255, 255, 255), width=8)

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "src" / "PanelRemote" / "Assets" / "app.ico"
OUTPUT.parent.mkdir(parents=True, exist_ok=True)
img.save(OUTPUT, sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)])
print(f"icon written: {OUTPUT}")
