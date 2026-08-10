using Cursivis.Domain.Context;

namespace Cursivis.Application.Context;

public static class TextContextKindClassifier
{
    private static readonly string[] CodeMarkers =
    [
        "```", "using ", "namespace ", " class ", "public ", "private ",
        "function ", "const ", "let ", "var ", "def ", "import ", "return ",
        "=>", "</", "#include", "SELECT ",
    ];

    public static ContextKind Classify(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string normalized = text.Trim();
        string[] lines = normalized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        if (LooksLikeTable(lines))
        {
            return ContextKind.Table;
        }

        if (LooksLikeEmail(normalized, lines))
        {
            return ContextKind.Email;
        }

        if (LooksLikeCode(normalized, lines))
        {
            return ContextKind.Code;
        }

        if (normalized.EndsWith('?'))
        {
            return ContextKind.Question;
        }

        int wordCount = normalized.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries).Length;
        return wordCount >= 40 ? ContextKind.Prose : ContextKind.Text;
    }

    private static bool LooksLikeTable(IReadOnlyList<string> lines)
    {
        if (lines.Count < 2)
        {
            return false;
        }

        int tabularLines = lines.Count(static line =>
            line.Count(static character => character == '\t') >= 1 ||
            line.Count(static character => character == '|') >= 2);
        return tabularLines >= 2;
    }

    private static bool LooksLikeEmail(string text, IReadOnlyList<string> lines) =>
        text.Contains("From:", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("To:", StringComparison.OrdinalIgnoreCase) ||
        (lines.Count >= 2 &&
         (lines[0].StartsWith("Hi ", StringComparison.OrdinalIgnoreCase) ||
          lines[0].StartsWith("Hello ", StringComparison.OrdinalIgnoreCase) ||
          lines[0].StartsWith("Dear ", StringComparison.OrdinalIgnoreCase)) &&
         (text.Contains("Regards", StringComparison.OrdinalIgnoreCase) ||
          text.Contains("Thanks", StringComparison.OrdinalIgnoreCase)));

    private static bool LooksLikeCode(string text, IReadOnlyList<string> lines)
    {
        int markerCount = CodeMarkers.Count(marker =>
            text.Contains(marker, StringComparison.OrdinalIgnoreCase));
        int structuralSignals = 0;
        if (text.Contains('{') && text.Contains('}'))
        {
            structuralSignals++;
        }

        if (text.Contains(';'))
        {
            structuralSignals++;
        }

        if (lines.Any(static line =>
                line.StartsWith("    ", StringComparison.Ordinal) ||
                line.StartsWith('\t')))
        {
            structuralSignals++;
        }

        return markerCount >= 1 && markerCount + structuralSignals >= 2 ||
               structuralSignals >= 3;
    }
}
