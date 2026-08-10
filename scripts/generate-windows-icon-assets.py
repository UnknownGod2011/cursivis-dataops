#!/usr/bin/env python3
"""Generate the deterministic Cursivis Windows icon family from one vector-like mark."""

from __future__ import annotations

from pathlib import Path
from typing import Iterable

from PIL import Image, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "apps" / "windows" / "Cursivis.Windows.App" / "Assets"
ICON_SIZES = (16, 20, 24, 32, 48, 64, 128, 256)
MASTER_SIZE = 2048


def _interpolate(start: tuple[int, int, int], end: tuple[int, int, int], amount: float):
    return tuple(round(a + ((b - a) * amount)) for a, b in zip(start, end))


def _rounded_gradient(
    size: int,
    margin: int,
    radius: int,
    start: tuple[int, int, int],
    end: tuple[int, int, int],
) -> Image.Image:
    gradient = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    pixels = gradient.load()
    for y in range(size):
        color = _interpolate(start, end, y / max(1, size - 1))
        for x in range(size):
            pixels[x, y] = (*color, 255)

    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (margin, margin, size - margin, size - margin),
        radius=radius,
        fill=255,
    )
    gradient.putalpha(mask)
    return gradient


def _round_line(
    draw: ImageDraw.ImageDraw,
    points: Iterable[tuple[int, int]],
    fill: tuple[int, int, int, int],
    width: int,
) -> None:
    points = list(points)
    draw.line(points, fill=fill, width=width, joint="curve")
    radius = width // 2
    for x, y in (points[0], points[-1]):
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=fill)


def build_master() -> Image.Image:
    size = MASTER_SIZE
    image = _rounded_gradient(
        size,
        margin=92,
        radius=430,
        start=(5, 20, 54),
        end=(15, 48, 108),
    )
    draw = ImageDraw.Draw(image)

    # Selection corners: recognizable context framing without a generic sparkle.
    _round_line(
        draw,
        [(1190, 430), (1550, 430), (1610, 490), (1610, 850)],
        (25, 215, 247, 255),
        94,
    )
    _round_line(
        draw,
        [(850, 1610), (490, 1610), (430, 1550), (430, 1190)],
        (61, 132, 246, 255),
        94,
    )

    cursor = [
        (595, 505),
        (1470, 1190),
        (1115, 1225),
        (1335, 1580),
        (1135, 1700),
        (915, 1345),
        (620, 1580),
    ]
    draw.polygon(cursor, fill=(249, 250, 251, 255), outline=(3, 16, 49, 255), width=80)
    draw.line(cursor + [cursor[0]], fill=(3, 16, 49, 255), width=80, joint="curve")

    # Intelligence node anchored to the cursor rather than floating as a sparkle.
    orb_center = (1235, 1505)
    orb_radius = 155
    draw.ellipse(
        (
            orb_center[0] - orb_radius - 34,
            orb_center[1] - orb_radius - 34,
            orb_center[0] + orb_radius + 34,
            orb_center[1] + orb_radius + 34,
        ),
        fill=(3, 16, 49, 255),
    )
    orb = Image.new("RGBA", (orb_radius * 2, orb_radius * 2), (0, 0, 0, 0))
    orb_pixels = orb.load()
    for y in range(orb.height):
        amount = y / max(1, orb.height - 1)
        color = _interpolate((25, 214, 247), (130, 55, 246), amount)
        for x in range(orb.width):
            orb_pixels[x, y] = (*color, 255)
    orb_mask = Image.new("L", orb.size, 0)
    ImageDraw.Draw(orb_mask).ellipse((0, 0, orb.width - 1, orb.height - 1), fill=255)
    orb.putalpha(orb_mask)
    image.alpha_composite(orb, (orb_center[0] - orb_radius, orb_center[1] - orb_radius))

    return image


def _resize(master: Image.Image, size: tuple[int, int]) -> Image.Image:
    result = master.resize(size, Image.Resampling.LANCZOS)
    if min(size) <= 64:
        result = result.filter(ImageFilter.UnsharpMask(radius=0.7, percent=115, threshold=2))
    return result


def _square_asset(master: Image.Image, size: int) -> Image.Image:
    return _resize(master, (size, size))


def _centered_asset(master: Image.Image, size: tuple[int, int], mark_size: int) -> Image.Image:
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    mark = _square_asset(master, mark_size)
    canvas.alpha_composite(mark, ((size[0] - mark_size) // 2, (size[1] - mark_size) // 2))
    return canvas


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    icon_directory = ASSETS / "IconSizes"
    icon_directory.mkdir(parents=True, exist_ok=True)
    master = build_master()

    for size in ICON_SIZES:
        _square_asset(master, size).save(
            icon_directory / f"CursivisIcon-{size}.png",
            optimize=True,
        )

    ico_source = _square_asset(master, 256)
    ico_source.save(
        ASSETS / "AppIcon.ico",
        format="ICO",
        sizes=[(size, size) for size in ICON_SIZES],
        bitmap_format="png",
    )

    _square_asset(master, 48).save(ASSETS / "LockScreenLogo.scale-200.png", optimize=True)
    _centered_asset(master, (1240, 600), 320).save(
        ASSETS / "SplashScreen.scale-200.png",
        optimize=True,
    )
    _square_asset(master, 300).save(ASSETS / "Square150x150Logo.scale-200.png", optimize=True)
    _square_asset(master, 88).save(ASSETS / "Square44x44Logo.scale-200.png", optimize=True)
    _square_asset(master, 24).save(
        ASSETS / "Square44x44Logo.targetsize-24_altform-unplated.png",
        optimize=True,
    )
    _square_asset(master, 48).save(
        ASSETS / "Square44x44Logo.targetsize-48_altform-lightunplated.png",
        optimize=True,
    )
    _square_asset(master, 50).save(ASSETS / "StoreLogo.png", optimize=True)
    _centered_asset(master, (620, 300), 230).save(
        ASSETS / "Wide310x150Logo.scale-200.png",
        optimize=True,
    )

    master.resize((512, 512), Image.Resampling.LANCZOS).save(
        ASSETS / "Brand" / "CursivisIconMaster.png",
        optimize=True,
    )


if __name__ == "__main__":
    main()
