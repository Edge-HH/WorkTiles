using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;
using WorkTiles.Services;
using WorkTiles.Shared.Markdown;

namespace WorkTiles.Controls;

/// <summary>
/// 把更新日志文档画成可选中的原生文本。画笔按弹窗主题从主题字典取出，
/// 不走 Application.RequestedTheme，否则深色文字会铺到浅色对话框上。
/// </summary>
public sealed class ChangelogView : StackPanel
{
    private static readonly double[] FullHeadingSizes = [20, 17, 15, 14, 13, 13];
    private static readonly double[] CompactHeadingSizes = [16, 14, 13, 12, 12, 12];
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private readonly bool _compact;
    private readonly Brush _text;
    private readonly Brush _secondary;
    private readonly Brush _muted;
    private readonly Brush _brand;
    private readonly Brush _surface;
    private readonly Brush _line;
    private Panel _panel;
    private RichTextBlock? _flow;

    public ChangelogView(string? markdown, bool compact, ElementTheme theme)
    {
        _compact = compact;
        bool light = theme == ElementTheme.Light;
        _text = Themed("BoardTextBrush", light, light ? Rgb(0, 0, 0) : Rgb(0xF0, 0xF0, 0xF5));
        _secondary = Themed("BoardTextSecondaryBrush", light, light ? Rgb(0x55, 0x55, 0x68) : Rgb(0xA4, 0xA4, 0xB4));
        _muted = Themed("BoardTextMutedBrush", light, light ? Rgb(0x74, 0x74, 0x86) : Rgb(0x73, 0x73, 0x86));
        _brand = Themed("BoardBrandBrush", light, light ? Rgb(0x54, 0x57, 0xDB) : Rgb(0x6D, 0x70, 0xF6));
        _surface = Themed("BoardSurfaceSecondaryBrush", light, light ? Rgb(0xFF, 0xFF, 0xFF) : Rgb(0x1E, 0x1E, 0x28));
        _line = Themed("BoardLineBrush", light, light ? Rgb(0xD9, 0xD9, 0xE6) : Rgb(0x2A, 0x2A, 0x35));
        _panel = this;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        if (theme is ElementTheme.Light or ElementTheme.Dark) RequestedTheme = theme;

        ChangelogDocument document = ChangelogMarkdown.Parse(markdown);
        foreach (ChangelogBlock block in document.Blocks) AddBlock(block, 0, _text);
        Flush();
        if (!document.Truncated) return;
        Children.Add(new TextBlock
        {
            Text = "其余内容已省略。",
            Foreground = _muted,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0)
        });
    }

    private void AddBlock(ChangelogBlock block, int depth, Brush foreground)
    {
        switch (block)
        {
            case HeadingBlock heading: AddHeading(heading, depth, foreground); break;
            case ParagraphBlock paragraph: AddParagraph(null, depth, paragraph.Inlines, foreground); break;
            case ListBlock list: AddList(list, depth, foreground); break;
            case QuoteBlock quote: AddQuote(quote); break;
            case CodeBlock code: AddCode(code, foreground); break;
            case ThematicBreakBlock: AddRule(); break;
            case TableBlock table: AddTable(table); break;
        }
    }

    private void AddList(ListBlock list, int depth, Brush foreground)
    {
        int number = list.Start;
        foreach (ListItem item in list.Items)
        {
            string marker = item.Checked switch
            {
                true => "☑ ",
                false => "☐ ",
                _ => list.Ordered ? $"{number}. " : "• "
            };
            if (list.Ordered && item.Checked is null) number++;
            bool marked = false;
            if (item.Blocks.Count == 0) AddParagraph(marker, depth, [], foreground);
            foreach (ChangelogBlock child in item.Blocks)
            {
                if (!marked && child is ParagraphBlock paragraph)
                {
                    AddParagraph(marker, depth, paragraph.Inlines, foreground);
                    marked = true;
                    continue;
                }
                if (!marked && child is HeadingBlock heading)
                {
                    AddHeading(heading, depth, foreground, marker);
                    marked = true;
                    continue;
                }
                if (!marked)
                {
                    AddParagraph(marker, depth, [], foreground);
                    marked = true;
                }
                AddBlock(child, child is ListBlock ? depth + 1 : depth, foreground);
            }
        }
    }

    private void AddHeading(HeadingBlock heading, int depth, Brush foreground, string? marker = null)
    {
        int level = Math.Clamp(heading.Level, 1, 6);
        Paragraph paragraph = new()
        {
            FontFamily = FontService.BundledFamilyForWeight(level <= 2 ? "Bold" : "Medium"),
            FontSize = (_compact ? CompactHeadingSizes : FullHeadingSizes)[level - 1],
            Margin = new Thickness(18 * depth, 0, 0, 4)
        };
        if (marker is not null) paragraph.Inlines.Add(new Run { Text = marker, Foreground = _secondary });
        AddInlines(paragraph.Inlines, heading.Inlines, bold: false, italic: false, strike: false, foreground);
        Flow(foreground).Blocks.Add(paragraph);
    }

    private void AddParagraph(string? marker, int depth, IReadOnlyList<ChangelogInline> inlines, Brush foreground)
    {
        Paragraph paragraph = new() { Margin = new Thickness(18 * depth, 0, 0, 4) };
        if (marker is not null) paragraph.Inlines.Add(new Run { Text = marker, Foreground = _secondary });
        AddInlines(paragraph.Inlines, inlines, bold: false, italic: false, strike: false, foreground);
        Flow(foreground).Blocks.Add(paragraph);
    }

    private void AddQuote(QuoteBlock quote)
    {
        Flush();
        StackPanel inner = new() { Spacing = 0 };
        Border border = new()
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = _brand,
            Padding = new Thickness(12, 0, 0, 0),
            Margin = new Thickness(0, 4, 0, 8),
            Child = inner
        };
        Panel previous = _panel;
        _panel = inner;
        foreach (ChangelogBlock child in quote.Blocks) AddBlock(child, 0, _secondary);
        Flush();
        _panel = previous;
        _panel.Children.Add(border);
    }

    private void AddCode(CodeBlock code, Brush foreground)
    {
        Flush();
        TextBlock text = new()
        {
            Text = code.Text,
            FontFamily = Mono,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
            FontSize = _compact ? 12 : 13,
            Foreground = foreground
        };
        ScrollViewer scroller = new()
        {
            Content = text,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _panel.Children.Add(new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Background = _surface,
            BorderBrush = _line,
            Margin = new Thickness(0, 4, 0, 8),
            Child = scroller
        });
    }

    private void AddRule()
    {
        Flush();
        _panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 8, 0, 8),
            Background = _line
        });
    }

    private void AddTable(TableBlock table)
    {
        int columns = table.Header.Cells.Count;
        foreach (TableRow row in table.Rows) columns = Math.Max(columns, row.Cells.Count);
        if (columns == 0) return;
        Flush();
        Grid grid = new();
        for (int column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddTableRow(grid, table.Header, 0, columns, header: true);
        for (int index = 0; index < table.Rows.Count; index++)
            AddTableRow(grid, table.Rows[index], index + 1, columns, header: false);
        _panel.Children.Add(new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 8)
        });
    }

    private void AddTableRow(Grid grid, TableRow row, int rowIndex, int columns, bool header)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int column = 0; column < columns; column++)
        {
            Paragraph paragraph = new();
            if (header) paragraph.FontFamily = FontService.BundledFamilyForWeight("Medium");
            if (column < row.Cells.Count)
                AddInlines(paragraph.Inlines, row.Cells[column], bold: false, italic: false, strike: false, _text);
            RichTextBlock text = new()
            {
                IsTextSelectionEnabled = true,
                FontFamily = FontService.DefaultFamily,
                Foreground = _text,
                FontSize = _compact ? 12 : 14,
                TextWrapping = TextWrapping.Wrap,
                Blocks = { paragraph }
            };
            Border cell = new()
            {
                Padding = new Thickness(8, 4, 8, 4),
                BorderBrush = _line,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = text
            };
            Grid.SetRow(cell, rowIndex);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
    }

    private void AddInlines(InlineCollection target, IReadOnlyList<ChangelogInline> inlines, bool bold, bool italic, bool strike, Brush foreground)
    {
        foreach (ChangelogInline inline in inlines)
        {
            switch (inline)
            {
                case TextInline text:
                    target.Add(MakeRun(text.Text, bold, italic, strike, foreground));
                    break;
                case CodeSpanInline code:
                    target.Add(new Run { Text = code.Text, FontFamily = Mono, Foreground = _brand });
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case LinkInline link:
                    string url = link.Url;
                    Hyperlink hyperlink = new() { Foreground = _brand };
                    hyperlink.Click += (_, _) => Open(url);
                    AddInlines(hyperlink.Inlines, link.Children, bold, italic, strike, _brand);
                    target.Add(hyperlink);
                    break;
                case StyleInline style:
                    AddInlines(target, style.Children, bold || style.Bold, italic || style.Italic, strike || style.Strike, foreground);
                    break;
            }
        }
    }

    private static Run MakeRun(string text, bool bold, bool italic, bool strike, Brush foreground)
    {
        Run run = new() { Text = text, Foreground = foreground };
        // 内置 HarmonyOS 每个字重是单独的 TTF，只设 FontWeight 不会换成粗体文件。
        if (bold) run.FontFamily = FontService.BundledFamilyForWeight("Bold");
        if (italic) run.FontStyle = FontStyle.Italic;
        if (strike) run.TextDecorations = TextDecorations.Strikethrough;
        return run;
    }

    private RichTextBlock Flow(Brush foreground)
    {
        if (_flow is not null) return _flow;
        _flow = new RichTextBlock
        {
            FontFamily = FontService.DefaultFamily,
            IsTextSelectionEnabled = true,
            Foreground = foreground,
            FontSize = _compact ? 12 : 14,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        return _flow;
    }

    private void Flush()
    {
        if (_flow is null) return;
        _panel.Children.Add(_flow);
        _flow = null;
    }

    // WinUI 3 桌面不会因为 NavigateUri 打开浏览器；Click 里再检查一次协议。
    private static async void Open(string url)
    {
        if (!ChangelogMarkdown.TryGetSafeHttpUri(url, out Uri? uri) || uri is null) return;
        try { await Windows.System.Launcher.LaunchUriAsync(uri); }
        catch (Exception) { }
    }

    private static Color Rgb(byte red, byte green, byte blue) => Color.FromArgb(255, red, green, blue);

    private static Brush Themed(string key, bool light, Color fallback) =>
        Application.Current is null ? new SolidColorBrush(fallback)
        : Lookup(Application.Current.Resources, key, light ? "Light" : "Dark") ?? new SolidColorBrush(fallback);

    private static Brush? Lookup(ResourceDictionary dictionary, string key, string themeKey)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out object? value)
            && value is ResourceDictionary themes
            && themes.TryGetValue(key, out object? entry)
            && entry is Brush brush)
            return brush;
        foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
            if (Lookup(merged, key, themeKey) is { } found) return found;
        return null;
    }
}
