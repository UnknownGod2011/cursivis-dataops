namespace Cursivis.Application.Context;

public static class ApproximateColorNamer
{
    private static readonly NamedColor[] Palette =
    [
        new("black", 0x00, 0x00, 0x00),
        new("charcoal", 0x36, 0x3C, 0x44),
        new("gray", 0x80, 0x80, 0x80),
        new("silver", 0xC0, 0xC0, 0xC0),
        new("white", 0xFF, 0xFF, 0xFF),
        new("red", 0xE5, 0x39, 0x35),
        new("orange", 0xFB, 0x8C, 0x00),
        new("amber", 0xFF, 0xB3, 0x00),
        new("yellow", 0xFD, 0xD8, 0x35),
        new("lime", 0xC0, 0xCA, 0x33),
        new("green", 0x43, 0xA0, 0x47),
        new("teal", 0x00, 0x89, 0x7B),
        new("cyan", 0x00, 0xAC, 0xC1),
        new("blue", 0x1E, 0x88, 0xE5),
        new("indigo", 0x39, 0x49, 0xAB),
        new("purple", 0x8E, 0x24, 0xAA),
        new("magenta", 0xD8, 0x1B, 0x60),
        new("brown", 0x6D, 0x4C, 0x41),
        new("beige", 0xD7, 0xCC, 0xB8),
        new("navy", 0x00, 0x33, 0x66),
    ];

    public static string Find(byte red, byte green, byte blue)
    {
        NamedColor best = Palette[0];
        long bestDistance = long.MaxValue;
        foreach (NamedColor candidate in Palette)
        {
            long redDelta = red - candidate.Red;
            long greenDelta = green - candidate.Green;
            long blueDelta = blue - candidate.Blue;
            long distance = (redDelta * redDelta * 3) +
                            (greenDelta * greenDelta * 4) +
                            (blueDelta * blueDelta * 2);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best.Name;
    }

    private readonly record struct NamedColor(string Name, byte Red, byte Green, byte Blue);
}
