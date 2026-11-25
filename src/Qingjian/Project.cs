using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Qingjian;

public sealed record MediaAsset(string Path, double Duration, int Width, int Height,
    int VideoStream, int? AudioStream, string Codec, bool IsImage = false)
{
    public const double DefaultImageDuration = 5.0;
    public const double MaxImageDuration = 300.0;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".avif", ".heic", ".heif"
    };

    public static bool IsImageExtension(string path) => ImageExtensions.Contains(System.IO.Path.GetExtension(path));

    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsAudioOnly => VideoStream < 0;
    public string DurationText => IsImage ? "图片" : EditTime.Display(Duration);
    public string MetaText => VideoStream < 0 ? "本地音频" : IsImage ? $"{Width}×{Height} · 图片" : $"{Width}×{Height}";
    public string Caption => VideoStream < 0 ? $"音频 · {EditTime.Display(Duration)}" :
        IsImage ? $"{Width} × {Height} · 本地图片" : $"{Width} × {Height} · {EditTime.Display(Duration)}";
}

public sealed record CropMargins(int Left, int Top, int Right, int Bottom)
{
    public bool IsZero => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
    public bool IsValid => Left is >= 0 and <= 90 && Top is >= 0 and <= 90 &&
        Right is >= 0 and <= 90 && Bottom is >= 0 and <= 90 &&
        Left + Right <= 90 && Top + Bottom <= 90;
}

public sealed record TransformSpec(double X = 0, double Y = 0, double Scale = 1,
    double Rotation = 0, double Opacity = 1)
{
    public static TransformSpec Default => new();
    public bool IsDefault => Math.Abs(X) < 0.0001 && Math.Abs(Y) < 0.0001 &&
        Math.Abs(Scale - 1) < 0.0001 && Math.Abs(Rotation) < 0.0001 && Math.Abs(Opacity - 1) < 0.0001;
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Scale) &&
        double.IsFinite(Rotation) && double.IsFinite(Opacity) &&
        X is >= -2 and <= 2 && Y is >= -2 and <= 2 &&
        Scale is >= 0.05 and <= 5 && Rotation is >= -360 and <= 360 && Opacity is >= 0 and <= 1;
}

public sealed record TextStyle(string FontFamily = "Microsoft YaHei UI", double FontSize = 44,
    string ColorHex = "#FFFFFF", bool Outline = true, string OutlineColorHex = "#000000",
    double OutlineWidth = 2.5, bool BackgroundBox = false, string BackgroundHex = "#B3000000",
    string Align = "Center", double X = 0, double Y = 0.32)
{
    public static TextStyle Default => new();
    public bool IsValid => !string.IsNullOrWhiteSpace(FontFamily) &&
        double.IsFinite(FontSize) && FontSize is >= 12 and <= 160 &&
        double.IsFinite(OutlineWidth) && OutlineWidth is >= 0 and <= 12 &&
        double.IsFinite(X) && X is >= -0.6 and <= 0.6 &&
        double.IsFinite(Y) && Y is >= -0.6 and <= 0.6;
}

public sealed record TrackFlags(bool Muted = false, bool Hidden = false, bool Locked = false);

public enum SelectionKind { None, MainClip, Overlay, Title, Audio }

public readonly record struct TimelineSelection(SelectionKind Kind, int Index)
{
    public static TimelineSelection None => new(SelectionKind.None, -1);
    public bool IsNone => Kind == SelectionKind.None || Index < 0;
}

public sealed record Clip(string Path, double In, double Out, double Volume = 1,
    CropMargins? Crop = null, double Dissolve = 0, TransformSpec? Transform = null,
    double Speed = 1, double FadeIn = 0, double FadeOut = 0)
{
    public double EffectiveSpeed => double.IsFinite(Speed) && Speed is >= 0.25 and <= 8 ? Speed : 1;
    public double Duration => (Out - In) / EffectiveSpeed;
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public TransformSpec EffectiveTransform => Transform ?? TransformSpec.Default;
}

public sealed record OverlayClip(string Path, double Start, double In, double Out,
    int Track = 0, double Volume = 1, CropMargins? Crop = null, TransformSpec? Transform = null,
    double Speed = 1, double FadeIn = 0, double FadeOut = 0)
{
    public double EffectiveSpeed => double.IsFinite(Speed) && Speed is >= 0.25 and <= 8 ? Speed : 1;
    public double Duration => (Out - In) / EffectiveSpeed;
    public double End => Start + Duration;
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public TransformSpec EffectiveTransform => Transform ?? new TransformSpec(0, 0, 0.45, 0, 1);
}

public sealed record TitleLayer(string Text, double Start, double Duration,
    int Track = 0, TextStyle? Style = null)
{
    public double End => Start + Duration;
    public TextStyle EffectiveStyle => Style ?? TextStyle.Default;
}

public sealed record AudioLayer(string Path, double Start, double In, double Out,
    double Volume = 1, int Track = 0, double Speed = 1, double FadeIn = 0, double FadeOut = 0)
{
    public double EffectiveSpeed => double.IsFinite(Speed) && Speed is >= 0.25 and <= 8 ? Speed : 1;
    public double Duration => (Out - In) / EffectiveSpeed;
    public double End => Start + Duration;
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

public sealed record EditProject(int Version, string[] Assets, Clip[] Clips, string Format,
    TitleLayer[]? Titles = null, AudioLayer[]? Audio = null,
    OverlayClip[]? Overlays = null, Dictionary<string, TrackFlags>? Tracks = null)
{
    public static EditProject Empty => new(3, [], [], "16:9");
    public TitleLayer[] TitleTrack => Titles ?? [];
    public AudioLayer[] AudioTrack => Audio ?? [];
    public OverlayClip[] OverlayTrack => Overlays ?? [];
    public IReadOnlyDictionary<string, TrackFlags> TrackStates => Tracks ?? EmptyTracks;
    private static readonly Dictionary<string, TrackFlags> EmptyTracks = [];

    public TrackFlags GetTrack(string id) => Tracks is not null && Tracks.TryGetValue(id, out var flags) ? flags : new TrackFlags();

    public EditProject WithTrack(string id, TrackFlags flags)
    {
        var next = Tracks is null ? new Dictionary<string, TrackFlags>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, TrackFlags>(Tracks, StringComparer.OrdinalIgnoreCase);
        next[id] = flags;
        return this with { Tracks = next };
    }

    public double Duration => Math.Max(Clips.Sum(clip => clip.Duration),
        Math.Max(OverlayTrack.Select(item => item.End).DefaultIfEmpty().Max(),
            Math.Max(TitleTrack.Select(item => item.End).DefaultIfEmpty().Max(),
                AudioTrack.Select(item => item.End).DefaultIfEmpty().Max())));

    public bool HasComposition => TitleTrack.Length > 0 || AudioTrack.Length > 0 || OverlayTrack.Length > 0 ||
        Clips.Any(clip => clip.Crop is not null || clip.Dissolve > 0 || clip.Transform is { IsDefault: false } ||
            Math.Abs(clip.EffectiveSpeed - 1) > 0.001 || clip.FadeIn > 0 || clip.FadeOut > 0);

    public bool NeedsBakedPreview => Clips.Any(clip => clip.Dissolve > 0 || Math.Abs(clip.EffectiveSpeed - 1) > 0.001);

    public static double TransitionLength(Clip left, Clip right) =>
        Math.Min(left.Dissolve, Math.Min(left.Duration, right.Duration));

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IgnoreReadOnlyProperties = true };

    public static async Task<EditProject> ReadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var project = await JsonSerializer.DeserializeAsync<EditProject>(stream, Json);
        if (project is not { Version: 1 or 2 or 3, Assets: not null, Clips: not null } ||
            project.Format is not ("16:9" or "9:16" or "1:1") ||
            project.Assets.Any(asset => string.IsNullOrWhiteSpace(asset) || !System.IO.Path.IsPathFullyQualified(asset)) ||
            project.Clips.Any(clip => clip is null || !project.Assets.Contains(clip.Path, StringComparer.OrdinalIgnoreCase) ||
                !double.IsFinite(clip.In) || !double.IsFinite(clip.Out) || clip.In < 0 || clip.Out <= clip.In ||
                !double.IsFinite(clip.Volume) || clip.Volume is < 0 or > 2 ||
                clip.Crop is { IsValid: false } || !double.IsFinite(clip.Dissolve) || clip.Dissolve is < 0 or > 2 ||
                clip.Transform is { IsValid: false } ||
                (clip.Speed != 0 && (!double.IsFinite(clip.Speed) || clip.Speed is < 0.25 or > 8)) ||
                !double.IsFinite(clip.FadeIn) || clip.FadeIn < 0 || !double.IsFinite(clip.FadeOut) || clip.FadeOut < 0) ||
            project.OverlayTrack.Any(ov => ov is null || !project.Assets.Contains(ov.Path, StringComparer.OrdinalIgnoreCase) ||
                !double.IsFinite(ov.In) || !double.IsFinite(ov.Out) || ov.In < 0 || ov.Out <= ov.In ||
                !EditTime.ValidSpan(ov.Start, ov.Duration) ||
                !double.IsFinite(ov.Volume) || ov.Volume is < 0 or > 2 ||
                ov.Crop is { IsValid: false } || ov.Transform is { IsValid: false }) ||
            project.TitleTrack.Any(layer => layer is null || string.IsNullOrWhiteSpace(layer.Text) ||
                layer.Text.Length > 240 || !EditTime.ValidSpan(layer.Start, layer.Duration) ||
                layer.Style is { IsValid: false }) ||
            project.AudioTrack.Any(layer => layer is null || !project.Assets.Contains(layer.Path, StringComparer.OrdinalIgnoreCase) ||
                !EditTime.ValidSpan(layer.Start, layer.Duration) || !double.IsFinite(layer.In) || layer.In < 0 ||
                !double.IsFinite(layer.Volume) || layer.Volume is < 0 or > 2))
            throw new InvalidDataException("项目文件格式无效或版本不受支持。");
        return project with { Version = 3 };
    }

    public async Task WriteAsync(string path)
    {
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, this with { Version = 3 }, Json);
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }
}

public sealed record RecentProjectEntry(string Path, string Name, string Format, DateTime LastOpenedUtc,
    double DurationSeconds = 0, string? ThumbnailPath = null, int ClipCount = 0);

public static class RecentProjectsStore
{
    private static readonly string BaseDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qingjian");
    private static readonly string StorePath = System.IO.Path.Combine(BaseDir, "recent.json");
    public static string ThumbDirectory => System.IO.Path.Combine(BaseDir, "thumbs");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<RecentProjectEntry> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return [];
            var items = JsonSerializer.Deserialize<List<RecentProjectEntry>>(File.ReadAllText(StorePath), Json) ?? [];
            return items.Where(e => !string.IsNullOrWhiteSpace(e.Path) && File.Exists(e.Path))
                .OrderByDescending(e => e.LastOpenedUtc).Take(24).ToList();
        }
        catch { return []; }
    }

    public static void Record(string path, string format, double durationSeconds = 0, string? thumbnailPath = null, int clipCount = 0)
    {
        try
        {
            string full = System.IO.Path.GetFullPath(path);
            var existing = Load().FirstOrDefault(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase));
            string? thumb = !string.IsNullOrWhiteSpace(thumbnailPath) && File.Exists(thumbnailPath)
                ? thumbnailPath
                : existing?.ThumbnailPath;
            double dur = durationSeconds > 0 ? durationSeconds : existing?.DurationSeconds ?? 0;
            int clips = clipCount > 0 ? clipCount : existing?.ClipCount ?? 0;

            var list = Load().Where(e => !string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Insert(0, new RecentProjectEntry(full, System.IO.Path.GetFileNameWithoutExtension(full), format, DateTime.UtcNow, dur, thumb, clips));
            if (list.Count > 24) list = list.Take(24).ToList();
            Directory.CreateDirectory(BaseDir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(list, Json));
        }
        catch { }
    }

    public static void Remove(string path)
    {
        try
        {
            string full = System.IO.Path.GetFullPath(path);
            var list = Load().Where(e => !string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase)).ToList();
            Directory.CreateDirectory(BaseDir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(list, Json));
        }
        catch { }
    }
}

public static class EditTime
{
    public const double MinimumClip = 0.04;

    public static bool ValidSpan(double start, double duration) => double.IsFinite(start) && start >= 0 &&
        double.IsFinite(duration) && duration >= MinimumClip && start + duration <= 24 * 3600;

    public static string Display(double seconds, bool milliseconds = false)
    {
        long ms = (long)Math.Round(Math.Max(0, seconds) * 1000);
        string value = $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}";
        return milliseconds ? $"{value}.{ms % 1000:000}" : value;
    }

    public static bool TryParse(string text, out double seconds)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            return double.IsFinite(seconds) && seconds >= 0;
        if (!TimeSpan.TryParseExact(text.Trim(), [@"h\:mm\:ss\.FFF", @"hh\:mm\:ss\.FFF", @"h\:mm\:ss", @"hh\:mm\:ss"],
                CultureInfo.InvariantCulture, out var time)) return false;
        seconds = time.TotalSeconds;
        return seconds >= 0;
    }
}
