using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdCode = Markdig.Syntax.CodeBlock;
using MdContainer = Markdig.Syntax.ContainerBlock;
using MdFenced = Markdig.Syntax.FencedCodeBlock;
using MdHeading = Markdig.Syntax.HeadingBlock;
using MdHtml = Markdig.Syntax.HtmlBlock;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdLeaf = Markdig.Syntax.LeafBlock;
using MdLink = Markdig.Syntax.Inlines.LinkInline;
using MdList = Markdig.Syntax.ListBlock;
using MdListItem = Markdig.Syntax.ListItemBlock;
using MdParagraph = Markdig.Syntax.ParagraphBlock;
using MdQuote = Markdig.Syntax.QuoteBlock;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdRule = Markdig.Syntax.ThematicBreakBlock;

namespace WorkTiles.Shared.Markdown;

/// <summary>更新日志解析结果。超过块数或输入上限时 <see cref="Truncated"/> 为 true。</summary>
public sealed class ChangelogDocument
{
    public ChangelogDocument(IReadOnlyList<ChangelogBlock> blocks, bool truncated)
    {
        Blocks = blocks;
        Truncated = truncated;
    }

    public IReadOnlyList<ChangelogBlock> Blocks { get; }
    public bool Truncated { get; }
}

public abstract record ChangelogBlock;
public sealed record ParagraphBlock(IReadOnlyList<ChangelogInline> Inlines) : ChangelogBlock;
public sealed record HeadingBlock(int Level, IReadOnlyList<ChangelogInline> Inlines) : ChangelogBlock;
public sealed record ListBlock(bool Ordered, int Start, IReadOnlyList<ListItem> Items) : ChangelogBlock;
public sealed record ListItem(bool? Checked, IReadOnlyList<ChangelogBlock> Blocks);
public sealed record QuoteBlock(IReadOnlyList<ChangelogBlock> Blocks) : ChangelogBlock;
public sealed record CodeBlock(string Text, string? Language) : ChangelogBlock;
public sealed record ThematicBreakBlock : ChangelogBlock;
public sealed record TableBlock(TableRow Header, IReadOnlyList<TableRow> Rows) : ChangelogBlock;
public sealed record TableRow(IReadOnlyList<IReadOnlyList<ChangelogInline>> Cells);

public abstract record ChangelogInline;
public sealed record TextInline(string Text) : ChangelogInline;
public sealed record StyleInline(bool Bold, bool Italic, bool Strike, IReadOnlyList<ChangelogInline> Children) : ChangelogInline;
public sealed record CodeSpanInline(string Text) : ChangelogInline;
public sealed record LinkInline(IReadOnlyList<ChangelogInline> Children, string Url) : ChangelogInline;
public sealed record LineBreakInline : ChangelogInline;

/// <summary>
/// 把 GitHub Release 正文解析成不含 HTML、不含图片节点的文档。
/// 正文来自网络：只保留 http/https 链接，原始 HTML 不当成结构。
/// </summary>
public static class ChangelogMarkdown
{
    public const int MaxInputLength = 100_000;
    public const int MaxTopLevelBlocks = 500;
    public const int MaxDepth = 8;

    private static readonly Markdig.MarkdownPipeline Pipeline = new Markdig.MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    // 占位符不含 Markdown 特殊字符，解析后再换回原文，避免标签里的地址被自动链接。
    private static readonly Regex SlotPattern = new(@"(\d+)", RegexOptions.Compiled);
    private static readonly Regex ScriptStylePattern = new(
        @"<(script|style)\b[^>]*>.*?(?:</\1\s*>|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new(
        @"</?[A-Za-z][A-Za-z0-9:-]*(?:\s+(?:[^<>""']|""[^""]*""|'[^']*')*)?\s*/?>",
        RegexOptions.Compiled);
    private static readonly Regex HtmlCommentPattern = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex EntityPattern = new(
        @"&(lt|gt|amp|quot|#39|apos|#x[0-9a-fA-F]+|#\d+);",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ChangelogDocument Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return new([], false);
        if (markdown.Length > MaxInputLength) return Plain(markdown);

        List<string> slots = [];
        string protectedMarkdown = ProtectHtml(markdown, slots);
        Markdig.Syntax.MarkdownDocument document;
        try { document = Markdig.Markdown.Parse(protectedMarkdown, Pipeline); }
        catch (Exception) { return Plain(markdown); }

        Context context = new(slots);
        return new(context.Walk(document, depth: 1, topLevel: true), context.Truncated);
    }

    /// <summary>唯一的协议检查。非 http/https、没有主机、或含控制字符的地址一律拒绝。</summary>
    public static bool TryGetSafeHttpUri(string? raw, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string trimmed = raw.Trim();
        foreach (char c in trimmed)
            if (c < ' ') return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed)) return false;
        if (!parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (parsed.Host.Length == 0) return false;
        uri = parsed;
        return true;
    }

    /// <summary>残留 HTML 节点的纯文本。不读 href/src/alt，script 与 style 连正文一起丢掉。</summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        string stripped = ScriptStylePattern.Replace(html, "");
        stripped = HtmlTagPattern.Replace(stripped, "");
        stripped = Regex.Replace(stripped, @"<[^>]*>", "");
        return DecodeEntities(stripped);
    }

    private static ChangelogDocument Plain(string markdown)
    {
        string text = Clean(markdown);
        if (text.Length > MaxInputLength) text = text[..MaxInputLength];
        return text.Length == 0
            ? new([], true)
            : new([new ParagraphBlock([new TextInline(text)])], true);
    }

    private static string ProtectHtml(string markdown, List<string> slots)
    {
        string Slot(string value)
        {
            slots.Add(value);
            return $"{slots.Count - 1}";
        }

        string shielded = HtmlCommentPattern.Replace(markdown, match => Slot(match.Value));
        shielded = ScriptStylePattern.Replace(shielded, match => Slot(match.Value));
        return HtmlTagPattern.Replace(shielded, match => Slot(match.Value));
    }

    private static string Expand(string text, List<string> slots) =>
        SlotPattern.Replace(text, match =>
        {
            if (!int.TryParse(match.Groups[1].Value, out int index) || (uint)index >= (uint)slots.Count) return match.Value;
            return slots[index];
        });

    private static string Clean(string text) => text.Replace("\0", "");

    private static string DecodeEntities(string text) => EntityPattern.Replace(text, match =>
    {
        string token = match.Groups[1].Value;
        if (token.Equals("lt", StringComparison.OrdinalIgnoreCase)) return "<";
        if (token.Equals("gt", StringComparison.OrdinalIgnoreCase)) return ">";
        if (token.Equals("amp", StringComparison.OrdinalIgnoreCase)) return "&";
        if (token.Equals("quot", StringComparison.OrdinalIgnoreCase)) return "\"";
        if (token.Equals("apos", StringComparison.OrdinalIgnoreCase) || token == "#39") return "'";
        if (token[0] != '#') return match.Value;
        bool hex = token.Length > 1 && (token[1] == 'x' || token[1] == 'X');
        string digits = token[(hex ? 2 : 1)..];
        if (digits.Length == 0) return match.Value;
        if (!int.TryParse(digits, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer, null, out int code))
            return match.Value;
        if (code < 0 || code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF) return match.Value;
        return char.ConvertFromUtf32(code);
    });

    private sealed class Context
    {
        private readonly List<string> _slots;
        private bool _dropSpace;
        public bool Truncated { get; private set; }

        public Context(List<string> slots) => _slots = slots;

        public List<ChangelogBlock> Walk(MdContainer container, int depth, bool topLevel)
        {
            List<ChangelogBlock> result = [];
            foreach (MdBlock block in container)
            {
                if (block is LinkReferenceDefinitionGroup) continue;
                if (topLevel && result.Count >= MaxTopLevelBlocks)
                {
                    Truncated = true;
                    break;
                }
                ChangelogBlock? mapped = Map(block, depth);
                if (mapped is not null) result.Add(mapped);
            }
            return result;
        }

        private ChangelogBlock? Map(MdBlock block, int depth)
        {
            if (depth > MaxDepth)
            {
                string plain = Visible(block);
                return plain.Length == 0 ? null : new ParagraphBlock([new TextInline(plain)]);
            }

            switch (block)
            {
                case MdHeading heading:
                    return new HeadingBlock(Math.Clamp(heading.Level, 1, 6), Inlines(heading.Inline));
                case MdParagraph paragraph:
                    IReadOnlyList<ChangelogInline> inlines = Inlines(paragraph.Inline);
                    return inlines.Count == 0 ? null : new ParagraphBlock(inlines);
                case MdList list:
                    return MapList(list, depth);
                case MdQuote quote:
                    return new QuoteBlock(Walk(quote, depth + 1, topLevel: false));
                case MdFenced fenced:
                    return new CodeBlock(CodeText(fenced), Language(fenced.Info));
                case MdCode code:
                    return new CodeBlock(CodeText(code), null);
                case MdRule:
                    return new ThematicBreakBlock();
                case MdTable table:
                    return MapTable(table);
                case MdHtml html:
                    string htmlText = HtmlToText(html.Lines.ToString());
                    return htmlText.Length == 0 ? null : new ParagraphBlock([new TextInline(htmlText)]);
                default:
                    string fallback = Visible(block);
                    return fallback.Length == 0 ? null : new ParagraphBlock([new TextInline(fallback)]);
            }
        }

        private ListBlock MapList(MdList list, int depth)
        {
            int start = 1;
            if (list.IsOrdered && int.TryParse(list.OrderedStart, out int parsed) && parsed > 0) start = parsed;
            List<ListItem> items = [];
            foreach (MdBlock child in list)
            {
                if (child is not MdListItem item) continue;
                items.Add(new ListItem(TaskState(item), Walk(item, depth + 1, topLevel: false)));
            }
            return new ListBlock(list.IsOrdered, start, items);
        }

        private static bool? TaskState(MdListItem item)
        {
            foreach (MdBlock child in item)
            {
                if (child is not MdParagraph paragraph || paragraph.Inline is null) return null;
                for (MdInline? inline = paragraph.Inline.FirstChild; inline is not null; inline = inline.NextSibling)
                {
                    if (inline is TaskList task) return task.Checked;
                    if (inline is LiteralInline literal && string.IsNullOrWhiteSpace(literal.Content.ToString())) continue;
                    return null;
                }
                return null;
            }
            return null;
        }

        private TableBlock? MapTable(MdTable table)
        {
            List<MdTableRow> source = [];
            foreach (MdBlock child in table)
                if (child is MdTableRow row) source.Add(row);
            if (source.Count == 0) return null;
            MdTableRow headerSource = source.Find(row => row.IsHeader) ?? source[0];
            TableRow header = MapRow(headerSource);
            List<TableRow> rows = [];
            foreach (MdTableRow row in source)
                if (!ReferenceEquals(row, headerSource)) rows.Add(MapRow(row));
            if (header.Cells.Count == 0 && rows.TrueForAll(row => row.Cells.Count == 0)) return null;
            return new TableBlock(header, rows);
        }

        private TableRow MapRow(MdTableRow row)
        {
            List<IReadOnlyList<ChangelogInline>> cells = [];
            foreach (MdBlock child in row)
                if (child is TableCell cell) cells.Add(CellInlines(cell));
            return new TableRow(cells);
        }

        private IReadOnlyList<ChangelogInline> CellInlines(TableCell cell)
        {
            List<ChangelogInline> inlines = [];
            foreach (MdBlock child in cell)
            {
                if (inlines.Count > 0) inlines.Add(new LineBreakInline());
                if (child is MdLeaf leaf) inlines.AddRange(Inlines(leaf.Inline));
                else
                {
                    string plain = Visible(child);
                    if (plain.Length > 0) inlines.Add(new TextInline(plain));
                }
            }
            return Normalize(inlines);
        }

        private IReadOnlyList<ChangelogInline> Inlines(ContainerInline? container)
        {
            List<ChangelogInline> result = [];
            if (container is null) return result;
            bool saved = _dropSpace;
            try
            {
                for (MdInline? inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
                {
                    if (inline is TaskList)
                    {
                        _dropSpace = true;
                        continue;
                    }
                    result.AddRange(MapInline(inline));
                }
            }
            finally { _dropSpace = saved; }
            return Normalize(result);
        }

        private List<ChangelogInline> MapInline(MdInline inline)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    string text = Expand(Clean(literal.Content.ToString()), _slots);
                    if (_dropSpace && text.StartsWith(' ')) text = text[1..];
                    if (_dropSpace && text.Length > 0) _dropSpace = false;
                    return text.Length == 0 ? [] : [new TextInline(text)];
                case CodeInline code:
                    _dropSpace = false;
                    string codeText = Expand(Clean(code.Content), _slots);
                    return codeText.Length == 0 ? [] : [new CodeSpanInline(codeText)];
                case Markdig.Syntax.Inlines.LineBreakInline:
                    _dropSpace = false;
                    return [new LineBreakInline()];
                case EmphasisInline emphasis:
                    _dropSpace = false;
                    bool strike = emphasis.DelimiterChar == '~';
                    bool bold = !strike && emphasis.DelimiterCount >= 2;
                    bool italic = !strike && emphasis.DelimiterCount % 2 == 1;
                    IReadOnlyList<ChangelogInline> children = Inlines(emphasis);
                    if (children.Count == 0) return [];
                    if (!strike && !bold && !italic) return children.ToList();
                    return [new StyleInline(bold, italic, strike, children)];
                case MdLink link:
                    _dropSpace = false;
                    return MapLink(link);
                case AutolinkInline autolink:
                    _dropSpace = false;
                    string label = Expand(Clean(autolink.Url), _slots);
                    if (!autolink.IsEmail && TryGetSafeHttpUri(autolink.Url, out Uri? autoUri))
                        return [new LinkInline([new TextInline(label.Length == 0 ? autoUri!.AbsoluteUri : label)], autoUri!.AbsoluteUri)];
                    return label.Length == 0 ? [] : [new TextInline(label)];
                case HtmlInline html:
                    _dropSpace = false;
                    string htmlText = HtmlToText(html.Tag);
                    return htmlText.Length == 0 ? [] : [new TextInline(htmlText)];
                case HtmlEntityInline entity:
                    _dropSpace = false;
                    string entityText = Clean(entity.Transcoded.ToString());
                    return entityText.Length == 0 ? [] : [new TextInline(entityText)];
                case EmphasisDelimiterInline delimiter:
                    _dropSpace = false;
                    string marks = Expand(Clean(delimiter.ToLiteral()), _slots);
                    return marks.Length == 0 ? [] : [new TextInline(marks)];
                case ContainerInline container:
                    _dropSpace = false;
                    return Inlines(container).ToList();
                default:
                    _dropSpace = false;
                    return [];
            }
        }

        private List<ChangelogInline> MapLink(MdLink link)
        {
            List<ChangelogInline> children = Inlines(link).ToList();
            bool safe = TryGetSafeHttpUri(link.Url, out Uri? uri);
            if (!safe) return children;
            if (link.IsImage && children.Count == 0) children.Add(new TextInline(uri!.AbsoluteUri));
            return [new LinkInline(children, uri!.AbsoluteUri)];
        }

        private string CodeText(MdCode block) => Expand(Clean(block.Lines.ToString()), _slots);

        private static string? Language(string? info)
        {
            if (string.IsNullOrWhiteSpace(info)) return null;
            string token = info.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
            if (token.Length > 32 || token.IndexOfAny(['<', '>', '"', '\'', '{']) >= 0) return null;
            return token;
        }

        private string Visible(MdBlock block)
        {
            switch (block)
            {
                case MdLeaf leaf when leaf.Inline is not null:
                    return TextOf(Inlines(leaf.Inline));
                case MdCode code:
                    return CodeText(code);
                case MdContainer container:
                    StringBuilder builder = new();
                    foreach (MdBlock child in container)
                    {
                        string part = Visible(child);
                        if (part.Length == 0) continue;
                        if (builder.Length > 0) builder.Append('\n');
                        builder.Append(part);
                    }
                    return builder.ToString();
                default:
                    return "";
            }
        }

        private static string TextOf(IReadOnlyList<ChangelogInline> inlines)
        {
            StringBuilder builder = new();
            foreach (ChangelogInline inline in inlines)
            {
                switch (inline)
                {
                    case TextInline text: builder.Append(text.Text); break;
                    case CodeSpanInline code: builder.Append(code.Text); break;
                    case LineBreakInline: builder.Append('\n'); break;
                    case StyleInline style: builder.Append(TextOf(style.Children)); break;
                    case LinkInline link: builder.Append(TextOf(link.Children)); break;
                }
            }
            return builder.ToString();
        }

        private static List<ChangelogInline> Normalize(List<ChangelogInline> items)
        {
            List<ChangelogInline> result = [];
            foreach (ChangelogInline item in items)
            {
                if (item is TextInline { Text.Length: 0 }) continue;
                if (item is TextInline text && result.Count > 0 && result[^1] is TextInline previous)
                    result[^1] = new TextInline(previous.Text + text.Text);
                else result.Add(item);
            }
            return result;
        }
    }
}
