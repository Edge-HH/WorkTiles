using WorkTiles.Shared.Markdown;

int checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    checks++;
}

static string InlineText(IReadOnlyList<ChangelogInline> inlines)
{
    System.Text.StringBuilder builder = new();
    foreach (ChangelogInline inline in inlines)
    {
        switch (inline)
        {
            case TextInline text: builder.Append(text.Text); break;
            case CodeSpanInline code: builder.Append(code.Text); break;
            case LineBreakInline: builder.Append('\n'); break;
            case StyleInline style: builder.Append(InlineText(style.Children)); break;
            case LinkInline link: builder.Append(InlineText(link.Children)); break;
        }
    }
    return builder.ToString();
}

static string BlockText(ChangelogBlock block) => block switch
{
    ParagraphBlock paragraph => InlineText(paragraph.Inlines),
    HeadingBlock heading => InlineText(heading.Inlines),
    ListBlock list => string.Join("\n", list.Items.Select(ItemText)),
    QuoteBlock quote => string.Join("\n", quote.Blocks.Select(BlockText)),
    CodeBlock code => code.Text,
    TableBlock table => string.Join("\n", new[] { table.Header }.Concat(table.Rows).Select(row => string.Join("|", row.Cells.Select(InlineText)))),
    _ => ""
};

static string ItemText(ListItem item) => string.Join("\n", item.Blocks.Select(BlockText));
static string AllText(ChangelogDocument document) => string.Join("\n", document.Blocks.Select(BlockText));

static List<LinkInline> Links(ChangelogDocument document)
{
    List<LinkInline> links = [];
    foreach (ChangelogBlock block in document.Blocks) CollectBlock(block, links);
    return links;
}

static void CollectBlock(ChangelogBlock block, List<LinkInline> links)
{
    switch (block)
    {
        case ParagraphBlock paragraph: CollectInlines(paragraph.Inlines, links); break;
        case HeadingBlock heading: CollectInlines(heading.Inlines, links); break;
        case ListBlock list:
            foreach (ListItem item in list.Items)
                foreach (ChangelogBlock child in item.Blocks) CollectBlock(child, links);
            break;
        case QuoteBlock quote:
            foreach (ChangelogBlock child in quote.Blocks) CollectBlock(child, links);
            break;
        case TableBlock table:
            foreach (TableRow row in new[] { table.Header }.Concat(table.Rows))
                foreach (IReadOnlyList<ChangelogInline> cell in row.Cells) CollectInlines(cell, links);
            break;
    }
}

static void CollectInlines(IReadOnlyList<ChangelogInline> inlines, List<LinkInline> links)
{
    foreach (ChangelogInline inline in inlines)
    {
        switch (inline)
        {
            case LinkInline link:
                links.Add(link);
                CollectInlines(link.Children, links);
                break;
            case StyleInline style: CollectInlines(style.Children, links); break;
        }
    }
}

static bool HasStyle(IReadOnlyList<ChangelogInline> inlines)
{
    foreach (ChangelogInline inline in inlines)
    {
        if (inline is StyleInline) return true;
        if (inline is LinkInline link && HasStyle(link.Children)) return true;
    }
    return false;
}

ChangelogDocument sample = ChangelogMarkdown.Parse("## 更新内容\n- 新增启动参数");
Check(sample.Blocks.Count == 2, "示例正文应只有标题和列表");
Check(sample.Blocks[0] is HeadingBlock { Level: 2 } sampleHeading && InlineText(sampleHeading.Inlines) == "更新内容", "二级标题文字不符");
Check(sample.Blocks[1] is ListBlock { Ordered: false, Items.Count: 1 } sampleList && ItemText(sampleList.Items[0]) == "新增启动参数", "无序列表文字不符");

ChangelogDocument headings = ChangelogMarkdown.Parse("# A\n## B\n### C");
Check(headings.Blocks.Count == 3
    && headings.Blocks[0] is HeadingBlock { Level: 1 }
    && headings.Blocks[1] is HeadingBlock { Level: 2 }
    && headings.Blocks[2] is HeadingBlock { Level: 3 }, "标题级别不符");

ChangelogDocument styled = ChangelogMarkdown.Parse("**b** *i* ~~s~~ `code`");
ParagraphBlock? styledParagraph = styled.Blocks.Count == 1 ? styled.Blocks[0] as ParagraphBlock : null;
Check(styledParagraph is not null, "强调应在同一段");
Check(styledParagraph!.Inlines.OfType<StyleInline>().Any(style => style.Bold && !style.Italic && InlineText(style.Children) == "b"), "粗体不符");
Check(styledParagraph.Inlines.OfType<StyleInline>().Any(style => style.Italic && !style.Bold && InlineText(style.Children) == "i"), "斜体不符");
Check(styledParagraph.Inlines.OfType<StyleInline>().Any(style => style.Strike && InlineText(style.Children) == "s"), "删除线不符");
Check(styledParagraph.Inlines.OfType<CodeSpanInline>().Any(code => code.Text == "code"), "行内代码不符");

ChangelogDocument nested = ChangelogMarkdown.Parse("- outer\n  - inner");
ListBlock? nestedList = nested.Blocks.Count == 1 ? nested.Blocks[0] as ListBlock : null;
Check(nestedList is { Items.Count: 1 } && nestedList.Items[0].Blocks.Any(block => block is ListBlock), "嵌套列表应留在列表项里");

ChangelogDocument tasks = ChangelogMarkdown.Parse("- [ ] a\n- [x] b");
ListBlock? taskList = tasks.Blocks.Count == 1 ? tasks.Blocks[0] as ListBlock : null;
Check(taskList is { Items: [ListItem { Checked: false } open, ListItem { Checked: true } done] }, "任务勾选状态不符");
Check(taskList is not null && ItemText(taskList.Items[0]) == "a" && ItemText(taskList.Items[1]) == "b"
    && !ItemText(taskList.Items[0]).Contains('☐') && !ItemText(taskList.Items[1]).Contains('☑'), "勾选符不应进正文");

ChangelogDocument numbered = ChangelogMarkdown.Parse("2. item");
Check(numbered.Blocks is [ListBlock { Ordered: true, Start: 2 }], "有序列表起始序号不符");

ChangelogDocument fenced = ChangelogMarkdown.Parse("```cs\n`**not bold**`\n```");
CodeBlock? fencedCode = fenced.Blocks.Count == 1 ? fenced.Blocks[0] as CodeBlock : null;
Check(fencedCode is { Language: "cs" } && fencedCode.Text.Contains("`**not bold**`"), "围栏代码应保留字面量");
Check(fencedCode is not null && !fencedCode.Text.Contains('\uE000'), "代码里的占位符必须换回原文");

ChangelogDocument bare = ChangelogMarkdown.Parse("https://example.com/a");
Check(Links(bare).Count == 1 && ChangelogMarkdown.TryGetSafeHttpUri(Links(bare)[0].Url, out Uri? bareUri) && bareUri!.Scheme == "https", "裸地址应成为 https 链接");
ChangelogDocument labeled = ChangelogMarkdown.Parse("[docs](https://example.com/a)");
Check(Links(labeled).Count == 1 && BlockText(labeled.Blocks[0]) == "docs", "带文字的链接不符");
ChangelogDocument mixedCase = ChangelogMarkdown.Parse("[x](HTTPS://Example.COM/a)");
Check(Links(mixedCase).Count == 1, "大写协议的 https 链接应允许");

string[] rejected =
[
    "javascript:alert(1)", "JavaScript:alert(1)", "file:///C:/Windows/notepad.exe", "data:text/html,hi",
    "ms-appx:///Assets/foo", "mailto:a@b.c", "/relative", "//example.com", "http://", "https://example.com/a\nb", "https://example.com/a\rb"
];
foreach (string raw in rejected)
{
    string shown = raw.Replace("\n", "\\n").Replace("\r", "\\r");
    Check(!ChangelogMarkdown.TryGetSafeHttpUri(raw, out _), "不安全地址未被拒绝: " + shown);
    // 换行会截断自动链接，截出来的前半段仍须通过同一套协议检查；其余地址不能产生链接。
    bool newline = raw.Contains('\n') || raw.Contains('\r');
    foreach (ChangelogDocument parsed in new[] { ChangelogMarkdown.Parse(raw), ChangelogMarkdown.Parse("[x](" + raw + ")") })
    {
        foreach (LinkInline link in Links(parsed))
            Check(ChangelogMarkdown.TryGetSafeHttpUri(link.Url, out _), "解析结果里出现了不安全链接: " + shown);
        if (!newline) Check(Links(parsed).Count == 0, "不安全链接仍被保留: " + shown);
    }
}

ChangelogDocument safeImage = ChangelogMarkdown.Parse("![pic](https://example.com/a.png)");
Check(AllText(safeImage).Contains("pic") && Links(safeImage).Count <= 1 && Links(safeImage).All(link => link.Url.StartsWith("https://", StringComparison.Ordinal)), "安全图片只保留文字和 https 链接");
ChangelogDocument unsafeImage = ChangelogMarkdown.Parse("![pic](javascript:alert(1))");
Check(AllText(unsafeImage) == "pic" && Links(unsafeImage).Count == 0 && !AllText(unsafeImage).Contains("javascript:", StringComparison.OrdinalIgnoreCase), "不安全图片不能留下脚本地址");

ChangelogDocument literalHtml = ChangelogMarkdown.Parse("hello <b>bold</b>");
ParagraphBlock? htmlParagraph = literalHtml.Blocks.Count == 1 ? literalHtml.Blocks[0] as ParagraphBlock : null;
Check(AllText(literalHtml) == "hello <b>bold</b>" && htmlParagraph is not null && !HasStyle(htmlParagraph.Inlines), "尖括号应保持字面量，不能变成粗体");
foreach (string html in new[] { "<a href=\"https://example.com\">x</a>", "<script>alert(1)</script>", "<img src=\"http://evil/x.png\">" })
{
    ChangelogDocument parsed = ChangelogMarkdown.Parse(html);
    Check(Links(parsed).Count == 0 && parsed.Blocks.All(block => block is not HeadingBlock), "HTML 不应变成链接或标题: " + html);
}
Check(ChangelogMarkdown.HtmlToText("<a href=\"javascript:alert(1)\">click</a>") == "click", "锚文本提取不符");
Check(ChangelogMarkdown.HtmlToText("<script>alert(1)</script><b>ok</b>") == "ok", "script 正文应被丢掉");
Check(ChangelogMarkdown.HtmlToText("<img src=\"http://evil/x.png\" alt=\"pic\">") == "", "HTML 图片不应留下地址或替代文字");

Check(ChangelogMarkdown.Parse(null).Blocks.Count == 0 && !ChangelogMarkdown.Parse(null).Truncated, "null 应是空文档");
Check(ChangelogMarkdown.Parse("").Blocks.Count == 0 && !ChangelogMarkdown.Parse("").Truncated, "空字符串应是空文档");
Check(ChangelogMarkdown.Parse("   ").Blocks.Count == 0 && !ChangelogMarkdown.Parse("   ").Truncated, "空白应是空文档");
ChangelogDocument plain = ChangelogMarkdown.Parse("hello");
Check(plain.Blocks is [ParagraphBlock] && plain.Blocks[0] is not HeadingBlock && BlockText(plain.Blocks[0]) == "hello", "纯文本应是一段");

ChangelogDocument broken = ChangelogMarkdown.Parse("hello\nworld");
ParagraphBlock? brokenParagraph = broken.Blocks.Count == 1 ? broken.Blocks[0] as ParagraphBlock : null;
Check(brokenParagraph is not null && brokenParagraph.Inlines.OfType<LineBreakInline>().Any() && InlineText(brokenParagraph.Inlines) == "hello\nworld", "段内换行应保留");
Check(ChangelogMarkdown.Parse("---").Blocks is [ThematicBreakBlock], "分隔线不应变成段落");

ChangelogDocument table = ChangelogMarkdown.Parse("| h | i |\n| --- | --- |\n| **c** | d |");
TableBlock? tableBlock = table.Blocks.Count == 1 ? table.Blocks[0] as TableBlock : null;
Check(tableBlock is { Header: var header, Rows: [var body] }
    && InlineText(header.Cells[0]) == "h" && InlineText(header.Cells[1]) == "i"
    && body.Cells[0] is [StyleInline { Bold: true }] && InlineText(body.Cells[0]) == "c" && InlineText(body.Cells[1]) == "d",
    "管道表格的表头、正文或单元格强调不符");

ChangelogDocument capped = ChangelogMarkdown.Parse(string.Join('\n', Enumerable.Repeat("## x", 501)));
Check(capped.Truncated && capped.Blocks.Count == 500, "超过 500 个顶层块应截断");

string huge = "https://example.com/a" + new string('a', ChangelogMarkdown.MaxInputLength);
ChangelogDocument oversized = ChangelogMarkdown.Parse(huge);
Check(oversized.Truncated && Links(oversized).Count == 0 && oversized.Blocks is [ParagraphBlock], "超长输入不应解析出链接");

Check(AllText(ChangelogMarkdown.Parse("List<T> and a < b")) == "List<T> and a < b", "比较号和泛型尖括号必须原样留下");
Check(ChangelogMarkdown.TryGetSafeHttpUri("https://example.com/a", out Uri? safe) && safe!.Scheme == "https", "安全地址的协议应为 https");

Console.WriteLine($"PASS: {checks} changelog markdown checks.");
