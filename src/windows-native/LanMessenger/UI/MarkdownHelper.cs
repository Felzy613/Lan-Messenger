using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System.Text;
using Windows.UI.Text;

namespace LanMessenger.UI;

// Minimal Markdown → RichTextBlock renderer for GitHub release notes.
// Handles: ATX headings (# / ## / ###), bullet lists (- / *), bold (**),
// inline code (`), Markdown links ([text](url)), and plain paragraphs.
// Safe: no script execution, all links open via Hyperlink.NavigateUri
// which honours the system default browser.
internal static class MarkdownHelper
{
    public static void PopulateBlocks(RichTextBlock block, string markdown)
    {
        block.Blocks.Clear();
        if (string.IsNullOrWhiteSpace(markdown)) return;

        var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        Paragraph? openPara = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // Empty line flushes the current paragraph so the next non-empty
            // line starts a fresh one (paragraph break).
            if (string.IsNullOrWhiteSpace(line))
            {
                openPara = null;
                continue;
            }

            // ATX heading: # / ## / ###
            if (line.StartsWith('#'))
            {
                var level = 0;
                while (level < line.Length && line[level] == '#') level++;
                if (level <= 4 && level < line.Length && line[level] == ' ')
                {
                    openPara = null;
                    var text = line[(level + 1)..].Trim();
                    var para = new Paragraph
                    {
                        Margin = new Thickness(0, level <= 2 ? 6 : 3, 0, 2),
                    };
                    para.Inlines.Add(new Run
                    {
                        Text       = text,
                        FontWeight = FontWeights.SemiBold,
                        FontSize   = level == 1 ? 14 : (level == 2 ? 13 : 12),
                    });
                    block.Blocks.Add(para);
                    continue;
                }
            }

            // Bullet list: - text  or  * text
            bool isBullet = (line.StartsWith("- ") || line.StartsWith("* "));
            if (isBullet)
            {
                openPara = null;
                line = "• " + line[2..];
            }

            // Start a new paragraph for bullets; merge consecutive plain lines.
            if (openPara is null || isBullet)
            {
                openPara = new Paragraph
                {
                    Margin       = new Thickness(isBullet ? 10 : 0, 0, 0, 1),
                    TextIndent   = isBullet ? -10 : 0,
                };
                block.Blocks.Add(openPara);
            }
            else
            {
                openPara.Inlines.Add(new LineBreak());
            }

            ParseInline(line, openPara.Inlines);
        }
    }

    // -----------------------------------------------------------------------

    private static void ParseInline(string text, InlineCollection dest)
    {
        var sb  = new StringBuilder();
        var i   = 0;

        void Flush()
        {
            if (sb.Length == 0) return;
            dest.Add(new Run { Text = sb.ToString() });
            sb.Clear();
        }

        while (i < text.Length)
        {
            // **bold**
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    Flush();
                    dest.Add(new Run
                    {
                        Text       = text[(i + 2)..end],
                        FontWeight = FontWeights.SemiBold,
                    });
                    i = end + 2;
                    continue;
                }
            }

            // *italic* (single asterisk, not part of a bullet prefix)
            if (text[i] == '*' && (i == 0 || text[i - 1] != '*'))
            {
                var end = text.IndexOf('*', i + 1);
                if (end >= 0 && (end + 1 >= text.Length || text[end + 1] != '*'))
                {
                    Flush();
                    dest.Add(new Run
                    {
                        Text       = text[(i + 1)..end],
                        FontStyle  = Windows.UI.Text.FontStyle.Italic,
                    });
                    i = end + 1;
                    continue;
                }
            }

            // `inline code`
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end >= 0)
                {
                    Flush();
                    dest.Add(new Run
                    {
                        Text       = text[(i + 1)..end],
                        FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                    });
                    i = end + 1;
                    continue;
                }
            }

            // [link text](url)
            if (text[i] == '[')
            {
                var cb = text.IndexOf(']', i + 1);
                if (cb >= 0 && cb + 1 < text.Length && text[cb + 1] == '(')
                {
                    var cp = text.IndexOf(')', cb + 2);
                    if (cp >= 0)
                    {
                        var linkText = text[(i + 1)..cb];
                        var linkUrl  = text[(cb + 2)..cp];
                        if (Uri.TryCreate(linkUrl, UriKind.Absolute, out var uri))
                        {
                            Flush();
                            var hyper = new Hyperlink { NavigateUri = uri };
                            hyper.Inlines.Add(new Run { Text = linkText.Length > 0 ? linkText : linkUrl });
                            dest.Add(hyper);
                            i = cp + 1;
                            continue;
                        }
                    }
                }
            }

            sb.Append(text[i++]);
        }
        Flush();
    }
}
