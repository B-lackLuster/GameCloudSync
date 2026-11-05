using System.Text.RegularExpressions;

namespace GameCloudSync.Core.Util;

public enum NoteKind { Heading, Bullet, Text }

public sealed record NoteLine(NoteKind Kind, string Text);

/// <summary>Turns a release's Markdown notes into plain lines for the update dialog.</summary>
public static partial class ReleaseNotes
{
    public static List<NoteLine> Parse(string? markdown)
    {
        var lines = new List<NoteLine>();
        var inCode = false;
        foreach (var raw in (markdown ?? "").ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                if (line.Length > 0)
                    lines.Add(new NoteLine(NoteKind.Text, line));
                continue;
            }
            if (line.Length == 0 || IsRule(line))
                continue;
            if (line.StartsWith('>'))
                line = line.TrimStart('>', ' ');
            if (HeadingRegex().Match(line) is { Success: true } heading)
                Add(NoteKind.Heading, heading.Groups[1].Value);
            else if (BulletRegex().Match(line) is { Success: true } bullet)
                Add(NoteKind.Bullet, bullet.Groups[1].Value);
            else
                Add(NoteKind.Text, line);
        }
        return lines;

        void Add(NoteKind kind, string text)
        {
            text = Inline(text);
            if (text.Length > 0)
                lines.Add(new NoteLine(kind, text));
        }
    }

    // Images go; links, **bold**, `code` and HTML tags keep only their text.
    private static string Inline(string text)
    {
        text = ImageRegex().Replace(text, "");
        text = LinkRegex().Replace(text, "$1");
        text = TagRegex().Replace(text, "");
        return text.Replace("**", "").Replace("__", "").Replace("`", "").Trim();
    }

    // "---", "***" or "___" (spaces allowed) draws a line in Markdown; there is nothing to show.
    private static bool IsRule(string line)
    {
        var marks = line.Replace(" ", "");
        return marks.Length >= 3 && (marks.All(c => c == '-') || marks.All(c => c == '*') || marks.All(c => c == '_'));
    }

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*#*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^[-*+]\s+(.+)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")]
    private static partial Regex TagRegex();
}
