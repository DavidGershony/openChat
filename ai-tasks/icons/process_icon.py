"""Process the pre-stripped Scramble icon: square-crop to circle bounds and generate Android mipmap launcher icons."""
from PIL import Image
import os

SRC = 'ai-tasks/Scramble icon.png'
OUT_DIR = 'ai-tasks/icons'
os.makedirs(OUT_DIR, exist_ok=True)

im = Image.open(SRC).convert('RGBA')
w, h = im.size
print(f'source: {w}x{h}')

# Find bounds using alpha channel (background already transparent in this asset).
# Use a threshold of 16 to ignore stray near-zero alpha pixels.
px = im.load()
min_x, min_y, max_x, max_y = w, h, 0, 0
for y in range(h):
    for x in range(w):
        if px[x, y][3] >= 16:
            if x < min_x: min_x = x
            if y < min_y: min_y = y
            if x > max_x: max_x = x
            if y > max_y: max_y = y

print(f'icon bounds: ({min_x},{min_y}) -> ({max_x},{max_y})')

# Square crop centered on icon, with small padding
cx = (min_x + max_x) // 2
cy = (min_y + max_y) // 2
size = max(max_x - min_x, max_y - min_y)
size = int(size * 1.04)
half = size // 2
left = max(0, cx - half)
top = max(0, cy - half)
right = min(w, cx + half)
bottom = min(h, cy + half)
side = min(right - left, bottom - top)
right = left + side
bottom = top + side
print(f'square crop: ({left},{top}) -> ({right},{bottom}) side={side}')

cropped = im.crop((left, top, right, bottom))
master_path = os.path.join(OUT_DIR, 'scramble_icon_master.png')
cropped.save(master_path)
print(f'saved master: {master_path} {cropped.size}')

# Generate Android mipmap sizes
densities = {
    'mipmap-mdpi': 48,
    'mipmap-hdpi': 72,
    'mipmap-xhdpi': 96,
    'mipmap-xxhdpi': 144,
    'mipmap-xxxhdpi': 192,
}

base = 'src/Scramble.Android/Resources'
for folder, sz in densities.items():
    target_dir = os.path.join(base, folder)
    os.makedirs(target_dir, exist_ok=True)
    resized = cropped.resize((sz, sz), Image.LANCZOS)
    target = os.path.join(target_dir, 'ic_launcher.png')
    resized.save(target, optimize=True)
    print(f'wrote {target} ({sz}x{sz})')

cropped.resize((512, 512), Image.LANCZOS).save(os.path.join(OUT_DIR, 'playstore_icon_512.png'), optimize=True)
cropped.resize((1024, 1024), Image.LANCZOS).save(os.path.join(OUT_DIR, 'icon_1024.png'), optimize=True)
print('done')
