using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Qingjian;

public partial class MainWindow
{
    private enum CanvasDragKind { None, MoveVideo, ScaleVideo, MoveTitle, CropLeft, CropTop, CropRight, CropBottom }

    private bool cropMode;
    private CanvasDragKind canvasDrag = CanvasDragKind.None;
    private Point canvasDragOrigin;
    private TransformSpec? dragStartTransform;
    private CropMargins? dragStartCrop;
    private TextStyle? dragStartTextStyle;
    private TransformSpec? liveTransformDraft;
    private CropMargins? liveCropDraft;
    private TextStyle? liveTextStyleDraft;
    private bool snapCenterX, snapCenterY;
    private Rect selectedTitleLogicalRect = Rect.Empty;
    private int activeOverlayIndex = -1;
    private string? overlaySourcePath;
    private bool overlayPlayerReady;
    private double pendingOverlayLocal;

    private (double W, double H) LogicalCanvasSize => project.Format switch
    {
        "9:16" => (1080, 1920),
        "1:1" => (1080, 1080),
        _ => (1920, 1080)
    };

    private void UpdateCompositeStageGeometry()
    {
        var (lw, lh) = LogicalCanvasSize;
        CompositeStage.Width = lw;
        CompositeStage.Height = lh;
        RefreshLiveCanvas();
    }

    private void RefreshLiveCanvas()
    {
        if (!ready) return;
        var (lw, lh) = LogicalCanvasSize;
        bool baked = UsingRender;

        // 1. V1 Main Video live transform, crop & track visibility
        bool v1Hidden = project.GetTrack("V1").Hidden;
        MainVideoLayer.Visibility = (!baked && v1Hidden) ? Visibility.Hidden : Visibility.Visible;
        if (!baked && activeClip >= 0 && activeClip < project.Clips.Length)
        {
            var clip = project.Clips[activeClip];
            bool isSel = selection == new TimelineSelection(SelectionKind.MainClip, activeClip);
            var tr = isSel && liveTransformDraft is not null ? liveTransformDraft : clip.EffectiveTransform;
            var crop = isSel && liveCropDraft is not null ? liveCropDraft : clip.Crop;
            ApplyLayerTransform(MainVideoLayer, MainVideoScale, MainVideoRotate, MainVideoTranslate, tr, crop, lw, lh);
        }
        else
        {
            ApplyLayerTransform(MainVideoLayer, MainVideoScale, MainVideoRotate, MainVideoTranslate, TransformSpec.Default, null, lw, lh);
        }

        // 2. V2 PiP Overlay live transform, crop & visibility
        bool v2Hidden = project.GetTrack("V2").Hidden;
        if (!baked && !v2Hidden && activeOverlayIndex >= 0 && activeOverlayIndex < project.OverlayTrack.Length)
        {
            var ov = project.OverlayTrack[activeOverlayIndex];
            bool isSel = selection == new TimelineSelection(SelectionKind.Overlay, activeOverlayIndex);
            var tr = isSel && liveTransformDraft is not null ? liveTransformDraft : ov.EffectiveTransform;
            var crop = isSel && liveCropDraft is not null ? liveCropDraft : ov.Crop;
            OverlayVideoLayer.Visibility = Visibility.Visible;
            ApplyLayerTransform(OverlayVideoLayer, OverlayVideoScale, OverlayVideoRotate, OverlayVideoTranslate, tr, crop, lw, lh);
        }
        else
        {
            OverlayVideoLayer.Visibility = Visibility.Collapsed;
        }

        // 3. T1 Live Styled Titles
        LiveTitleCanvas.Children.Clear();
        LiveTitleCanvas.Width = lw;
        LiveTitleCanvas.Height = lh;
        selectedTitleLogicalRect = Rect.Empty;
        bool t1Hidden = project.GetTrack("T1").Hidden;
        if (!baked && !t1Hidden)
        {
            for (int i = 0; i < project.TitleTrack.Length; i++)
            {
                var title = project.TitleTrack[i];
                if (head < title.Start - 0.0001 || head >= title.End + 0.0001) continue;
                bool isSel = selection == new TimelineSelection(SelectionKind.Title, i);
                var st = isSel && liveTextStyleDraft is not null ? liveTextStyleDraft : title.EffectiveStyle;
                string text = isSel && InspectorTitleScroll.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(TitleTextBox.Text)
                    ? TitleTextBox.Text : title.Text;
                var placed = AddLiveTitleElement(text, st, lw, lh);
                if (isSel) selectedTitleLogicalRect = placed;
            }
        }

        // 4. Screen-space Interactive Adorner (Selection Box / Corner Handles / Snap Guides / Crop Mask)
        RenderCanvasAdorner();
    }

    private static void ApplyLayerTransform(Grid layer, ScaleTransform scale, RotateTransform rotate,
        TranslateTransform translate, TransformSpec tr, CropMargins? crop, double lw, double lh)
    {
        scale.ScaleX = tr.Scale;
        scale.ScaleY = tr.Scale;
        rotate.Angle = tr.Rotation;
        translate.X = tr.X * lw;
        translate.Y = tr.Y * lh;
        layer.Opacity = Math.Clamp(tr.Opacity, 0, 1);

        if (crop is { IsZero: false })
        {
            double cl = lw * crop.Left / 100.0;
            double ct = lh * crop.Top / 100.0;
            double cw = Math.Max(1, lw * (100 - crop.Left - crop.Right) / 100.0);
            double ch = Math.Max(1, lh * (100 - crop.Top - crop.Bottom) / 100.0);
            layer.Clip = new RectangleGeometry(new Rect(cl, ct, cw, ch));
        }
        else
        {
            layer.Clip = null;
        }
    }

    private Rect AddLiveTitleElement(string text, TextStyle st, double lw, double lh)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily(st.FontFamily),
            FontSize = st.FontSize * (Math.Min(lw, lh) / 1080.0),
            FontWeight = st.FontSize >= 56 ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = ParseBrush(st.ColorHex, Brushes.White),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = lw * 0.88
        };
        if (st.Outline && st.OutlineWidth > 0)
        {
            tb.Effect = new DropShadowEffect
            {
                Color = ParseColor(st.OutlineColorHex, Colors.Black),
                ShadowDepth = 0,
                BlurRadius = Math.Max(2, st.OutlineWidth * 2.4),
                Opacity = 0.96
            };
        }

        FrameworkElement visual = tb;
        if (st.BackgroundBox)
        {
            visual = new Border
            {
                Background = ParseBrush(st.BackgroundHex, new SolidColorBrush(Color.FromArgb(0xB8, 0, 0, 0))),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(24, 10, 24, 10),
                Child = tb
            };
        }

        visual.Measure(new Size(lw, lh));
        Size sz = visual.DesiredSize;
        double cx = lw * (0.5 + st.X);
        double cy = lh * (0.5 + st.Y);
        double left = cx - sz.Width / 2;
        double top = cy - sz.Height / 2;
        Canvas.SetLeft(visual, left);
        Canvas.SetTop(visual, top);
        LiveTitleCanvas.Children.Add(visual);
        return new Rect(left, top, sz.Width, sz.Height);
    }

    private Rect GetActiveVisualScreenRect(out bool editable)
    {
        editable = false;
        double pw = Picture.ActualWidth, ph = Picture.ActualHeight;
        if (pw < 10 || ph < 10 || UsingRender) return Rect.Empty;

        if (selection.Kind == SelectionKind.MainClip && selection.Index == activeClip && activeClip >= 0)
        {
            var tr = liveTransformDraft ?? project.Clips[activeClip].EffectiveTransform;
            editable = !project.GetTrack("V1").Locked && !project.GetTrack("V1").Hidden;
            double w = pw * tr.Scale, h = ph * tr.Scale;
            return new Rect(pw * (0.5 + tr.X) - w / 2, ph * (0.5 + tr.Y) - h / 2, w, h);
        }
        if (selection.Kind == SelectionKind.Overlay && selection.Index == activeOverlayIndex && activeOverlayIndex >= 0)
        {
            var tr = liveTransformDraft ?? project.OverlayTrack[activeOverlayIndex].EffectiveTransform;
            editable = !project.GetTrack("V2").Locked && !project.GetTrack("V2").Hidden;
            double w = pw * tr.Scale, h = ph * tr.Scale;
            return new Rect(pw * (0.5 + tr.X) - w / 2, ph * (0.5 + tr.Y) - h / 2, w, h);
        }
        if (selection.Kind == SelectionKind.Title && selection.Index >= 0 && selection.Index < project.TitleTrack.Length)
        {
            var title = project.TitleTrack[selection.Index];
            if (head >= title.Start - 0.0001 && head <= title.End + 0.0001 && !project.GetTrack("T1").Hidden)
            {
                editable = !project.GetTrack("T1").Locked;
                var (lw, lh) = LogicalCanvasSize;
                if (!selectedTitleLogicalRect.IsEmpty && lw > 0 && lh > 0)
                {
                    double sx = pw / lw, sy = ph / lh;
                    return new Rect(
                        selectedTitleLogicalRect.X * sx - 6,
                        selectedTitleLogicalRect.Y * sy - 5,
                        selectedTitleLogicalRect.Width * sx + 12,
                        selectedTitleLogicalRect.Height * sy + 10);
                }
                var st = liveTextStyleDraft ?? title.EffectiveStyle;
                double w = Math.Clamp(pw * 0.42, 110, pw * 0.85);
                double h = Math.Clamp(ph * 0.14, 34, ph * 0.4);
                return new Rect(pw * (0.5 + st.X) - w / 2, ph * (0.5 + st.Y) - h / 2, w, h);
            }
        }
        return Rect.Empty;
    }

    private void RenderCanvasAdorner()
    {
        CanvasAdorner.Children.Clear();
        double pw = Picture.ActualWidth, ph = Picture.ActualHeight;
        if (pw < 10 || ph < 10) return;

        bool canCrop = selection.Kind is SelectionKind.MainClip or SelectionKind.Overlay &&
            ((selection.Kind == SelectionKind.MainClip && selection.Index == activeClip) ||
             (selection.Kind == SelectionKind.Overlay && selection.Index == activeOverlayIndex));

        if (cropMode && !canCrop)
        {
            cropMode = false;
            CropModeToggle.IsChecked = false;
            CropBanner.Visibility = Visibility.Collapsed;
        }
        CropModeToggle.Visibility = canCrop && !UsingRender ? Visibility.Visible : Visibility.Collapsed;
        CropBanner.Visibility = cropMode ? Visibility.Visible : Visibility.Collapsed;

        // Center Alignment Snap Guides during Drag
        var accent = (Brush)FindResource("Accent");
        if (snapCenterX)
            CanvasAdorner.Children.Add(new Line { X1 = pw / 2, Y1 = 0, X2 = pw / 2, Y2 = ph, Stroke = accent, StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false });
        if (snapCenterY)
            CanvasAdorner.Children.Add(new Line { X1 = 0, Y1 = ph / 2, X2 = pw, Y2 = ph / 2, Stroke = accent, StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false });

        var box = GetActiveVisualScreenRect(out bool editable);
        if (box.IsEmpty || !editable) return;

        if (cropMode && canCrop)
        {
            var crop = liveCropDraft ?? (selection.Kind == SelectionKind.MainClip
                ? project.Clips[selection.Index].Crop : project.OverlayTrack[selection.Index].Crop) ?? new CropMargins(0, 0, 0, 0);
            double l = box.Left + box.Width * crop.Left / 100.0;
            double t = box.Top + box.Height * crop.Top / 100.0;
            double r = box.Right - box.Width * crop.Right / 100.0;
            double b = box.Bottom - box.Height * crop.Bottom / 100.0;
            var maskBrush = new SolidColorBrush(Color.FromArgb(0x98, 0x06, 0x08, 0x0C));

            // 4 shaded cropped exterior bands
            AddMaskRect(0, 0, pw, Math.Max(0, t), maskBrush);
            AddMaskRect(0, Math.Max(0, b), pw, Math.Max(0, ph - b), maskBrush);
            AddMaskRect(0, Math.Max(0, t), Math.Max(0, l), Math.Max(0, b - t), maskBrush);
            AddMaskRect(Math.Max(0, r), Math.Max(0, t), Math.Max(0, pw - r), Math.Max(0, b - t), maskBrush);

            // Inner crop frame
            var cropFrame = new Rectangle
            {
                Width = Math.Max(2, r - l), Height = Math.Max(2, b - t),
                Stroke = accent, StrokeThickness = 1.6, IsHitTestVisible = false
            };
            Canvas.SetLeft(cropFrame, l);
            Canvas.SetTop(cropFrame, t);
            CanvasAdorner.Children.Add(cropFrame);

            // 4 thick draggable cyan edge bars
            AddCropBar(l - 2, (t + b) / 2 - 16, 4, 32, accent);
            AddCropBar(r - 2, (t + b) / 2 - 16, 4, 32, accent);
            AddCropBar((l + r) / 2 - 16, t - 2, 32, 4, accent);
            AddCropBar((l + r) / 2 - 16, b - 2, 32, 4, accent);
            return;
        }

        // Transform Selection Bounding Box + 4 Corner Handles
        bool showBox = selection.Kind is SelectionKind.Overlay or SelectionKind.Title ||
            (selection.Kind == SelectionKind.MainClip && (liveTransformDraft is { IsDefault: false } || project.Clips[selection.Index].Transform is { IsDefault: false } || canvasDrag != CanvasDragKind.None));
        if (!showBox) return;

        var outline = new Rectangle
        {
            Width = Math.Max(2, box.Width), Height = Math.Max(2, box.Height),
            Stroke = accent, StrokeThickness = 1.4,
            StrokeDashArray = selection.Kind == SelectionKind.Title ? [5, 3] : null,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(outline, box.Left);
        Canvas.SetTop(outline, box.Top);
        CanvasAdorner.Children.Add(outline);

        if (selection.Kind is SelectionKind.MainClip or SelectionKind.Overlay)
        {
            foreach (var pt in new[] { box.TopLeft, box.TopRight, box.BottomLeft, box.BottomRight })
            {
                var h = new Border
                {
                    Width = 9, Height = 9, CornerRadius = new CornerRadius(2),
                    Background = Brushes.White, BorderBrush = accent, BorderThickness = new Thickness(1.6),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(h, pt.X - 4.5);
                Canvas.SetTop(h, pt.Y - 4.5);
                CanvasAdorner.Children.Add(h);
            }
        }
    }

    private void AddMaskRect(double x, double y, double w, double h, Brush brush)
    {
        if (w <= 0 || h <= 0) return;
        var rect = new Rectangle { Width = w, Height = h, Fill = brush, IsHitTestVisible = false };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        CanvasAdorner.Children.Add(rect);
    }

    private void AddCropBar(double x, double y, double w, double h, Brush brush)
    {
        var bar = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(2), Background = brush, IsHitTestVisible = false };
        Canvas.SetLeft(bar, x);
        Canvas.SetTop(bar, y);
        CanvasAdorner.Children.Add(bar);
    }

    private void CanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Busy || UsingRender) return;
        Point pt = e.GetPosition(CanvasAdorner);
        var box = GetActiveVisualScreenRect(out bool editable);
        if (box.IsEmpty || !editable) return;

        canvasDragOrigin = pt;
        snapCenterX = snapCenterY = false;

        if (cropMode && selection.Kind is SelectionKind.MainClip or SelectionKind.Overlay)
        {
            var crop = (selection.Kind == SelectionKind.MainClip
                ? project.Clips[selection.Index].Crop : project.OverlayTrack[selection.Index].Crop) ?? new CropMargins(0, 0, 0, 0);
            double l = box.Left + box.Width * crop.Left / 100.0;
            double t = box.Top + box.Height * crop.Top / 100.0;
            double r = box.Right - box.Width * crop.Right / 100.0;
            double b = box.Bottom - box.Height * crop.Bottom / 100.0;
            const double edgeHit = 12;
            if (Math.Abs(pt.X - l) <= edgeHit) canvasDrag = CanvasDragKind.CropLeft;
            else if (Math.Abs(pt.X - r) <= edgeHit) canvasDrag = CanvasDragKind.CropRight;
            else if (Math.Abs(pt.Y - t) <= edgeHit) canvasDrag = CanvasDragKind.CropTop;
            else if (Math.Abs(pt.Y - b) <= edgeHit) canvasDrag = CanvasDragKind.CropBottom;
            else return;
            dragStartCrop = crop;
            liveCropDraft = crop;
            CanvasAdorner.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (selection.Kind == SelectionKind.Title)
        {
            if (!box.Contains(pt)) return;
            dragStartTextStyle = project.TitleTrack[selection.Index].EffectiveStyle;
            liveTextStyleDraft = dragStartTextStyle;
            canvasDrag = CanvasDragKind.MoveTitle;
            CanvasAdorner.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (selection.Kind is SelectionKind.MainClip or SelectionKind.Overlay)
        {
            var tr = selection.Kind == SelectionKind.MainClip
                ? project.Clips[selection.Index].EffectiveTransform
                : project.OverlayTrack[selection.Index].EffectiveTransform;
            bool onCorner = new[] { box.TopLeft, box.TopRight, box.BottomLeft, box.BottomRight }
                .Any(c => (c - pt).Length <= 11);
            if (!onCorner && !box.Contains(pt)) return;
            dragStartTransform = tr;
            liveTransformDraft = tr;
            canvasDrag = onCorner ? CanvasDragKind.ScaleVideo : CanvasDragKind.MoveVideo;
            CanvasAdorner.CaptureMouse();
            e.Handled = true;
        }
    }

    private void CanvasMouseMove(object sender, MouseEventArgs e)
    {
        Point pt = e.GetPosition(CanvasAdorner);
        double pw = Math.Max(1, Picture.ActualWidth), ph = Math.Max(1, Picture.ActualHeight);
        if (canvasDrag == CanvasDragKind.None)
        {
            UpdateCanvasCursor(pt);
            return;
        }

        snapCenterX = snapCenterY = false;
        double dx = (pt.X - canvasDragOrigin.X) / pw;
        double dy = (pt.Y - canvasDragOrigin.Y) / ph;

        if (canvasDrag == CanvasDragKind.MoveVideo && dragStartTransform is { } tr0)
        {
            double nx = Math.Clamp(tr0.X + dx, -1.5, 1.5);
            double ny = Math.Clamp(tr0.Y + dy, -1.5, 1.5);
            if (Math.Abs(nx) < 0.018) { nx = 0; snapCenterX = true; }
            if (Math.Abs(ny) < 0.018) { ny = 0; snapCenterY = true; }
            liveTransformDraft = tr0 with { X = nx, Y = ny };
            PositionReadout.Text = $"位置 X {nx:P0}  ·  Y {ny:P0}（可在画布直接拖动）";
            RefreshLiveCanvas();
            return;
        }

        if (canvasDrag == CanvasDragKind.ScaleVideo && dragStartTransform is { } trs)
        {
            Point center = new(pw * (0.5 + trs.X), ph * (0.5 + trs.Y));
            double d0 = Math.Max(16, (canvasDragOrigin - center).Length);
            double d1 = Math.Max(16, (pt - center).Length);
            double nextScale = Math.Clamp(trs.Scale * (d1 / d0), 0.1, 3.0);
            liveTransformDraft = trs with { Scale = nextScale };
            refreshing = true;
            ScaleSlider.Value = nextScale * 100;
            ScaleValueText.Text = $"{nextScale * 100:0}%";
            refreshing = false;
            RefreshLiveCanvas();
            return;
        }

        if (canvasDrag == CanvasDragKind.MoveTitle && dragStartTextStyle is { } st0)
        {
            double nx = Math.Clamp(st0.X + dx, -0.48, 0.48);
            double ny = Math.Clamp(st0.Y + dy, -0.46, 0.46);
            if (Math.Abs(nx) < 0.018) { nx = 0; snapCenterX = true; }
            if (Math.Abs(ny) < 0.018) { ny = 0; snapCenterY = true; }
            liveTextStyleDraft = st0 with { X = nx, Y = ny };
            RefreshLiveCanvas();
            return;
        }

        if (dragStartCrop is { } c0)
        {
            var box = GetActiveVisualScreenRect(out _);
            double bw = Math.Max(10, box.Width), bh = Math.Max(10, box.Height);
            int dPctX = (int)Math.Round((pt.X - canvasDragOrigin.X) / bw * 100);
            int dPctY = (int)Math.Round((pt.Y - canvasDragOrigin.Y) / bh * 100);
            liveCropDraft = canvasDrag switch
            {
                CanvasDragKind.CropLeft => c0 with { Left = Math.Clamp(c0.Left + dPctX, 0, 90 - c0.Right) },
                CanvasDragKind.CropRight => c0 with { Right = Math.Clamp(c0.Right - dPctX, 0, 90 - c0.Left) },
                CanvasDragKind.CropTop => c0 with { Top = Math.Clamp(c0.Top + dPctY, 0, 90 - c0.Bottom) },
                CanvasDragKind.CropBottom => c0 with { Bottom = Math.Clamp(c0.Bottom - dPctY, 0, 90 - c0.Top) },
                _ => c0
            };
            CropBadge.Text = liveCropDraft.IsZero ? "未裁剪" : $"{liveCropDraft.Left}/{liveCropDraft.Top}/{liveCropDraft.Right}/{liveCropDraft.Bottom}%";
            RefreshLiveCanvas();
        }
    }

    private void CanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (canvasDrag == CanvasDragKind.None) return;
        var mode = canvasDrag;
        var tr = liveTransformDraft;
        var cr = liveCropDraft;
        var st = liveTextStyleDraft;
        canvasDrag = CanvasDragKind.None;
        dragStartTransform = null;
        dragStartCrop = null;
        dragStartTextStyle = null;
        liveTransformDraft = null;
        liveCropDraft = null;
        liveTextStyleDraft = null;
        snapCenterX = snapCenterY = false;
        CanvasAdorner.ReleaseMouseCapture();

        if (mode is CanvasDragKind.MoveVideo or CanvasDragKind.ScaleVideo && tr is not null)
            ApplyVideoTransform(tr);
        else if (mode == CanvasDragKind.MoveTitle && st is not null && selection.Kind == SelectionKind.Title)
            UpdateSelectedTitle(t => t with { Style = st });
        else if (mode is CanvasDragKind.CropLeft or CanvasDragKind.CropRight or CanvasDragKind.CropTop or CanvasDragKind.CropBottom && cr is not null)
            ApplyVideoCrop(cr.IsZero ? null : cr);
        else
            RefreshLiveCanvas();
        e.Handled = true;
    }

    private void UpdateCanvasCursor(Point pt)
    {
        var box = GetActiveVisualScreenRect(out bool editable);
        if (box.IsEmpty || !editable) { CanvasAdorner.Cursor = Cursors.Arrow; return; }
        if (cropMode && selection.Kind is SelectionKind.MainClip or SelectionKind.Overlay)
        {
            var crop = (selection.Kind == SelectionKind.MainClip
                ? project.Clips[selection.Index].Crop : project.OverlayTrack[selection.Index].Crop) ?? new CropMargins(0, 0, 0, 0);
            double l = box.Left + box.Width * crop.Left / 100.0;
            double t = box.Top + box.Height * crop.Top / 100.0;
            double r = box.Right - box.Width * crop.Right / 100.0;
            double b = box.Bottom - box.Height * crop.Bottom / 100.0;
            if (Math.Abs(pt.X - l) <= 12 || Math.Abs(pt.X - r) <= 12) CanvasAdorner.Cursor = Cursors.SizeWE;
            else if (Math.Abs(pt.Y - t) <= 12 || Math.Abs(pt.Y - b) <= 12) CanvasAdorner.Cursor = Cursors.SizeNS;
            else CanvasAdorner.Cursor = Cursors.Arrow;
            return;
        }
        if (selection.Kind is SelectionKind.MainClip or SelectionKind.Overlay)
        {
            if (new[] { box.TopLeft, box.BottomRight }.Any(c => (c - pt).Length <= 11)) { CanvasAdorner.Cursor = Cursors.SizeNWSE; return; }
            if (new[] { box.TopRight, box.BottomLeft }.Any(c => (c - pt).Length <= 11)) { CanvasAdorner.Cursor = Cursors.SizeNESW; return; }
        }
        CanvasAdorner.Cursor = box.Contains(pt) ? Cursors.SizeAll : Cursors.Arrow;
    }

    private void SyncOverlayPlayer(double position)
    {
        if (UsingRender || project.GetTrack("V2").Hidden)
        {
            StopOverlayMedia();
            return;
        }
        int idx = -1;
        for (int i = project.OverlayTrack.Length - 1; i >= 0; i--)
        {
            var ov = project.OverlayTrack[i];
            if (position >= ov.Start - 0.0001 && position < ov.End - 0.0001) { idx = i; break; }
        }
        activeOverlayIndex = idx;
        if (idx < 0)
        {
            StopOverlayMedia();
            return;
        }
        var activeOv = project.OverlayTrack[idx];
        if (!assets.TryGetValue(activeOv.Path, out var row))
        {
            StopOverlayMedia();
            return;
        }
        if (row.Asset.IsImage)
        {
            if (overlaySourcePath is not null)
            {
                OverlayPlayer.Close();
                OverlayPlayer.Source = null;
                overlaySourcePath = null;
            }
            OverlayPlayer.Visibility = Visibility.Collapsed;
            OverlayImageLayer.Source = imageFrames.GetValueOrDefault(row.Asset.Path) ?? row.Thumbnail;
            OverlayImageLayer.Visibility = Visibility.Visible;
            overlayPlayerReady = true;
            return;
        }
        OverlayImageLayer.Visibility = Visibility.Collapsed;
        OverlayImageLayer.Source = null;
        OverlayPlayer.Visibility = Visibility.Visible;

        string media = proxies.GetValueOrDefault(row.Asset.Path) ?? row.Asset.Path;
        double lastFrame = Math.Max(activeOv.In, activeOv.Out - 0.018);
        pendingOverlayLocal = Math.Clamp(activeOv.In + (position - activeOv.Start) * activeOv.EffectiveSpeed, activeOv.In, lastFrame);
        if (!string.Equals(overlaySourcePath, media, StringComparison.OrdinalIgnoreCase))
        {
            overlayPlayerReady = false;
            overlaySourcePath = media;
            OverlayPlayer.Position = TimeSpan.Zero;
            OverlayPlayer.Source = new Uri(media);
            OverlayPlayer.Play();
        }
        else if (overlayPlayerReady)
        {
            ResumeOverlayAtPending();
        }
    }

    private void StopOverlayMedia()
    {
        activeOverlayIndex = -1;
        if (overlaySourcePath is not null)
        {
            OverlayPlayer.Close();
            OverlayPlayer.Source = null;
            overlaySourcePath = null;
            overlayPlayerReady = false;
        }
        OverlayImageLayer.Visibility = Visibility.Collapsed;
        OverlayImageLayer.Source = null;
        OverlayPlayer.Visibility = Visibility.Visible;
        OverlayVideoLayer.Visibility = Visibility.Collapsed;
    }

    private void OverlayPlayerOpened(object sender, RoutedEventArgs e)
    {
        if (activeOverlayIndex < 0 || overlaySourcePath is null) return;
        overlayPlayerReady = true;
        ResumeOverlayAtPending();
        RefreshLiveCanvas();
    }

    private void ResumeOverlayAtPending()
    {
        if (activeOverlayIndex < 0 || activeOverlayIndex >= project.OverlayTrack.Length) return;
        bool muted = project.GetTrack("V2").Muted;
        OverlayPlayer.Volume = muted ? 0 : Math.Min(1, project.OverlayTrack[activeOverlayIndex].Volume);
        OverlayPlayer.Position = TimeSpan.FromSeconds(pendingOverlayLocal);
        if (playing) OverlayPlayer.Play();
        else OverlayPlayer.Pause();
    }

    private void EnterCropModeClick(object sender, RoutedEventArgs e)
    {
        cropMode = true;
        CropModeToggle.IsChecked = true;
        RefreshLiveCanvas();
    }

    private void ExitCropModeClick(object sender, RoutedEventArgs e)
    {
        cropMode = false;
        CropModeToggle.IsChecked = false;
        RefreshLiveCanvas();
    }

    private void CropModeToggleClick(object sender, RoutedEventArgs e)
    {
        cropMode = CropModeToggle.IsChecked == true;
        RefreshLiveCanvas();
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }

    private static Brush ParseBrush(string hex, Brush fallback)
    {
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
        catch { return fallback; }
    }
}
