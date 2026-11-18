// 仅在 EnableUiVerification=true 时编译；在隔离的构建目录运行，不访问用户正常运行目录的数据。
// 用真实鼠标和像素检查选区罩层：不挡点击、松手后停止、行末能落点，并且字色和荧光笔还在。
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WorkTiles.Controls;
using WorkTiles.Models;
using WorkTiles.Services;
using Windows.Foundation;
using Windows.UI;

namespace WorkTiles;

public sealed partial class MainWindow
{
    internal void ScheduleSelectionVerification()
    {
        RootShell.Loaded += async (_, _) =>
        {
            string output = Path.Combine(AppContext.BaseDirectory, "verification");
            Directory.CreateDirectory(output);
            List<string> evidence = [];
            try
            {
                List<string> failures = [];
                void Check(bool condition, string message)
                {
                    if (!condition) { failures.Add("FAIL: " + message); return; }
                    evidence.Add("PASS: " + message);
                }
                await NextLayoutAsync();
                await VerifySelectionAsync(output, evidence, Check);
                evidence.AddRange(failures);
                if (failures.Count > 0) throw new Exception($"{failures.Count} selection checks failed");
                evidence.Add("SELECTION_VERIFICATION_OK");
            }
            catch (Exception ex) { evidence.Add("SELECTION_VERIFICATION_FAILED\n" + ex); }
            finally
            {
                File.WriteAllLines(Path.Combine(output, "selection-result.txt"), evidence);
                Close();
            }
        };
    }

    private async Task VerifySelectionAsync(string output, List<string> evidence, Action<bool, string> check)
    {
        _settings.AutoUpdateEnabled = false;
        _settings.Autofill.Subject.Enabled = false;
        _settings.Autofill.Homework.Enabled = false;
        _library = new ProjectLibrary { Settings = _settings };
        ProjectDocument project = ProjectStore.Create(_library, false);
        project.Name = "选区探针";
        ViewModel.ReplaceSubjects([]);
        SubjectBoard subject = ViewModel.AddSubject("数学");
        subject.X = 48;
        subject.Y = 48;
        subject.TileWidth = 520;
        subject.TileHeight = 280;
        subject.Entries.Add(new HomeworkEntry
        {
            Content = "红字黄底选区还要更长一些\n第二行短\n第三行短\n" + string.Join('\n', Enumerable.Range(1, 18).Select(index => $"填充行{index}"))
        });
        ShowBoard();
        SetBoardZoom(1);
        BuildTiles();
        EnterEditing();
        await NextLayoutAsync();

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Windows.Graphics.SizeInt32 size = VerificationWindowSize();
        _appWindow?.MoveAndResize(new Windows.Graphics.RectInt32(0, 0, size.Width, size.Height));
        await NextLayoutAsync();
        SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        ForceForeground(hwnd);
        await Task.Delay(300);
        double scale = RootShell.XamlRoot.RasterizationScale;
        SubjectTileControl tile = BoardCanvas.Children.OfType<SubjectTileControl>().Single();
        RichEditBox editor = FindVisuals<RichEditBox>(tile).First(box => !TransparentInputMarkers.IsOverlayMirror(box));
        editor.Focus(FocusState.Programmatic);
        await NextLayoutAsync();

        Color red = Color.FromArgb(255, 220, 38, 38);
        Color yellow = Color.FromArgb(255, 250, 204, 21);
        ITextRange colored = editor.Document.GetRange(0, 4);
        colored.CharacterFormat.ForegroundColor = red;
        colored.CharacterFormat.BackgroundColor = yellow;
        editor.Document.Selection.SetRange(0, 4);
        await NextLayoutAsync();
        await NextLayoutAsync();
        editor.Document.GetRange(0, 1).GetRect(PointOptions.ClientCoordinates, out Rect glyph0, out _);
        editor.Document.GetRange(3, 4).GetRect(PointOptions.ClientCoordinates, out Rect glyph3, out _);

        FrameworkElement? host = TransparentInputMarkers.OverlayHost(editor);
        evidence.Add($"host vis={host?.Visibility} size={host?.ActualWidth:0.#}x{host?.ActualHeight:0.#} editor={editor.ActualWidth:0.#}x{editor.ActualHeight:0.#} focus={editor.FocusState} sel={editor.Document.Selection.StartPosition}..{editor.Document.Selection.EndPosition}");
        check(host is { Visibility: Visibility.Visible }, "a selection shows the overlay");
        if (host is not { Visibility: Visibility.Visible }) return;
        (int warm, int white, int opaque) pixels;
        try { pixels = await SampleAsync(host); }
        catch (Exception ex)
        {
            evidence.Add("sample failed: " + ex.Message);
            check(false, "the overlay can be sampled");
            return;
        }
        evidence.Add($"selection pixels warm={pixels.warm} white={pixels.white} opaque={pixels.opaque}");
        check(pixels.warm > 8 && pixels.white * 2 < Math.Max(1, pixels.opaque), "selected text keeps its color and highlight under the veil");
        Border? band = FindVisuals<Border>(host).FirstOrDefault(border =>
            border.Background is SolidColorBrush brush && brush.Color.A == 104 && brush.Color.R == 186 && border.Width > 1);
        if (band is not null)
        {
            double bandLeft = Canvas.GetLeft(band);
            double expectedLeft = glyph0.X + editor.Padding.Left;
            double expectedRight = glyph3.Right + editor.Padding.Left;
            evidence.Add($"band {bandLeft:0.#}+{band.Width:0.#} glyphs {expectedLeft:0.#}..{expectedRight:0.#}");
            check(Math.Abs(bandLeft - expectedLeft) < 1.5, "the selection starts at the first glyph");
            check(Math.Abs(bandLeft + band.Width - expectedRight) < 1.5, "the selection ends at the last glyph");
        }

        editor.Document.GetRange(6, 7).GetRect(PointOptions.ClientCoordinates, out Rect outside, out _);
        (int _, int _, int outsideOpaque) = await SampleRegionAsync(host, outside);
        evidence.Add($"unselected overlay opaque={outsideOpaque}");
        check(outsideOpaque < 12, "the veil does not cover unselected text");

        Point Screen(FrameworkElement element, double x, double y)
        {
            Point client = element.TransformToVisual(null).TransformPoint(new Point(x, y));
            POINT origin = new();
            ClientToScreen(hwnd, ref origin);
            return new Point(origin.X + client.X * scale, origin.Y + client.Y * scale);
        }
        async Task Click(Point screen)
        {
            MoveMouse(screen);
            await Task.Delay(40);
            SendMouse(MouseLeftDown);
            await Task.Delay(40);
            SendMouse(MouseLeftUp);
            await NextLayoutAsync();
        }
        async Task<long> Drag(Point from, Point to)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long waits = 0;
            MoveMouse(from);
            await Task.Delay(40);
            waits += 40;
            SendMouse(MouseLeftDown);
            await Task.Delay(40);
            waits += 40;
            for (int step = 1; step <= 12; step++)
            {
                MoveMouse(new Point(from.X + (to.X - from.X) * step / 12, from.Y + (to.Y - from.Y) * step / 12));
                await Task.Delay(16);
                waits += 16;
            }
            SendMouse(MouseLeftUp);
            watch.Stop();
            await NextLayoutAsync();
            return watch.ElapsedMilliseconds - waits;
        }
        static int SelectionLength(RichEditBox box) =>
            Math.Abs(box.Document.Selection.EndPosition - box.Document.Selection.StartPosition);

        editor.Document.GetRange(0, 1).GetRect(PointOptions.ClientCoordinates, out Rect first, out _);
        editor.Document.GetRange(4, 5).GetRect(PointOptions.ClientCoordinates, out Rect fifth, out _);
        double entryTop = editor.TransformToVisual(tile).TransformPoint(new Point(0, 0)).Y;
        double entryHeight = editor.ActualHeight;
        long dragCost = await Drag(
            Screen(editor, first.X + 2, first.Y + first.Height / 2),
            Screen(editor, fifth.Right, fifth.Y + fifth.Height / 2));
        evidence.Add($"drag cost {dragCost}ms");
        int dragged = SelectionLength(editor);
        check(dragged > 1, "dragging creates a selection");
        check(dragCost < 2000, "dragging a selection stays within the time budget");
        MoveMouse(Screen(editor, fifth.Right + 70, fifth.Y + fifth.Height / 2));
        await Task.Delay(80);
        await NextLayoutAsync();
        check(SelectionLength(editor) == dragged, "the selection stops growing after the button is released");
        double entryTopAfter = editor.TransformToVisual(tile).TransformPoint(new Point(0, 0)).Y;
        evidence.Add($"entry top {entryTop:0.#}->{entryTopAfter:0.#} height {entryHeight:0.#}->{editor.ActualHeight:0.#}");
        check(Math.Abs(entryTopAfter - entryTop) < 1 && Math.Abs(editor.ActualHeight - entryHeight) < 1,
            "dragging the selection does not move or resize the entry");

        editor.Document.GetText(TextGetOptions.None, out string plain);
        int second = plain.IndexOf("第二行短", StringComparison.Ordinal);
        int third = plain.IndexOf("第三行短", StringComparison.Ordinal);
        check(second > 0 && third > second, "the sample has a second line to click");
        if (second > 0 && third > second)
        {
            ITextRange secondLine = editor.Document.GetRange(second, second);
            secondLine.Expand(TextRangeUnit.Line);
            secondLine.GetRect(PointOptions.ClientCoordinates, out Rect lineRect, out _);
            await Click(Screen(editor, Math.Min(editor.ActualWidth - 8, lineRect.Right + 36), lineRect.Y + lineRect.Height / 2));
            int caret = editor.Document.Selection.StartPosition;
            evidence.Add($"line-end caret {caret} line {secondLine.StartPosition}..{secondLine.EndPosition} third {third}");
            check(editor.Document.Selection.EndPosition == caret, "clicking past the line leaves a caret");
            check(caret >= secondLine.StartPosition && caret < third, "clicking past a line puts the caret on that line");
        }

        await Click(Screen(tile, tile.ActualWidth / 2, tile.ActualHeight - 24));
        check(SelectionLength(editor) == 0, "clicking empty tile space clears the selection");
        check(TransparentInputMarkers.OverlayHost(editor)?.Visibility != Visibility.Visible, "clearing the selection hides the overlay");

        editor.Focus(FocusState.Programmatic);
        editor.Document.Selection.SetRange(0, 4);
        await NextLayoutAsync();
        host = TransparentInputMarkers.OverlayHost(editor);
        ScrollViewer? scroller = FindVisuals<ScrollViewer>(tile).FirstOrDefault(viewer => viewer.ScrollableHeight > 20);
        if (scroller is not null && host is not null)
        {
            scroller.ChangeView(null, 60, null, true);
            await NextLayoutAsync();
            Point editorAt = editor.TransformToVisual(RootShell).TransformPoint(new Point(0, 0));
            Point hostAt = host.TransformToVisual(RootShell).TransformPoint(new Point(0, 0));
            evidence.Add($"scrolled editor {editorAt.X:0.#},{editorAt.Y:0.#} host {hostAt.X:0.#},{hostAt.Y:0.#}");
            check(Math.Abs(editorAt.X - hostAt.X) < 1.5 && Math.Abs(editorAt.Y - hostAt.Y) < 1.5, "the overlay stays on the editor while the list scrolls");
            check(Math.Abs(editor.ActualWidth - host.ActualWidth) < 1.5 && Math.Abs(editor.ActualHeight - host.ActualHeight) < 1.5,
                "the overlay matches the editor size");
        }
        else
        {
            check(false, "the homework list can scroll");
        }

        SetBoardZoom(1.25);
        await NextLayoutAsync();
        host = TransparentInputMarkers.OverlayHost(editor);
        if (host is not null)
        {
            Point editorAt = editor.TransformToVisual(RootShell).TransformPoint(new Point(0, 0));
            Point hostAt = host.TransformToVisual(RootShell).TransformPoint(new Point(0, 0));
            evidence.Add($"zoomed editor {editorAt.X:0.#},{editorAt.Y:0.#} {editor.ActualWidth:0.#}x{editor.ActualHeight:0.#} host {hostAt.X:0.#},{hostAt.Y:0.#} {host.ActualWidth:0.#}x{host.ActualHeight:0.#}");
            check(Math.Abs(editorAt.X - hostAt.X) < 1.5 && Math.Abs(editorAt.Y - hostAt.Y) < 1.5, "the overlay stays on the editor when the board is zoomed");
            check(Math.Abs(editor.ActualWidth - host.ActualWidth) < 1.5 && Math.Abs(editor.ActualHeight - host.ActualHeight) < 1.5,
                "the overlay matches the editor size when the board is zoomed");
        }

        SetBoardZoom(1);
        await NextLayoutAsync();
        editor.Focus(FocusState.Programmatic);
        await NextLayoutAsync();
        editor.Document.Selection.SetRange(0, 4);
        await NextLayoutAsync();
        NumberBox? sizeBox = FindVisuals<NumberBox>(RichTextToolbarHost).FirstOrDefault();
        check(sizeBox is not null, "the formatting toolbar is available");
        if (sizeBox is not null)
        {
            sizeBox.Focus(FocusState.Programmatic);
            sizeBox.Value = 28;
            await NextLayoutAsync();
            evidence.Add($"after font size focus={editor.FocusState} selection={editor.Document.Selection.StartPosition}..{editor.Document.Selection.EndPosition}");
            check(editor.FocusState == FocusState.Unfocused, "the font size box takes focus");
            check(TransparentInputMarkers.OverlayHost(editor)?.Visibility == Visibility.Visible, "the overlay stays while the formatting toolbar is active");
            check(editor.Document.Selection.StartPosition == 0 && editor.Document.Selection.EndPosition == 4, "changing the font size keeps the selection");
        }

        ExportTileVisual export = new(subject, Color.FromArgb(255, 32, 36, 48), project.Palette, _settings);
        ExportOverlay.Visibility = Visibility.Visible;
        ExportOverlay.Children.Add(export);
        await export.PrepareAsync();
        await NextLayoutAsync();
        check(FindVisuals<RichEditBox>(export).All(box => TransparentInputMarkers.OverlayHost(box) is null), "export editors do not grow a selection overlay");
        await SaveVisualAsync(tile, Path.Combine(output, "selection-tile.png"), (int)tile.ActualWidth, (int)tile.ActualHeight);
        ExportOverlay.Children.Clear();
        ExportOverlay.Visibility = Visibility.Collapsed;
    }

    private static async Task<(int Warm, int White, int Opaque)> SampleAsync(UIElement element)
    {
        RenderTargetBitmap bitmap = new();
        await bitmap.RenderAsync(element);
        byte[] data = (await bitmap.GetPixelsAsync()).ToArray();
        int warm = 0, white = 0, opaque = 0;
        for (int index = 0; index + 3 < data.Length; index += 16)
        {
            byte blue = data[index], green = data[index + 1], red = data[index + 2], alpha = data[index + 3];
            if (alpha < 24) continue;
            opaque++;
            if (red > 230 && green > 230 && blue > 230) white++;
            if (red > 120 && red > blue + 25 && green > blue) warm++;
        }
        return (warm, white, opaque);
    }

    private static async Task<(int Warm, int White, int Opaque)> SampleRegionAsync(FrameworkElement element, Rect region)
    {
        RenderTargetBitmap bitmap = new();
        await bitmap.RenderAsync(element);
        byte[] data = (await bitmap.GetPixelsAsync()).ToArray();
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        if (width < 2 || height < 2 || element.ActualWidth < 1 || element.ActualHeight < 1) return (0, 0, 0);
        double scaleX = width / element.ActualWidth;
        double scaleY = height / element.ActualHeight;
        int x = (int)Math.Clamp(region.X * scaleX + 1, 0, width - 1);
        int y = (int)Math.Clamp((region.Y + region.Height / 2) * scaleY, 0, height - 1);
        int opaque = 0;
        for (int dx = 0; dx < 4; dx++)
        {
            int pixel = (y * width + Math.Min(width - 1, x + dx)) * 4;
            if (pixel + 3 >= data.Length) continue;
            if (data[pixel + 3] >= 24) opaque++;
        }
        return (0, 0, opaque);
    }
}
