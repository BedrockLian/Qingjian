using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Qingjian;

public sealed class TimelineView : FrameworkElement
{
    // Exact lane geometry shared pixel-for-pixel with MainWindow.xaml track headers:
    // Ruler:       0..30   (30)
    // Overlay V2:  30..82  (52) -> Clip: 35..77  (42)
    // Video V1:    82..148 (66) -> Clip: 87..143 (56)
    // Audio A1:    148..182 (34) -> Bar: 152..178 (26)
    // Title T1:    182..214 (32) -> Bar: 186..210 (24)
    // Audio A2:    214..248 (34) -> Bar: 218..244 (26)
    private const double Lead = 16;
    private const double RulerHeight = 30;

    public const double OverlayLaneTop = 30;
    public const double OverlayLaneHeight = 52;
    private const double OverlayTop = 35;
    private const double OverlayHeight = 42;

    public const double VideoLaneTop = 82;
    public const double VideoLaneHeight = 66;
    private const double VideoTop = 87;
    private const double VideoHeight = 56;

    public const double AudioLaneTop = 148;
    public const double AudioLaneHeight = 34;
    private const double AudioTop = 152;
    private const double AudioHeight = 26;

    public const double TitleLaneTop = 182;
    public const double TitleLaneHeight = 32;
    private const double TitleTop = 186;
    private const double TitleHeight = 24;

    public const double DetachedLaneTop = 214;
    public const double DetachedLaneHeight = 34;
    private const double DetachedTop = 218;
    private const double DetachedHeight = 26;

    private const double HandleWidth = 7;
    private const double TrackHeight = 254;
    private const double SnapThresholdPx = 8;

    private static readonly Typeface TimeFont = new("Cascadia Mono, Consolas");
    private static readonly Typeface NameFont = new(new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    private static readonly Typeface BoldFont = new(new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    // Obsidian Studio Palette
    private static readonly Brush Background = Paint(0x0F, 0x11, 0x15);
    private static readonly Brush RulerBg = Paint(0x13, 0x15, 0x1A);
    private static readonly Brush LaneDark = Paint(0x10, 0x12, 0x17);
    private static readonly Brush LaneAlt = Paint(0x13, 0x16, 0x1C);
    private static readonly Brush GridLine = Paint(0x1A, 0x1D, 0x25);
    private static readonly Brush RulerTickMajor = Paint(0x4A, 0x51, 0x62);
    private static readonly Brush RulerTickMinor = Paint(0x2A, 0x2F, 0x3B);
    private static readonly Brush LaneDivider = Paint(0x1E, 0x21, 0x2A);
    private static readonly Brush Muted = Paint(0x76, 0x7E, 0x8F);
    private static readonly Brush Text = Paint(0xED, 0xED, 0xEF);
    private static readonly Brush Accent = Paint(0x18, 0xD2, 0xE6);
    private static readonly Brush AccentGlow = PaintA(0x28, 0x18, 0xD2, 0xE6);
    private static readonly Brush PlayheadLine = Paint(0xF5, 0xF7, 0xFA);

    // Track Semantic Colors
    private static readonly Brush OverlayWell = Paint(0x1D, 0x20, 0x3A);
    private static readonly Brush OverlayBorder = Paint(0x52, 0x5D, 0xB8);
    private static readonly Brush OverlayHoverBorder = Paint(0x7A, 0x88, 0xF5);
    private static readonly Brush OverlayTagBg = Paint(0x7A, 0x88, 0xF5);

    private static readonly Brush ClipWell = Paint(0x16, 0x24, 0x2B);
    private static readonly Brush ClipWellSelected = Paint(0x1C, 0x30, 0x3A);
    private static readonly Brush ClipBorder = Paint(0x2B, 0x4E, 0x5E);
    private static readonly Brush ClipHoverBorder = Paint(0x4A, 0x7D, 0x94);
    private static readonly Brush ClipHeaderScrim = MakeVerticalGradient(Color.FromArgb(0xDC, 0x0A, 0x0E, 0x13), Color.FromArgb(0x00, 0x0A, 0x0E, 0x13));
    private static readonly Brush ChipBg = PaintA(0xC6, 0x09, 0x0B, 0x0F);
    private static readonly Brush CropChipBg = PaintA(0xDC, 0x10, 0x33, 0x3C);

    private static readonly Brush OrigAudioBg = Paint(0x14, 0x2F, 0x2D);
    private static readonly Brush OrigAudioMutedBg = Paint(0x1B, 0x1E, 0x24);
    private static readonly Brush OrigAudioBorder = Paint(0x23, 0x54, 0x50);
    private static readonly Brush OrigAudioWave = Paint(0x3D, 0xBD, 0xAF);
    private static readonly Brush OrigAudioText = Paint(0xB8, 0xED, 0xE6);

    private static readonly Brush TitleBg = Paint(0x43, 0x29, 0x1E);
    private static readonly Brush TitleHoverBg = Paint(0x52, 0x33, 0x25);
    private static readonly Brush TitleBorder = Paint(0x7B, 0x4C, 0x37);
    private static readonly Brush TitleTagBg = Paint(0xC9, 0x76, 0x4E);
    private static readonly Brush TitleText = Paint(0xFA, 0xE5, 0xDA);

    private static readonly Brush DetachedBg = Paint(0x17, 0x29, 0x42);
    private static readonly Brush DetachedHoverBg = Paint(0x1E, 0x35, 0x56);
    private static readonly Brush DetachedBorder = Paint(0x2B, 0x4F, 0x7E);
    private static readonly Brush DetachedWave = Paint(0x5B, 0x9E, 0xF2);
    private static readonly Brush DetachedText = Paint(0xCA, 0xE1, 0xFC);

    private static readonly Brush DissolveBg = PaintA(0xE8, 0x10, 0x28, 0x30);
    private static readonly Brush FadeShade = PaintA(0x78, 0x08, 0x0A, 0x0E);
    private static readonly Brush MutedLineBrush = Paint(0x49, 0x50, 0x60);

    private static readonly Pen GridPen = FrozenPen(GridLine, 1);
    private static readonly Pen LanePen = FrozenPen(LaneDivider, 1);
    private static readonly Pen TickMajorPen = FrozenPen(RulerTickMajor, 1);
    private static readonly Pen TickMinorPen = FrozenPen(RulerTickMinor, 1);
    private static readonly Pen OverlayBorderPen = FrozenPen(OverlayBorder, 1);
    private static readonly Pen OverlayHoverPen = FrozenPen(OverlayHoverBorder, 1.3);
    private static readonly Pen ClipBorderPen = FrozenPen(ClipBorder, 1);
    private static readonly Pen ClipHoverPen = FrozenPen(ClipHoverBorder, 1.2);
    private static readonly Pen SelectedPen = FrozenPen(Text, 1.6);
    private static readonly Pen OrigAudioBorderPen = FrozenPen(OrigAudioBorder, 1);
    private static readonly Pen MutedBorderPen = FrozenPen(LaneDivider, 1);
    private static readonly Pen TitleBorderPen = FrozenPen(TitleBorder, 1);
    private static readonly Pen DetachedBorderPen = FrozenPen(DetachedBorder, 1);
    private static readonly Pen DissolveBorderPen = FrozenPen(Accent, 1.1);
    private static readonly Pen DissolveGlyphPen = FrozenPen(Text, 1.2);
    private static readonly Pen PlayheadPen = FrozenPen(PlayheadLine, 1.4);
    private static readonly Pen PlayheadGlowPen = FrozenPen(AccentGlow, 5);
    private static readonly Pen InsertPen = FrozenPen(Accent, 2);
    private static readonly Pen SnapPen = FrozenDashPen(Accent, 1.2);
    private static readonly Pen MutedDashPen = FrozenDashPen(MutedLineBrush, 1);
    private static readonly Pen FadeEdgePen = FrozenPen(PaintA(0xCC, 0xED, 0xED, 0xEF), 1);
    private static readonly Pen HandleGripPen = FrozenPen(Paint(0x08, 0x18, 0x1C), 1.1);

    private enum DragMode { None, Scrub, TrimMainStart, TrimMainEnd, MoveOverlay, TrimOverlayStart, TrimOverlayEnd, MoveTitle, TrimTitleStart, TrimTitleEnd, MoveAudio, TrimAudioStart, TrimAudioEnd }

    public event Action<TimelineSelection>? SelectionChanged;
    public event Action<double>? Seeked;
    public event Action<int, double, double>? MainClipTrimmed;
    public event Action<int, double, double, double>? OverlayEdited;
    public event Action<int, double, double>? TitleEdited;
    public event Action<int, double, double, double>? AudioEdited;
    public event Action<TimelineSelection>? ItemDoubleClicked;
    public event Action<TimelineSelection, Point>? ContextMenuRequested;

    public IReadOnlyList<Clip> Clips { get; set; } = [];
    public IReadOnlyList<OverlayClip> Overlays { get; set; } = [];
    public IReadOnlyList<TitleLayer> Titles { get; set; } = [];
    public IReadOnlyList<AudioLayer> Audio { get; set; } = [];
    public double Duration { get; set; }
    public Func<string, MediaAsset?> GetAsset { get; set; } = _ => null;
    public Func<string, ImageSource?> GetThumbnail { get; set; } = _ => null;
    public Func<string, float[]?> GetWaveform { get; set; } = _ => null;
    public Func<string, TrackFlags> GetTrackFlags { get; set; } = _ => new TrackFlags();
    public double Scale { get; private set; } = 66;
    public TimelineSelection Selection { get; set; } = TimelineSelection.None;
    public double Head { get; set; }
    public double ViewOffset { get; set; }
    public double ViewWidth { get; set; } = 800;

    private DragMode dragMode = DragMode.None;
    private int dragIndex = -1;
    private double dragStartX;
    private Clip? draftMain;
    private OverlayClip? draftOverlay;
    private TitleLayer? draftTitle;
    private AudioLayer? draftAudio;
    private TimelineSelection hovered = TimelineSelection.None;
    private (string TrackId, double Time)? dropIndicator;
    private double? snapGuideTime;

    public void SetScale(double value)
    {
        Scale = value;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    public double PositionOf(double time) => Lead + time * Scale;
    public double TimeOf(double x) => Math.Max(0, (x - Lead) / Scale);

    public (string TrackId, int InsertIndex, double Time) DropTargetAt(Point point)
    {
        double time = TimeOf(point.X);
        if (point.Y >= OverlayLaneTop && point.Y < VideoLaneTop)
            return ("V2", Overlays.Count, SnapTime(time, SelectionKind.None, -1));
        if (point.Y >= DetachedLaneTop)
            return ("A2", Audio.Count, SnapTime(time, SelectionKind.None, -1));
        var (idx, cutTime) = InsertionAt(point.X);
        return ("V1", idx, cutTime);
    }

    public (int Index, double Time) InsertionAt(double x)
    {
        double target = (x - Lead) / Scale;
        double start = 0;
        for (int i = 0; i < Clips.Count; i++)
        {
            double length = Clips[i].Duration;
            if (target <= start + length / 2) return (i, start);
            start += length;
        }
        return (Clips.Count, start);
    }

    public void ShowDropIndicator(string? trackId, double? time)
    {
        var next = trackId is not null && time is double t ? (trackId, t) : ((string, double)?)null;
        if (dropIndicator == next) return;
        dropIndicator = next;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Max(ViewWidth, PositionOf(Duration) + 56),
            Math.Max(TrackHeight, double.IsFinite(availableSize.Height) ? availableSize.Height : TrackHeight));

    protected override void OnRender(DrawingContext dc)
    {
        double left = Math.Max(0, ViewOffset - 4), right = Math.Min(ActualWidth, ViewOffset + ViewWidth + 6);
        if (right <= left) return;
        double fullHeight = Math.Max(TrackHeight, ActualHeight);

        dc.DrawRectangle(Background, null, new Rect(left, 0, right - left, fullHeight));
        dc.DrawRectangle(RulerBg, null, new Rect(left, 0, right - left, RulerHeight));
        dc.DrawRectangle(LaneDark, null, new Rect(left, OverlayLaneTop, right - left, OverlayLaneHeight));
        dc.DrawRectangle(LaneAlt, null, new Rect(left, VideoLaneTop, right - left, VideoLaneHeight));
        dc.DrawRectangle(LaneDark, null, new Rect(left, AudioLaneTop, right - left, AudioLaneHeight));
        dc.DrawRectangle(LaneAlt, null, new Rect(left, TitleLaneTop, right - left, TitleLaneHeight));
        dc.DrawRectangle(LaneDark, null, new Rect(left, DetachedLaneTop, right - left, DetachedLaneHeight));

        // Time ruler ticks & vertical subtle gridlines
        double major = NiceTick(96 / Scale);
        double tick = major / 5;
        long firstTick = Math.Max(0, (long)Math.Floor((left - Lead) / (tick * Scale)));
        long lastTick = Math.Max(0, (long)Math.Ceiling((right - Lead) / (tick * Scale)));
        for (long i = firstTick; i <= lastTick; i++)
        {
            double time = i * tick, x = Math.Round(PositionOf(time)) + 0.5;
            bool majorTick = i % 5 == 0;
            if (majorTick)
            {
                dc.DrawLine(GridPen, new Point(x, RulerHeight), new Point(x, fullHeight));
                dc.DrawLine(TickMajorPen, new Point(x, 18), new Point(x, RulerHeight));
                string label = EditTime.Display(time, major < 1);
                dc.DrawText(Label(time < 3600 ? label[3..] : label, 10, Muted, mono: true), new Point(x + 4, 5));
            }
            else
            {
                dc.DrawLine(TickMinorPen, new Point(x, 23), new Point(x, RulerHeight));
            }
        }

        // Crisp 1px Horizontal Lane Dividers matching MainWindow Track Headers
        foreach (double y in new[] { RulerHeight, VideoLaneTop, AudioLaneTop, TitleLaneTop, DetachedLaneTop, DetachedLaneTop + DetachedLaneHeight })
            dc.DrawLine(LanePen, new Point(left, y - 0.5), new Point(right, y - 0.5));

        if (Overlays.Count == 0 && Clips.Count > 0)
        {
            var ovHint = Label("拖入视频到此轨添加画中画（支持自由移动、缩放与吸附）", 10.5, Paint(0x4C, 0x52, 0x62));
            dc.DrawText(ovHint, new Point(Lead + 6, OverlayLaneTop + (OverlayLaneHeight - ovHint.Height) / 2));
        }
        if (Clips.Count == 0)
        {
            var hint = Label("拖入本地视频，或点击素材卡片「＋」快速添加到主视频轨", 12, Muted);
            dc.DrawText(hint, new Point(Lead + 8, VideoLaneTop + (VideoLaneHeight - hint.Height) / 2));
        }

        // Draw V2 PiP Overlays
        var v2Flags = GetTrackFlags("V2");
        if (v2Flags.Hidden) dc.PushOpacity(0.38);
        for (int i = 0; i < Overlays.Count; i++)
        {
            var ov = dragIndex == i && draftOverlay is not null ? draftOverlay : Overlays[i];
            bool sel = Selection == new TimelineSelection(SelectionKind.Overlay, i);
            bool hov = hovered == new TimelineSelection(SelectionKind.Overlay, i);
            DrawOverlayClip(dc, ov, sel, hov, left, right);
        }
        if (v2Flags.Hidden) dc.Pop();

        // Draw V1 Main Video & A1 Original Audio
        var v1Flags = GetTrackFlags("V1");
        var a1Flags = GetTrackFlags("A1");
        double start = 0;
        List<(double CutX, double SpanWidth, double DissolveSec)> dissolves = [];
        for (int i = 0; i < Clips.Count; i++)
        {
            var clip = dragIndex == i && draftMain is not null ? draftMain : Clips[i];
            double x = PositionOf(start);
            double width = Math.Max(2, clip.Duration * Scale - 1.5);
            if (x <= right && x + width >= left)
            {
                bool sel = Selection == new TimelineSelection(SelectionKind.MainClip, i);
                bool hov = hovered == new TimelineSelection(SelectionKind.MainClip, i);
                DrawMainClip(dc, clip, sel, hov, v1Flags.Hidden, a1Flags.Muted || v1Flags.Muted, x, width);
            }
            start += clip.Duration;
            if (i + 1 < Clips.Count && clip.Dissolve > 0)
            {
                double length = EditProject.TransitionLength(clip, Clips[i + 1]);
                double cutX = PositionOf(start);
                if (cutX + length * Scale >= left && cutX - length * Scale <= right)
                    dissolves.Add((cutX, length * Scale, clip.Dissolve));
            }
            if (x > right) break;
        }
        foreach (var (cutX, spanWidth, dissolveSec) in dissolves)
            DrawDissolveBadge(dc, cutX, spanWidth, dissolveSec);

        // Draw T1 Titles
        var t1Flags = GetTrackFlags("T1");
        if (t1Flags.Hidden) dc.PushOpacity(0.38);
        for (int i = 0; i < Titles.Count; i++)
        {
            var title = dragIndex == i && draftTitle is not null ? draftTitle : Titles[i];
            bool sel = Selection == new TimelineSelection(SelectionKind.Title, i);
            bool hov = hovered == new TimelineSelection(SelectionKind.Title, i);
            DrawTitleLayer(dc, title, sel, hov, left, right);
        }
        if (t1Flags.Hidden) dc.Pop();

        // Draw A2 Detached Audio
        var a2Flags = GetTrackFlags("A2");
        if (a2Flags.Muted) dc.PushOpacity(0.42);
        for (int i = 0; i < Audio.Count; i++)
        {
            var layer = dragIndex == i && draftAudio is not null ? draftAudio : Audio[i];
            bool sel = Selection == new TimelineSelection(SelectionKind.Audio, i);
            bool hov = hovered == new TimelineSelection(SelectionKind.Audio, i);
            DrawDetachedAudioLayer(dc, layer, sel, hov, left, right);
        }
        if (a2Flags.Muted) dc.Pop();

        // Draw Snap Alignment Guide Line
        if (snapGuideTime is double snapT)
        {
            double sx = Math.Round(PositionOf(snapT)) + 0.5;
            if (sx >= left && sx <= right)
                dc.DrawLine(SnapPen, new Point(sx, RulerHeight), new Point(sx, fullHeight));
        }

        // Draw Playhead
        double playhead = Math.Round(PositionOf(Head)) + 0.5;
        if (playhead >= left - 8 && playhead <= right + 8)
        {
            dc.DrawLine(PlayheadGlowPen, new Point(playhead, 16), new Point(playhead, fullHeight));
            dc.DrawLine(PlayheadPen, new Point(playhead, 15), new Point(playhead, fullHeight));
            var cap = new StreamGeometry();
            using (var ctx = cap.Open())
            {
                ctx.BeginFigure(new Point(playhead - 5.5, 4), true, true);
                ctx.LineTo(new Point(playhead + 5.5, 4), true, false);
                ctx.LineTo(new Point(playhead + 5.5, 11.5), true, false);
                ctx.LineTo(new Point(playhead, 16.5), true, false);
                ctx.LineTo(new Point(playhead - 5.5, 11.5), true, false);
            }
            cap.Freeze();
            dc.DrawGeometry(Accent, null, cap);
        }

        // Draw Drag-Drop Insertion Marker
        if (dropIndicator is var (dropTrack, timeAtDrop))
        {
            double x = Math.Round(PositionOf(timeAtDrop)) + 0.5;
            if (x >= left - 6 && x <= right + 6)
            {
                double yTop = dropTrack == "V2" ? OverlayLaneTop : dropTrack == "A2" ? DetachedLaneTop : VideoLaneTop;
                double yBot = dropTrack == "V2" ? OverlayLaneTop + OverlayLaneHeight : dropTrack == "A2" ? DetachedLaneTop + DetachedLaneHeight : AudioLaneTop + AudioLaneHeight;
                dc.DrawLine(InsertPen, new Point(x, yTop + 2), new Point(x, yBot));
                var tri = new StreamGeometry();
                using (var ctx = tri.Open())
                {
                    ctx.BeginFigure(new Point(x - 5, yTop), true, true);
                    ctx.LineTo(new Point(x + 5, yTop), true, false);
                    ctx.LineTo(new Point(x, yTop + 6), true, false);
                }
                tri.Freeze();
                dc.DrawGeometry(Accent, null, tri);
            }
        }
    }

    private void DrawOverlayClip(DrawingContext dc, OverlayClip ov, bool selected, bool hovered, double left, double right)
    {
        double x = PositionOf(ov.Start);
        double width = Math.Max(4, ov.Duration * Scale - 1.5);
        if (x > right || x + width < left) return;

        var rect = new Rect(x, OverlayTop, width, OverlayHeight);
        dc.DrawRoundedRectangle(OverlayWell, null, rect, 5.5, 5.5);

        if (width > 10)
        {
            dc.PushClip(new RectangleGeometry(new Rect(x + 1, OverlayTop + 1, Math.Max(0, width - 2), OverlayHeight - 2), 4.5, 4.5));
            if (GetThumbnail(ov.Path) is BitmapSource thumb && thumb.PixelHeight > 0)
            {
                double tileH = OverlayHeight - 2;
                double ratio = Math.Clamp((double)thumb.PixelWidth / thumb.PixelHeight, 0.5, 2.4);
                double tileW = Math.Max(28, tileH * ratio);
                for (double tx = x + 1; tx < x + width; tx += tileW)
                    dc.DrawImage(thumb, new Rect(tx, OverlayTop + 1, tileW + 0.5, tileH));
            }
            dc.DrawRectangle(ClipHeaderScrim, null, new Rect(x, OverlayTop, width, 22));

            double padLeft = selected && width >= 26 ? 10 : 5;
            if (width >= 38)
            {
                dc.DrawRoundedRectangle(OverlayTagBg, null, new Rect(x + padLeft, OverlayTop + 4, 20, 14), 3, 3);
                var pip = Label("PiP", 8.5, Paint(0x0B, 0x0C, 0x16), bold: true);
                dc.DrawText(pip, new Point(x + padLeft + (20 - pip.Width) / 2, OverlayTop + 4.5));
                padLeft += 24;
            }
            double avail = width - padLeft - (selected ? 10 : 6);
            if (avail > 14)
            {
                var name = Label(ov.Name, 10.5, Text, bold: true);
                name.MaxTextWidth = Math.Clamp(avail, 1, 480);
                name.MaxLineCount = 1;
                name.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(name, new Point(x + padLeft, OverlayTop + 3.5));
            }
            dc.Pop();
        }

        dc.DrawRoundedRectangle(null, selected ? SelectedPen : hovered ? OverlayHoverPen : OverlayBorderPen, rect, 5.5, 5.5);
        if (selected && width >= 22) DrawTrimHandles(dc, x, OverlayTop, width, OverlayHeight);
    }

    private void DrawMainClip(DrawingContext dc, Clip clip, bool selected, bool hovered, bool hidden, bool trackMuted, double x, double width)
    {
        if (hidden) dc.PushOpacity(0.38);
        var rect = new Rect(x, VideoTop, width, VideoHeight);
        dc.DrawRoundedRectangle(selected ? ClipWellSelected : ClipWell, null, rect, 6, 6);

        if (width > 10)
        {
            dc.PushClip(new RectangleGeometry(new Rect(x + 1, VideoTop + 1, Math.Max(0, width - 2), VideoHeight - 2), 5, 5));
            if (GetThumbnail(clip.Path) is BitmapSource thumbnail && thumbnail.PixelHeight > 0)
            {
                double tileH = VideoHeight - 2;
                double ratio = Math.Clamp((double)thumbnail.PixelWidth / thumbnail.PixelHeight, 0.5, 2.4);
                double tileW = Math.Max(32, tileH * ratio);
                for (double tx = x + 1; tx < x + width; tx += tileW)
                    dc.DrawImage(thumbnail, new Rect(tx, VideoTop + 1, tileW + 0.5, tileH));
            }
            dc.DrawRectangle(ClipHeaderScrim, null, new Rect(x, VideoTop, width, 25));

            double padLeft = selected && width >= 26 ? 11 : 7;
            double rightReserved = 6;
            if (width >= 115)
            {
                var dur = Label(EditTime.Display(clip.Duration, true), 9.5, Text, mono: true);
                double pillW = dur.Width + 10;
                double pillX = x + width - pillW - (selected ? 11 : 6);
                dc.DrawRoundedRectangle(ChipBg, null, new Rect(pillX, VideoTop + 4, pillW, 16), 3.5, 3.5);
                dc.DrawText(dur, new Point(pillX + 5, VideoTop + 5));
                rightReserved = pillW + (selected ? 16 : 11);
            }

            double titleMaxW = width - padLeft - rightReserved;
            if (titleMaxW > 14)
            {
                var title = Label(clip.Name, 11, Text, bold: true);
                title.MaxTextWidth = Math.Clamp(titleMaxW, 1, 560);
                title.Trimming = TextTrimming.CharacterEllipsis;
                title.MaxLineCount = 1;
                dc.DrawText(title, new Point(x + padLeft, VideoTop + 4));
            }

            // Bottom badges (Crop / Transform / Speed)
            double badgeX = x + padLeft;
            if (clip.Crop is { IsZero: false } && width >= 66)
            {
                badgeX += DrawMiniBadge(dc, "裁剪", badgeX, VideoTop + VideoHeight - 19);
            }
            if (clip.Transform is { IsDefault: false } && badgeX + 36 < x + width - 10)
            {
                badgeX += DrawMiniBadge(dc, "变换", badgeX, VideoTop + VideoHeight - 19);
            }
            if (Math.Abs(clip.EffectiveSpeed - 1) > 0.001 && badgeX + 38 < x + width - 10)
            {
                DrawMiniBadge(dc, $"{clip.EffectiveSpeed:0.##}x", badgeX, VideoTop + VideoHeight - 19);
            }
            dc.Pop();
        }

        dc.DrawRoundedRectangle(null, selected ? SelectedPen : hovered ? ClipHoverPen : ClipBorderPen, rect, 6, 6);
        if (selected && width >= 22) DrawTrimHandles(dc, x, VideoTop, width, VideoHeight);
        if (hidden) dc.Pop();

        // Embedded Original Audio Lane (A1)
        var asset = GetAsset(clip.Path);
        if (asset?.AudioStream is null) return;
        bool muted = trackMuted || clip.Volume <= 0.0001;
        var audioRect = new Rect(x, AudioTop, width, AudioHeight);
        dc.DrawRoundedRectangle(muted ? OrigAudioMutedBg : OrigAudioBg, muted ? MutedBorderPen : OrigAudioBorderPen, audioRect, 4.5, 4.5);

        if (width > 8)
        {
            dc.PushClip(new RectangleGeometry(new Rect(x + 1, AudioTop + 1, Math.Max(0, width - 2), AudioHeight - 2), 4, 4));
            if (muted)
            {
                double cy = AudioTop + AudioHeight / 2;
                dc.DrawLine(MutedDashPen, new Point(x + 4, cy), new Point(x + width - 4, cy));
            }
            else
            {
                DrawWaveform(dc, clip.Path, clip.In, clip.Out, asset.Duration, clip.Volume, x, AudioTop, width, AudioHeight, OrigAudioWave);
                DrawFadeRamps(dc, x, AudioTop, width, AudioHeight, clip.FadeIn, clip.FadeOut, clip.Duration);
            }
            if (width > 42)
            {
                string volText = muted ? "静音" : $"{clip.Volume:P0}";
                var lbl = Label(volText, 9.5, muted ? Muted : OrigAudioText, mono: true);
                double bw = lbl.Width + 8;
                dc.DrawRoundedRectangle(ChipBg, null, new Rect(x + 4, AudioTop + 4, bw, 14), 3, 3);
                dc.DrawText(lbl, new Point(x + 8, AudioTop + 4.5));
            }
            dc.Pop();
        }
    }

    private double DrawMiniBadge(DrawingContext dc, string text, double x, double y)
    {
        var tag = Label(text, 9, Accent, bold: true);
        double w = tag.Width + 9;
        dc.DrawRoundedRectangle(CropChipBg, DissolveBorderPen, new Rect(x, y, w, 14.5), 3, 3);
        dc.DrawText(tag, new Point(x + 4.5, y + 1));
        return w + 4;
    }

    private static void DrawTrimHandles(DrawingContext dc, double x, double top, double width, double height)
    {
        dc.DrawRoundedRectangle(Accent, null, new Rect(x, top, HandleWidth, height), 3.5, 3.5);
        dc.DrawRoundedRectangle(Accent, null, new Rect(x + width - HandleWidth, top, HandleWidth, height), 3.5, 3.5);
        double midY = top + height / 2;
        double gripHalf = Math.Min(7, height * 0.22);
        dc.DrawLine(HandleGripPen, new Point(x + HandleWidth / 2, midY - gripHalf), new Point(x + HandleWidth / 2, midY + gripHalf));
        dc.DrawLine(HandleGripPen, new Point(x + width - HandleWidth / 2, midY - gripHalf), new Point(x + width - HandleWidth / 2, midY + gripHalf));
    }

    private void DrawDissolveBadge(DrawingContext dc, double cutX, double spanWidth, double dissolveSec)
    {
        double badgeW = Math.Clamp(spanWidth, 26, 62);
        double badgeH = 21;
        double bx = cutX - badgeW / 2;
        double by = VideoTop + (VideoHeight - badgeH) / 2 + 4;
        dc.DrawRoundedRectangle(DissolveBg, DissolveBorderPen, new Rect(bx, by, badgeW, badgeH), 5, 5);

        bool showSec = badgeW >= 50;
        double gx = showSec ? bx + 12 : cutX;
        double gy = by + badgeH / 2;
        var bowtie = new StreamGeometry();
        using (var ctx = bowtie.Open())
        {
            ctx.BeginFigure(new Point(gx - 4.5, gy - 3.6), false, false);
            ctx.LineTo(new Point(gx + 4.5, gy + 3.6), true, false);
            ctx.LineTo(new Point(gx + 4.5, gy - 3.6), true, false);
            ctx.LineTo(new Point(gx - 4.5, gy + 3.6), true, false);
            ctx.LineTo(new Point(gx - 4.5, gy - 3.6), true, false);
        }
        bowtie.Freeze();
        dc.DrawGeometry(null, DissolveGlyphPen, bowtie);

        if (showSec)
        {
            var sec = Label($"{dissolveSec:0.#}s", 9.5, Text, mono: true);
            dc.DrawText(sec, new Point(gx + 9.5, by + (badgeH - sec.Height) / 2));
        }
    }

    private void DrawTitleLayer(DrawingContext dc, TitleLayer title, bool selected, bool hovered, double viewportLeft, double viewportRight)
    {
        double x = PositionOf(title.Start), width = Math.Max(6, title.Duration * Scale - 1.5);
        if (x > viewportRight || x + width < viewportLeft) return;
        var rect = new Rect(x, TitleTop, width, TitleHeight);
        dc.DrawRoundedRectangle(hovered ? TitleHoverBg : TitleBg, selected ? SelectedPen : TitleBorderPen, rect, 4.5, 4.5);

        if (width > 14)
        {
            dc.PushClip(new RectangleGeometry(new Rect(x + 1, TitleTop + 1, width - 2, TitleHeight - 2), 4, 4));
            double textX = x + (selected && width >= 26 ? 10 : 6);
            if (width >= 38)
            {
                dc.DrawRoundedRectangle(TitleTagBg, null, new Rect(textX - 1, TitleTop + 4, 15, 16), 3, 3);
                var tGlyph = Label("T", 10, Paint(0x18, 0x0E, 0x0A), bold: true);
                dc.DrawText(tGlyph, new Point(textX - 1 + (15 - tGlyph.Width) / 2, TitleTop + 4.5));
                textX += 19;
            }
            double avail = x + width - textX - (selected ? 10 : 6);
            if (avail > 8)
            {
                var label = Label(title.Text, 11, TitleText);
                label.MaxTextWidth = avail;
                label.MaxLineCount = 1;
                label.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(label, new Point(textX, TitleTop + (TitleHeight - label.Height) / 2));
            }
            dc.Pop();
        }
        if (selected && width >= 22) DrawTrimHandles(dc, x, TitleTop, width, TitleHeight);
    }

    private void DrawDetachedAudioLayer(DrawingContext dc, AudioLayer layer, bool selected, bool hovered, double viewportLeft, double viewportRight)
    {
        double x = PositionOf(layer.Start), width = Math.Max(6, layer.Duration * Scale - 1.5);
        if (x > viewportRight || x + width < viewportLeft) return;
        var rect = new Rect(x, DetachedTop, width, DetachedHeight);
        dc.DrawRoundedRectangle(hovered ? DetachedHoverBg : DetachedBg, selected ? SelectedPen : DetachedBorderPen, rect, 4.5, 4.5);

        if (width > 10)
        {
            dc.PushClip(new RectangleGeometry(new Rect(x + 1, DetachedTop + 1, width - 2, DetachedHeight - 2), 4, 4));
            var asset = GetAsset(layer.Path);
            DrawWaveform(dc, layer.Path, layer.In, layer.Out, asset?.Duration ?? layer.Out, layer.Volume,
                x, DetachedTop, width, DetachedHeight, DetachedWave);
            DrawFadeRamps(dc, x, DetachedTop, width, DetachedHeight, layer.FadeIn, layer.FadeOut, layer.Duration);

            if (width > 24)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(layer.Path);
                string caption = width > 110 ? $"{name} · {layer.Volume:P0}" : name;
                double leftPad = selected && width >= 26 ? 10 : 4;
                var label = Label(caption, 9.5, DetachedText);
                label.MaxTextWidth = Math.Max(1, width - leftPad - 12);
                label.MaxLineCount = 1;
                label.Trimming = TextTrimming.CharacterEllipsis;
                double bw = Math.Min(width - leftPad - 4, label.Width + 10);
                if (bw > 10)
                {
                    dc.DrawRoundedRectangle(ChipBg, null, new Rect(x + leftPad, DetachedTop + 4, bw, 14.5), 3, 3);
                    dc.DrawText(label, new Point(x + leftPad + 5, DetachedTop + 4.5));
                }
            }
            dc.Pop();
        }
        if (selected && width >= 22) DrawTrimHandles(dc, x, DetachedTop, width, DetachedHeight);
    }

    private void DrawFadeRamps(DrawingContext dc, double x, double top, double width, double height, double fadeIn, double fadeOut, double duration)
    {
        if (duration <= 0) return;
        if (fadeIn > 0.01)
        {
            double fw = Math.Min(width / 2, fadeIn * Scale);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(x, top), true, true);
                ctx.LineTo(new Point(x + fw, top), true, false);
                ctx.LineTo(new Point(x, top + height), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(FadeShade, null, g);
            dc.DrawLine(FadeEdgePen, new Point(x, top + height), new Point(x + fw, top));
        }
        if (fadeOut > 0.01)
        {
            double fw = Math.Min(width / 2, fadeOut * Scale);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(x + width - fw, top), true, true);
                ctx.LineTo(new Point(x + width, top), true, false);
                ctx.LineTo(new Point(x + width, top + height), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(FadeShade, null, g);
            dc.DrawLine(FadeEdgePen, new Point(x + width - fw, top), new Point(x + width, top + height));
        }
    }

    private void DrawWaveform(DrawingContext dc, string path, double clipIn, double clipOut, double totalDuration,
        double volume, double x, double top, double width, double height, Brush waveBrush)
    {
        float[]? peaks = GetWaveform(path);
        double gainScale = Math.Clamp(Math.Sqrt(Math.Max(0, volume)), 0.25, 1.25);
        double barStep = 3.2;
        double barW = 1.6;
        double maxHalfH = (height - 6) * 0.44;
        double midY = top + height / 2 + 1;
        int seed = path.GetHashCode();

        for (double bx = x + 3; bx < x + width - 3; bx += barStep)
        {
            double frac = (bx - x) / Math.Max(1, width);
            double t = clipIn + frac * (clipOut - clipIn);
            float amp;
            if (peaks is { Length: > 0 } && totalDuration > 0)
            {
                int idx = Math.Clamp((int)(t / totalDuration * peaks.Length), 0, peaks.Length - 1);
                amp = peaks[idx];
            }
            else
            {
                double n = Math.Sin(t * 11.3 + (seed & 0xFF)) * 0.35 + Math.Cos(t * 23.7) * 0.25 + 0.45;
                amp = (float)Math.Clamp(n, 0.14, 0.85);
            }
            double halfH = Math.Max(1.5, Math.Min(maxHalfH, amp * gainScale * maxHalfH));
            dc.DrawRectangle(waveBrush, null, new Rect(bx, midY - halfH, barW, halfH * 2));
        }
    }

    private (TimelineSelection Hit, double Left, double Right) HitTestItem(Point point)
    {
        double time = TimeOf(point.X);
        if (point.Y is >= OverlayLaneTop and < VideoLaneTop)
        {
            for (int i = Overlays.Count - 1; i >= 0; i--)
            {
                double l = PositionOf(Overlays[i].Start), r = PositionOf(Overlays[i].End);
                if (point.X >= l && point.X <= r)
                    return (new TimelineSelection(SelectionKind.Overlay, i), l, r);
            }
            return (TimelineSelection.None, 0, 0);
        }
        if (point.Y is >= VideoLaneTop and < TitleLaneTop)
        {
            var hit = FindMainClip(point.X);
            return hit.Index >= 0
                ? (new TimelineSelection(SelectionKind.MainClip, hit.Index), hit.Left, hit.Right)
                : (TimelineSelection.None, 0, 0);
        }
        if (point.Y is >= TitleLaneTop and < DetachedLaneTop)
        {
            for (int i = Titles.Count - 1; i >= 0; i--)
            {
                double l = PositionOf(Titles[i].Start), r = PositionOf(Titles[i].End);
                if (point.X >= l && point.X <= r)
                    return (new TimelineSelection(SelectionKind.Title, i), l, r);
            }
            return (TimelineSelection.None, 0, 0);
        }
        if (point.Y is >= DetachedLaneTop and <= DetachedLaneTop + DetachedLaneHeight)
        {
            for (int i = Audio.Count - 1; i >= 0; i--)
            {
                double l = PositionOf(Audio[i].Start), r = PositionOf(Audio[i].End);
                if (point.X >= l && point.X <= r)
                    return (new TimelineSelection(SelectionKind.Audio, i), l, r);
            }
        }
        _ = time;
        return (TimelineSelection.None, 0, 0);
    }

    private bool IsTrackLocked(SelectionKind kind) => kind switch
    {
        SelectionKind.Overlay => GetTrackFlags("V2").Locked,
        SelectionKind.MainClip => GetTrackFlags("V1").Locked,
        SelectionKind.Title => GetTrackFlags("T1").Locked,
        SelectionKind.Audio => GetTrackFlags("A2").Locked,
        _ => false
    };

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        Focus();
        Point point = e.GetPosition(this);
        var (hit, _, _) = HitTestItem(point);
        if (!hit.IsNone)
        {
            Selection = hit;
            SelectionChanged?.Invoke(hit);
            InvalidateVisual();
        }
        ContextMenuRequested?.Invoke(hit, point);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        Point point = e.GetPosition(this);
        if (point.Y < RulerHeight)
        {
            dragMode = DragMode.Scrub;
            Seeked?.Invoke(TimeOf(point.X));
            CaptureMouse();
            e.Handled = true;
            return;
        }

        var (hit, left, right) = HitTestItem(point);
        Selection = hit;
        SelectionChanged?.Invoke(hit);
        InvalidateVisual();

        if (e.ClickCount == 2 && !hit.IsNone)
        {
            ItemDoubleClicked?.Invoke(hit);
            e.Handled = true;
            return;
        }

        dragStartX = point.X;
        if (!hit.IsNone && !IsTrackLocked(hit.Kind))
        {
            bool nearLeft = right - left >= 20 && point.X - left <= HandleWidth + 1;
            bool nearRight = right - left >= 20 && right - point.X <= HandleWidth + 1;
            dragIndex = hit.Index;
            switch (hit.Kind)
            {
                case SelectionKind.MainClip:
                    if (point.Y <= VideoTop + VideoHeight && (nearLeft || nearRight))
                    {
                        draftMain = Clips[hit.Index];
                        dragMode = nearLeft ? DragMode.TrimMainStart : DragMode.TrimMainEnd;
                    }
                    else
                    {
                        dragMode = DragMode.Scrub;
                        Seeked?.Invoke(TimeOf(point.X));
                    }
                    break;
                case SelectionKind.Overlay:
                    draftOverlay = Overlays[hit.Index];
                    dragMode = nearLeft ? DragMode.TrimOverlayStart : nearRight ? DragMode.TrimOverlayEnd : DragMode.MoveOverlay;
                    break;
                case SelectionKind.Title:
                    draftTitle = Titles[hit.Index];
                    dragMode = nearLeft ? DragMode.TrimTitleStart : nearRight ? DragMode.TrimTitleEnd : DragMode.MoveTitle;
                    break;
                case SelectionKind.Audio:
                    draftAudio = Audio[hit.Index];
                    dragMode = nearLeft ? DragMode.TrimAudioStart : nearRight ? DragMode.TrimAudioEnd : DragMode.MoveAudio;
                    break;
            }
        }
        else
        {
            dragMode = DragMode.Scrub;
            Seeked?.Invoke(TimeOf(point.X));
        }

        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point point = e.GetPosition(this);
        double delta = (point.X - dragStartX) / Scale;
        snapGuideTime = null;

        switch (dragMode)
        {
            case DragMode.Scrub:
                Seeked?.Invoke(TimeOf(point.X));
                return;

            case DragMode.TrimMainStart when draftMain is not null:
            case DragMode.TrimMainEnd when draftMain is not null:
            {
                var orig = Clips[dragIndex];
                double maxDur = GetAsset(orig.Path)?.Duration ?? orig.Out;
                double minSpan = Math.Min(EditTime.MinimumClip * orig.EffectiveSpeed, orig.Out - orig.In);
                double srcDelta = delta * orig.EffectiveSpeed;
                draftMain = dragMode == DragMode.TrimMainStart
                    ? orig with { In = Math.Clamp(orig.In + srcDelta, 0, orig.Out - minSpan) }
                    : orig with { Out = Math.Clamp(orig.Out + srcDelta, orig.In + minSpan, Math.Max(orig.Out, maxDur)) };
                InvalidateVisual();
                return;
            }

            case DragMode.MoveOverlay when draftOverlay is not null:
            {
                var orig = Overlays[dragIndex];
                double rawStart = Math.Max(0, orig.Start + delta);
                double snapped = SnapSpan(rawStart, orig.Duration, SelectionKind.Overlay, dragIndex);
                draftOverlay = orig with { Start = snapped };
                InvalidateVisual();
                return;
            }
            case DragMode.TrimOverlayStart when draftOverlay is not null:
            {
                var orig = Overlays[dragIndex];
                double snappedStart = SnapTime(Math.Clamp(orig.Start + delta, 0, orig.End - EditTime.MinimumClip), SelectionKind.Overlay, dragIndex);
                double shift = snappedStart - orig.Start;
                double nextIn = Math.Clamp(orig.In + shift * orig.EffectiveSpeed, 0, orig.Out - EditTime.MinimumClip * orig.EffectiveSpeed);
                double appliedShift = (nextIn - orig.In) / orig.EffectiveSpeed;
                draftOverlay = orig with { Start = Math.Max(0, orig.Start + appliedShift), In = nextIn };
                InvalidateVisual();
                return;
            }
            case DragMode.TrimOverlayEnd when draftOverlay is not null:
            {
                var orig = Overlays[dragIndex];
                double maxDur = GetAsset(orig.Path)?.Duration ?? orig.Out;
                double snappedEnd = SnapTime(Math.Max(orig.Start + EditTime.MinimumClip, orig.End + delta), SelectionKind.Overlay, dragIndex);
                double nextOut = Math.Clamp(orig.In + (snappedEnd - orig.Start) * orig.EffectiveSpeed,
                    orig.In + EditTime.MinimumClip * orig.EffectiveSpeed, Math.Max(orig.Out, maxDur));
                draftOverlay = orig with { Out = nextOut };
                InvalidateVisual();
                return;
            }

            case DragMode.MoveTitle when draftTitle is not null:
            {
                var orig = Titles[dragIndex];
                double rawStart = Math.Max(0, orig.Start + delta);
                double snapped = SnapSpan(rawStart, orig.Duration, SelectionKind.Title, dragIndex);
                draftTitle = orig with { Start = snapped };
                InvalidateVisual();
                return;
            }
            case DragMode.TrimTitleStart when draftTitle is not null:
            {
                var orig = Titles[dragIndex];
                double newStart = SnapTime(Math.Clamp(orig.Start + delta, 0, orig.End - EditTime.MinimumClip), SelectionKind.Title, dragIndex);
                draftTitle = orig with { Start = newStart, Duration = Math.Max(EditTime.MinimumClip, orig.End - newStart) };
                InvalidateVisual();
                return;
            }
            case DragMode.TrimTitleEnd when draftTitle is not null:
            {
                var orig = Titles[dragIndex];
                double newEnd = SnapTime(Math.Max(orig.Start + EditTime.MinimumClip, orig.End + delta), SelectionKind.Title, dragIndex);
                draftTitle = orig with { Duration = Math.Max(EditTime.MinimumClip, newEnd - orig.Start) };
                InvalidateVisual();
                return;
            }

            case DragMode.MoveAudio when draftAudio is not null:
            {
                var orig = Audio[dragIndex];
                double rawStart = Math.Max(0, orig.Start + delta);
                double snapped = SnapSpan(rawStart, orig.Duration, SelectionKind.Audio, dragIndex);
                draftAudio = orig with { Start = snapped };
                InvalidateVisual();
                return;
            }
            case DragMode.TrimAudioStart when draftAudio is not null:
            {
                var orig = Audio[dragIndex];
                double snappedStart = SnapTime(Math.Clamp(orig.Start + delta, 0, orig.End - EditTime.MinimumClip), SelectionKind.Audio, dragIndex);
                double shift = snappedStart - orig.Start;
                double nextIn = Math.Clamp(orig.In + shift * orig.EffectiveSpeed, 0, orig.Out - EditTime.MinimumClip * orig.EffectiveSpeed);
                double appliedShift = (nextIn - orig.In) / orig.EffectiveSpeed;
                draftAudio = orig with { Start = Math.Max(0, orig.Start + appliedShift), In = nextIn };
                InvalidateVisual();
                return;
            }
            case DragMode.TrimAudioEnd when draftAudio is not null:
            {
                var orig = Audio[dragIndex];
                double maxDur = GetAsset(orig.Path)?.Duration ?? orig.Out;
                double snappedEnd = SnapTime(Math.Max(orig.Start + EditTime.MinimumClip, orig.End + delta), SelectionKind.Audio, dragIndex);
                double nextOut = Math.Clamp(orig.In + (snappedEnd - orig.Start) * orig.EffectiveSpeed,
                    orig.In + EditTime.MinimumClip * orig.EffectiveSpeed, Math.Max(orig.Out, maxDur));
                draftAudio = orig with { Out = nextOut };
                InvalidateVisual();
                return;
            }
        }

        // Hover & Cursor Feedback
        var (hit, left, right) = HitTestItem(point);
        if (hit != hovered)
        {
            hovered = hit;
            InvalidateVisual();
        }
        if (!hit.IsNone && !IsTrackLocked(hit.Kind))
        {
            bool canEdge = right - left >= 20 && (point.X - left <= HandleWidth + 1 || right - point.X <= HandleWidth + 1);
            if (canEdge && (hit.Kind != SelectionKind.MainClip || point.Y <= VideoTop + VideoHeight))
                Cursor = Cursors.SizeWE;
            else if (hit.Kind is SelectionKind.Overlay or SelectionKind.Title or SelectionKind.Audio)
                Cursor = Cursors.SizeAll;
            else
                Cursor = Cursors.Hand;
        }
        else
        {
            Cursor = point.Y < RulerHeight ? Cursors.Hand : Cursors.Arrow;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (!hovered.IsNone)
        {
            hovered = TimelineSelection.None;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var mode = dragMode;
        int idx = dragIndex;
        var pMain = draftMain;
        var pOverlay = draftOverlay;
        var pTitle = draftTitle;
        var pAudio = draftAudio;
        ResetDrag();
        ReleaseMouseCapture();

        if (idx >= 0)
        {
            if (mode is DragMode.TrimMainStart or DragMode.TrimMainEnd && pMain is not null)
                MainClipTrimmed?.Invoke(idx, pMain.In, pMain.Out);
            else if (mode is DragMode.MoveOverlay or DragMode.TrimOverlayStart or DragMode.TrimOverlayEnd && pOverlay is not null)
                OverlayEdited?.Invoke(idx, pOverlay.Start, pOverlay.In, pOverlay.Out);
            else if (mode is DragMode.MoveTitle or DragMode.TrimTitleStart or DragMode.TrimTitleEnd && pTitle is not null)
                TitleEdited?.Invoke(idx, pTitle.Start, pTitle.Duration);
            else if (mode is DragMode.MoveAudio or DragMode.TrimAudioStart or DragMode.TrimAudioEnd && pAudio is not null)
                AudioEdited?.Invoke(idx, pAudio.Start, pAudio.In, pAudio.Out);
        }
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => ResetDrag();

    private void ResetDrag()
    {
        dragMode = DragMode.None;
        dragIndex = -1;
        draftMain = null;
        draftOverlay = null;
        draftTitle = null;
        draftAudio = null;
        snapGuideTime = null;
        InvalidateVisual();
    }

    private double SnapSpan(double rawStart, double length, SelectionKind excludeKind, int excludeIdx)
    {
        double threshold = SnapThresholdPx / Math.Max(1, Scale);
        double bestDist = threshold;
        double bestStart = rawStart;
        double? guide = null;
        foreach (double target in CollectSnapPoints(excludeKind, excludeIdx))
        {
            double dStart = Math.Abs(rawStart - target);
            if (dStart < bestDist) { bestDist = dStart; bestStart = target; guide = target; }
            double dEnd = Math.Abs(rawStart + length - target);
            if (dEnd < bestDist && target >= length) { bestDist = dEnd; bestStart = target - length; guide = target; }
        }
        snapGuideTime = guide;
        return Math.Max(0, bestStart);
    }

    private double SnapTime(double raw, SelectionKind excludeKind, int excludeIdx)
    {
        double threshold = SnapThresholdPx / Math.Max(1, Scale);
        double bestDist = threshold;
        double best = raw;
        double? guide = null;
        foreach (double target in CollectSnapPoints(excludeKind, excludeIdx))
        {
            double d = Math.Abs(raw - target);
            if (d < bestDist) { bestDist = d; best = target; guide = target; }
        }
        snapGuideTime = guide;
        return Math.Max(0, best);
    }

    private IEnumerable<double> CollectSnapPoints(SelectionKind excludeKind, int excludeIdx)
    {
        yield return 0;
        yield return Head;
        double acc = 0;
        for (int i = 0; i < Clips.Count; i++)
        {
            yield return acc;
            acc += Clips[i].Duration;
            yield return acc;
        }
        for (int i = 0; i < Overlays.Count; i++)
        {
            if (excludeKind == SelectionKind.Overlay && excludeIdx == i) continue;
            yield return Overlays[i].Start;
            yield return Overlays[i].End;
        }
        for (int i = 0; i < Titles.Count; i++)
        {
            if (excludeKind == SelectionKind.Title && excludeIdx == i) continue;
            yield return Titles[i].Start;
            yield return Titles[i].End;
        }
        for (int i = 0; i < Audio.Count; i++)
        {
            if (excludeKind == SelectionKind.Audio && excludeIdx == i) continue;
            yield return Audio[i].Start;
            yield return Audio[i].End;
        }
    }

    private (int Index, double Left, double Right) FindMainClip(double x)
    {
        double left = Lead;
        for (int i = 0; i < Clips.Count; i++)
        {
            double right = left + Clips[i].Duration * Scale;
            if (x >= left && x < right) return (i, left, right);
            left = right;
        }
        return (-1, 0, 0);
    }

    private FormattedText Label(string value, double size, Brush brush, bool mono = false, bool bold = false) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            mono ? TimeFont : bold ? BoldFont : NameFont,
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static double NiceTick(double minimum)
    {
        double basis = Math.Pow(10, Math.Floor(Math.Log10(minimum)));
        foreach (double factor in new[] { 1d, 2d, 5d, 10d })
            if (basis * factor >= minimum) return Math.Max(0.2, basis * factor);
        return minimum;
    }

    private static Brush Paint(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Brush PaintA(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Brush MakeVerticalGradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static Pen FrozenDashPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }
}
