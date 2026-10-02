"""Generate Neuroset's reproducible Windows branding assets with Pillow."""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "App" / "Assets"
INDIGO = (124, 108, 242, 255)
VIOLET = (166, 121, 255, 255)
BLUE = (66, 182, 255, 255)
ROSE = (239, 119, 183, 255)


def cubic(p0, p1, p2, p3, steps=80):
    points = []
    for index in range(steps + 1):
        t = index / steps
        u = 1 - t
        points.append((
            u**3 * p0[0] + 3 * u**2 * t * p1[0] + 3 * u * t**2 * p2[0] + t**3 * p3[0],
            u**3 * p0[1] + 3 * u**2 * t * p1[1] + 3 * u * t**2 * p2[1] + t**3 * p3[1],
        ))
    return points


def gradient_color(start, end, amount):
    return tuple(round(start[i] + (end[i] - start[i]) * amount) for i in range(4))


def draw_gradient_path(image, points, width, colors):
    draw = ImageDraw.Draw(image)
    count = len(points) - 1
    for index in range(count):
        phase = index / max(1, count - 1)
        section = min(len(colors) - 2, int(phase * (len(colors) - 1)))
        local = phase * (len(colors) - 1) - section
        draw.line([points[index], points[index + 1]], fill=gradient_color(colors[section], colors[section + 1], local), width=width)


def create_mark(size=1024):
    scale = size / 1024
    paths = [
        cubic((244, 686), (142, 555), (174, 320), (350, 218))
        + cubic((350, 218), (520, 119), (752, 181), (826, 354))[1:]
        + cubic((826, 354), (902, 535), (794, 705), (620, 752))[1:]
        + cubic((620, 752), (500, 786), (395, 753), (330, 704))[1:]
        + [(229, 814), (253, 685)],
        cubic((335, 624), (390, 530), (419, 438), (482, 357))
        + cubic((482, 357), (524, 304), (570, 340), (596, 424))[1:]
        + cubic((596, 424), (625, 520), (653, 586), (702, 402))[1:],
    ]
    paths = [[(x * scale, y * scale) for x, y in path] for path in paths]

    glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    for path in paths:
        ImageDraw.Draw(glow).line(path, fill=(104, 100, 255, 190), width=max(3, round(68 * scale)), joint="curve")
    glow = glow.filter(ImageFilter.GaussianBlur(max(2, round(35 * scale))))

    mark = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    mark.alpha_composite(glow)
    draw_gradient_path(mark, paths[0], max(4, round(35 * scale)), [INDIGO, VIOLET, BLUE, INDIGO])
    draw_gradient_path(mark, paths[1], max(4, round(28 * scale)), [ROSE, VIOLET, BLUE])

    nodes = [(350, 218, INDIGO), (826, 354, BLUE), (620, 752, VIOLET), (244, 686, INDIGO), (335, 624, ROSE), (482, 357, VIOLET), (596, 424, BLUE), (702, 402, BLUE)]
    for x, y, color in nodes:
        center = (round(x * scale), round(y * scale))
        radius = max(4, round(22 * scale))
        node_glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        ImageDraw.Draw(node_glow).ellipse((center[0] - radius * 2, center[1] - radius * 2, center[0] + radius * 2, center[1] + radius * 2), fill=(*color[:3], 150))
        mark.alpha_composite(node_glow.filter(ImageFilter.GaussianBlur(max(2, round(15 * scale)))))
        draw = ImageDraw.Draw(mark)
        draw.ellipse((center[0] - radius, center[1] - radius, center[0] + radius, center[1] + radius), fill=color)
        highlight = max(2, radius // 3)
        draw.ellipse((center[0] - radius // 2, center[1] - radius // 2, center[0] - radius // 2 + highlight, center[1] - radius // 2 + highlight), fill=(255, 255, 255, 210))
    return mark


def contain(mark, size, fraction=0.82):
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    edge = round(min(size) * fraction)
    resized = mark.resize((edge, edge), Image.Resampling.LANCZOS)
    canvas.alpha_composite(resized, ((size[0] - edge) // 2, (size[1] - edge) // 2))
    return canvas


def branded_panel(mark, size, include_name=True):
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    icon_edge = round(min(size) * 0.68)
    icon = mark.resize((icon_edge, icon_edge), Image.Resampling.LANCZOS)
    if include_name:
        icon_x = round(size[0] * 0.16)
        canvas.alpha_composite(icon, (icon_x, (size[1] - icon_edge) // 2))
        font_path = Path("C:/Windows/Fonts/seguisb.ttf")
        font = ImageFont.truetype(str(font_path), max(20, round(size[1] * 0.19)))
        draw = ImageDraw.Draw(canvas)
        text_x = icon_x + icon_edge + round(size[0] * 0.025)
        text_y = size[1] // 2
        draw.text((text_x, text_y), "Neuroset", font=font, fill=(245, 247, 251, 255), anchor="lm")
    else:
        canvas.alpha_composite(icon, ((size[0] - icon_edge) // 2, (size[1] - icon_edge) // 2))
    return canvas


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    master = create_mark(2048).resize((1024, 1024), Image.Resampling.LANCZOS)
    master.save(ASSETS / "NeurosetMark.png", optimize=True)
    contain(master, (48, 48), 0.88).save(ASSETS / "LockScreenLogo.scale-200.png", optimize=True)
    contain(master, (300, 300), 0.82).save(ASSETS / "Square150x150Logo.scale-200.png", optimize=True)
    contain(master, (88, 88), 0.86).save(ASSETS / "Square44x44Logo.scale-200.png", optimize=True)
    contain(master, (24, 24), 0.95).save(ASSETS / "Square44x44Logo.targetsize-24_altform-unplated.png", optimize=True)
    contain(master, (48, 48), 0.92).save(ASSETS / "Square44x44Logo.targetsize-48_altform-lightunplated.png", optimize=True)
    contain(master, (50, 50), 0.88).save(ASSETS / "StoreLogo.png", optimize=True)
    branded_panel(master, (620, 300)).save(ASSETS / "Wide310x150Logo.scale-200.png", optimize=True)
    branded_panel(master, (1240, 600)).save(ASSETS / "SplashScreen.scale-200.png", optimize=True)

    ico_sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    icon = contain(master, (256, 256), 0.9)
    icon.save(ASSETS / "AppIcon.ico", format="ICO", sizes=[(value, value) for value in ico_sizes])


if __name__ == "__main__":
    main()
