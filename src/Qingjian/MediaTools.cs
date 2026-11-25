using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Qingjian;

public enum ExportCodec
{
    H264,
    Hevc,
    Hevc422,
    Av1,
    ProResProxy,
    ProResLt,
    ProRes422,
    ProRes422Hq,
    ProRes4444,
    Rle
}

public enum ExportContainer { Mp4, Mov, Mxf }
public enum BitratePreset { Low, Recommended, High, Custom }
public enum RateControlMode { Vbr, Cbr }
public enum ExportColorSpace { Rec709Sdr, Rec2020Sdr, Rec2100Hlg, Rec2100Pq }
public enum VideoAudioQuality { Aac192, Aac256, Aac320, PcmLossless }
public enum AudioExportFormat { Mp3, Wav, Aac, Flac, Aiff, Ogg }
public enum SubtitleExportFormat { Srt, Txt }
public enum ExportScope { FullTimeline, Range, SelectedClip }
public enum StillFrameFormat { Png, Jpg, Webp, Heic, Bmp, Tiff, Avif }

public sealed record ExportOptions(int ShortEdge, int Fps, string Format, bool Hardware,
    ExportCodec Codec = ExportCodec.H264, int? BitrateKbps = null)
{
    public const int AudioBitrateKbps = 192;

    public ExportContainer Container { get; init; } = ExportContainer.Mp4;
    public double? ExactFps { get; init; }
    public BitratePreset BitratePreset { get; init; } = BitratePreset.Recommended;
    public RateControlMode RateControl { get; init; } = RateControlMode.Vbr;
    public ExportColorSpace ColorSpace { get; init; } = ExportColorSpace.Rec709Sdr;
    public bool BitDepth10 { get; init; }
    public bool FastStart { get; init; } = true;
    public VideoAudioQuality AudioQuality { get; init; } = VideoAudioQuality.Aac192;
    public int AudioSampleRate { get; init; } = 48000;
    public string? CoverImagePath { get; init; }
    public double? RangeStart { get; init; }
    public double? RangeEnd { get; init; }
    public bool StripEffects { get; init; }

    public (int Width, int Height) Size
    {
        get
        {
            int shortEven = Math.Max(2, ShortEdge & ~1);
            int longEdge = Math.Max(2, ((int)Math.Round(shortEven * 16.0 / 9.0)) & ~1);
            return Format switch
            {
                "9:16" => (shortEven, longEdge),
                "1:1" => (shortEven, shortEven),
                _ => (longEdge, shortEven)
            };
        }
    }

    public string Extension => Container switch
    {
        ExportContainer.Mov => ".mov",
        ExportContainer.Mxf => ".mxf",
        _ => ".mp4"
    };

    public double EffectiveFpsValue => ExactFps is > 1 ? ExactFps.Value : Math.Max(1, Fps);

    public string FpsExpr => ExactFps switch
    {
        double f when Math.Abs(f - 29.97) < 0.02 => "30000/1001",
        double f when Math.Abs(f - 59.94) < 0.02 => "60000/1001",
        double f when f > 1 => f.ToString("0.###", CultureInfo.InvariantCulture),
        _ => Math.Max(1, Fps).ToString(CultureInfo.InvariantCulture)
    };

    public bool IsProRes => Codec is ExportCodec.ProResProxy or ExportCodec.ProResLt or ExportCodec.ProRes422
        or ExportCodec.ProRes422Hq or ExportCodec.ProRes4444;

    public bool Effective10Bit => BitDepth10 || ColorSpace != ExportColorSpace.Rec709Sdr || Codec == ExportCodec.Hevc422 || IsProRes;

    public int EffectiveAudioBitrateKbps => AudioQuality switch
    {
        VideoAudioQuality.Aac256 => 256,
        VideoAudioQuality.Aac320 => 320,
        VideoAudioQuality.PcmLossless => AudioSampleRate * 2 * 16 / 1000,
        _ => 192
    };

    public int EstimatedVideoBitrateKbps()
    {
        if (BitrateKbps is int custom) return custom;
        if (IsProRes)
        {
            double resScale = (Size.Width * (double)Size.Height) / (1920.0 * 1080.0) * (EffectiveFpsValue / 30.0);
            int baseRate = Codec switch
            {
                ExportCodec.ProResProxy => 45_000,
                ExportCodec.ProResLt => 102_000,
                ExportCodec.ProRes422 => 147_000,
                ExportCodec.ProRes422Hq => 220_000,
                ExportCodec.ProRes4444 => 330_000,
                _ => 147_000
            };
            return (int)Math.Clamp(baseRate * resScale, 15_000, 900_000);
        }
        if (Codec == ExportCodec.Rle) return 180_000;

        int rec = ShortEdge switch
        {
            <= 480 => 2500,
            <= 720 => 5000,
            <= 1080 => 10000,
            <= 1440 => 18000,
            <= 2160 => 35000,
            _ => 80000
        };
        if (EffectiveFpsValue >= 48) rec = (int)(rec * 1.4);
        if (Codec is ExportCodec.Hevc or ExportCodec.Hevc422 or ExportCodec.Av1) rec = (int)(rec * 0.78);
        return BitratePreset switch
        {
            BitratePreset.Low => Math.Max(800, (int)(rec * 0.55)),
            BitratePreset.High => (int)(rec * 1.6),
            _ => rec
        };
    }

    public int? TargetVideoBitrateKbps()
    {
        if (IsProRes || Codec == ExportCodec.Rle) return null;
        if (BitrateKbps is int custom) return custom;
        if (BitratePreset is BitratePreset.Low or BitratePreset.High || RateControl == RateControlMode.Cbr)
            return EstimatedVideoBitrateKbps();
        return null;
    }

    public static string CodecDisplayName(ExportCodec codec) => codec switch
    {
        ExportCodec.H264 => "H.264",
        ExportCodec.Hevc => "HEVC (H.265)",
        ExportCodec.Hevc422 => "HEVC 4:2:2 10-bit",
        ExportCodec.Av1 => "AV1",
        ExportCodec.ProResProxy => "Apple ProRes Proxy",
        ExportCodec.ProResLt => "Apple ProRes 422 LT",
        ExportCodec.ProRes422 => "Apple ProRes 422",
        ExportCodec.ProRes422Hq => "Apple ProRes 422 HQ",
        ExportCodec.ProRes4444 => "Apple ProRes 4444",
        ExportCodec.Rle => "QuickTime RLE",
        _ => codec.ToString()
    };

    public static string AudioExtension(AudioExportFormat format) => format switch
    {
        AudioExportFormat.Mp3 => ".mp3",
        AudioExportFormat.Wav => ".wav",
        AudioExportFormat.Aac => ".m4a",
        AudioExportFormat.Flac => ".flac",
        AudioExportFormat.Aiff => ".aiff",
        AudioExportFormat.Ogg => ".ogg",
        _ => ".mp3"
    };

    public static string SubtitleExtension(SubtitleExportFormat format) => format == SubtitleExportFormat.Txt ? ".txt" : ".srt";

    public static string ImageExtension(StillFrameFormat format) => format switch
    {
        StillFrameFormat.Jpg => ".jpg",
        StillFrameFormat.Webp => ".webp",
        StillFrameFormat.Heic => ".heic",
        StillFrameFormat.Bmp => ".bmp",
        StillFrameFormat.Tiff => ".tiff",
        StillFrameFormat.Avif => ".avif",
        _ => ".png"
    };
}

public sealed record ExportBatchPlan(
    string BaseName,
    string OutputDirectory,
    bool ReplaceExisting,
    ExportScope Scope,
    bool IncludeEffectsInClipExport,
    bool ExportVideo,
    ExportOptions VideoOptions,
    bool ExportAudio,
    AudioExportFormat AudioFormat,
    int AudioBitrateKbps,
    int AudioSampleRate,
    bool ExportSubtitle,
    SubtitleExportFormat SubtitleFormat,
    bool ExportGif,
    int GifShortEdge,
    int GifFps)
{
    public IReadOnlyList<(string Kind, string Path)> PlannedOutputs()
    {
        var list = new List<(string, string)>();
        if (ExportVideo) list.Add(("视频", System.IO.Path.GetFullPath(System.IO.Path.Combine(OutputDirectory, BaseName + VideoOptions.Extension))));
        if (ExportAudio) list.Add(("音频", System.IO.Path.GetFullPath(System.IO.Path.Combine(OutputDirectory, BaseName + ExportOptions.AudioExtension(AudioFormat)))));
        if (ExportSubtitle) list.Add(("字幕", System.IO.Path.GetFullPath(System.IO.Path.Combine(OutputDirectory, BaseName + ExportOptions.SubtitleExtension(SubtitleFormat)))));
        if (ExportGif) list.Add(("GIF", System.IO.Path.GetFullPath(System.IO.Path.Combine(OutputDirectory, BaseName + ".gif"))));
        return list;
    }
}

public sealed record ExportBatchResult(IReadOnlyList<string> CreatedFiles, string? VideoEncoderUsed);

public sealed class MediaTools
{
    private string? directory;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static string Seconds(double value) => value.ToString("0.########", Invariant);

    public bool Available => directory is not null;

    public MediaTools()
    {
        var paths = new[] { Environment.GetEnvironmentVariable("QINGJIAN_FFMPEG"), AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "ffmpeg") };
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        var candidates = paths.Concat(pathVariable.Split(Path.PathSeparator));
        directory = candidates.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Trim('"')).FirstOrDefault(ContainsTools);
    }

    private static bool ContainsTools(string path) => File.Exists(Path.Combine(path, "ffmpeg.exe")) &&
        File.Exists(Path.Combine(path, "ffprobe.exe"));

    public bool UseDirectory(string path)
    {
        if (!ContainsTools(path)) return false;
        directory = path;
        return true;
    }

    public async Task<MediaAsset> ProbeAsync(string path, CancellationToken ct, bool allowAudioOnly = false)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到素材文件。", path);
        var output = await RunAsync("ffprobe", ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_streams",
            "-show_format", "-of", "json", "-i", path], ct);
        using var data = JsonDocument.Parse(output);
        var streams = data.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(stream => Text(stream, "codec_type") == "video" &&
            !(stream.TryGetProperty("disposition", out var disposition) && Number(disposition, "attached_pic") == 1));
        var audio = streams.FirstOrDefault(stream => Text(stream, "codec_type") == "audio");
        if (video.ValueKind == JsonValueKind.Undefined)
        {
            if (!allowAudioOnly || audio.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("文件中没有可用的视频轨道。");
            double audioDuration = Number(audio, "duration");
            if (audioDuration <= 0) audioDuration = Number(data.RootElement.GetProperty("format"), "duration");
            if (!double.IsFinite(audioDuration) || audioDuration <= 0) throw new InvalidDataException("无法读取音频时长。");
            return new MediaAsset(path, audioDuration, 0, 0, -1, (int)Number(audio, "index"), Text(audio, "codec_name"));
        }
        string videoCodec = Text(video, "codec_name");
        int width = (int)Number(video, "width"), height = (int)Number(video, "height");
        if (video.TryGetProperty("side_data_list", out var sideData) &&
            sideData.EnumerateArray().Any(side => Math.Abs(Number(side, "rotation")) % 180 is > 45 and < 135))
            (width, height) = (height, width);

        double duration = Number(video, "duration");
        if (duration <= 0) duration = Number(data.RootElement.GetProperty("format"), "duration");
        bool isImage = MediaAsset.IsImageExtension(path) ||
            (audio.ValueKind == JsonValueKind.Undefined &&
             videoCodec is "png" or "mjpeg" or "webp" or "bmp" or "tiff" or "hevc" or "av1" &&
             (!double.IsFinite(duration) || duration <= 0.05));
        if (isImage)
        {
            if (width <= 0 || height <= 0) throw new InvalidDataException("无法读取图片尺寸。");
            return new MediaAsset(path, MediaAsset.MaxImageDuration, width, height, (int)Number(video, "index"), null, videoCodec, IsImage: true);
        }
        if (!double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("无法读取视频时长。");
        return new MediaAsset(path, duration, width, height, (int)Number(video, "index"),
            audio.ValueKind == JsonValueKind.Undefined ? null : (int)Number(audio, "index"), videoCodec);
    }

    public async Task ThumbnailAsync(MediaAsset asset, string path, CancellationToken ct, double? timestamp = null)
    {
        string size = timestamp is null ? "240:144" : "640:640";
        var args = new List<string> { "-v", "error", "-nostdin", "-y" };
        if (!asset.IsImage)
            args.AddRange(["-ss", Seconds(timestamp ?? Math.Min(1, asset.Duration / 4))]);
        args.AddRange(["-protocol_whitelist", "file,pipe", "-i", asset.Path, "-map", $"0:{asset.VideoStream}",
            "-an", "-frames:v", "1", "-vf", $"scale={size}:force_original_aspect_ratio=decrease:reset_sar=1",
            "-q:v", "5", "-update", "1", path]);
        await RunAsync("ffmpeg", args, ct);
    }

    public async Task DecodeImagePreviewAsync(MediaAsset asset, string pngOutput, CancellationToken ct, int maxEdge = 1920)
    {
        await RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe",
            "-i", asset.Path, "-map", $"0:{asset.VideoStream}", "-an", "-frames:v", "1",
            "-vf", $"scale={maxEdge}:{maxEdge}:force_original_aspect_ratio=decrease:reset_sar=1",
            "-c:v", "png", "-update", "1", pngOutput], ct);
    }

    public async Task<float[]?> WaveformAsync(MediaAsset asset, CancellationToken ct, int buckets = 180)
    {
        if (asset.AudioStream is not int stream || directory is null || asset.Duration <= 0) return null;
        try
        {
            var start = new ProcessStartInfo(Path.Combine(directory, "ffmpeg.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { "-v", "error", "-nostdin", "-protocol_whitelist", "file,pipe", "-i", asset.Path,
                "-map", $"0:{stream}", "-vn", "-ac", "1", "-ar", "8000", "-t", "600", "-f", "s16le", "pipe:1" })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return null;
            using var reg = ct.Register(() => { try { process.Kill(true); } catch { } });
            using var ms = new MemoryStream();
            var errTask = process.StandardError.ReadToEndAsync(ct);
            await process.StandardOutput.BaseStream.CopyToAsync(ms, ct);
            await process.WaitForExitAsync(ct);
            await errTask;
            if (process.ExitCode != 0 || ms.Length < 4) return null;
            byte[] raw = ms.ToArray();
            int totalSamples = raw.Length / 2;
            int count = Math.Min(buckets, totalSamples);
            if (count <= 0) return null;
            var peaks = new float[count];
            for (int b = 0; b < count; b++)
            {
                int from = (int)((long)b * totalSamples / count);
                int to = Math.Max(from + 1, (int)((long)(b + 1) * totalSamples / count));
                double sumSq = 0;
                float peak = 0;
                int n = 0;
                for (int s = from; s < to && s < totalSamples; s++)
                {
                    short v = BitConverter.ToInt16(raw, s * 2);
                    float a = Math.Abs(v == short.MinValue ? 1f : v / 32768f);
                    sumSq += a * a;
                    if (a > peak) peak = a;
                    n++;
                }
                float rms = n > 0 ? (float)Math.Sqrt(sumSq / n) : 0f;
                peaks[b] = peak * 0.55f + rms * 0.45f;
            }
            float[] sorted = [.. peaks];
            Array.Sort(sorted);
            float refPeak = Math.Max(0.01f, sorted[(int)((sorted.Length - 1) * 0.96)]);
            for (int i = 0; i < count; i++) peaks[i] = Math.Clamp(peaks[i] / refPeak, 0.08f, 1f);
            return peaks;
        }
        catch { return null; }
    }

    public async Task ProxyAsync(MediaAsset asset, string path, CancellationToken ct)
    {
        var args = new List<string> { "-v", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-i", asset.Path,
            "-map", $"0:{asset.VideoStream}" };
        if (asset.AudioStream is int audio) args.AddRange(["-map", $"0:{audio}", "-c:a", "aac", "-b:a", "128k", "-ac", "2"]);
        args.AddRange(["-vf", "scale=960:960:force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-pix_fmt", "yuv420p", "-movflags", "+faststart", path]);
        await RunAsync("ffmpeg", args, ct);
    }

    public async Task<ExportBatchResult> ExportBatchAsync(
        IReadOnlyList<(Clip Clip, MediaAsset Asset)> clips,
        ExportBatchPlan plan,
        IProgress<double> progress,
        CancellationToken ct,
        IReadOnlyList<TitleLayer>? titles = null,
        IReadOnlyList<(AudioLayer Layer, MediaAsset Asset)>? audioLayers = null,
        IReadOnlyList<(OverlayClip Clip, MediaAsset Asset)>? overlays = null,
        IReadOnlyDictionary<string, TrackFlags>? trackStates = null)
    {
        var tasks = new List<(string Kind, double Weight)>();
        if (plan.ExportVideo) tasks.Add(("video", 0.68));
        if (plan.ExportGif) tasks.Add(("gif", 0.18));
        if (plan.ExportAudio) tasks.Add(("audio", 0.12));
        if (plan.ExportSubtitle) tasks.Add(("subtitle", 0.02));
        if (tasks.Count == 0) throw new InvalidOperationException("请至少勾选一项导出内容。");

        double totalWeight = tasks.Sum(t => t.Weight);
        double doneWeight = 0;
        var created = new List<string>();
        string? videoEncoder = null;

        IProgress<double> SubProgress(double w) => new Progress<double>(p =>
            progress.Report(Math.Clamp((doneWeight + Math.Clamp(p, 0, 1) * w) / totalWeight, 0, 0.999)));

        foreach (var (kind, weight) in tasks)
        {
            ct.ThrowIfCancellationRequested();
            if (kind == "video")
            {
                string path = Path.GetFullPath(Path.Combine(plan.OutputDirectory, plan.BaseName + plan.VideoOptions.Extension));
                videoEncoder = await ExportAsync(clips, plan.VideoOptions, path, SubProgress(weight), ct,
                    plan.ReplaceExisting, titles, audioLayers, overlays, trackStates);
                created.Add(path);
            }
            else if (kind == "audio")
            {
                string path = Path.GetFullPath(Path.Combine(plan.OutputDirectory, plan.BaseName + ExportOptions.AudioExtension(plan.AudioFormat)));
                await ExportAudioAsync(clips, plan.AudioFormat, plan.AudioBitrateKbps, plan.AudioSampleRate,
                    plan.VideoOptions.RangeStart, plan.VideoOptions.RangeEnd, path, SubProgress(weight), ct,
                    plan.ReplaceExisting, titles, audioLayers, overlays, trackStates);
                created.Add(path);
            }
            else if (kind == "subtitle")
            {
                string path = Path.GetFullPath(Path.Combine(plan.OutputDirectory, plan.BaseName + ExportOptions.SubtitleExtension(plan.SubtitleFormat)));
                await ExportSubtitleAsync(titles ?? [], plan.SubtitleFormat, path, ct,
                    plan.VideoOptions.RangeStart, plan.VideoOptions.RangeEnd, plan.ReplaceExisting);
                created.Add(path);
            }
            else if (kind == "gif")
            {
                string path = Path.GetFullPath(Path.Combine(plan.OutputDirectory, plan.BaseName + ".gif"));
                var gifOpt = plan.VideoOptions with { ShortEdge = plan.GifShortEdge, Fps = plan.GifFps, ExactFps = null };
                await ExportGifAsync(clips, gifOpt, path, SubProgress(weight), ct,
                    plan.ReplaceExisting, titles, overlays, trackStates);
                created.Add(path);
            }
            doneWeight += weight;
        }
        progress.Report(1);
        return new ExportBatchResult(created, videoEncoder);
    }

    public async Task<string> ExportAsync(IReadOnlyList<(Clip Clip, MediaAsset Asset)> clips, ExportOptions options,
        string output, IProgress<double> progress, CancellationToken ct, bool overwrite = true,
        IReadOnlyList<TitleLayer>? titles = null, IReadOnlyList<(AudioLayer Layer, MediaAsset Asset)>? audioLayers = null,
        IReadOnlyList<(OverlayClip Clip, MediaAsset Asset)>? overlays = null,
        IReadOnlyDictionary<string, TrackFlags>? trackStates = null)
    {
        if (clips.Count == 0) throw new InvalidOperationException("时间线还没有视频片段。");
        titles ??= [];
        audioLayers ??= [];
        overlays ??= [];

        string extension = Path.GetExtension(output);
        if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mxf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择 .mp4、.mov 或 .mxf 文件名。");

        EnsureNotSourcePath(output, clips, overlays, audioLayers);

        ExportContainer container = extension.ToLowerInvariant() switch
        {
            ".mov" => ExportContainer.Mov,
            ".mxf" => ExportContainer.Mxf,
            _ => ExportContainer.Mp4
        };

        var effectiveOptions = options with { Container = container };
        string temp = Path.Combine(Path.GetDirectoryName(output)!, $".qingjian-{Guid.NewGuid():N}{extension}");
        string work = Path.Combine(Path.GetTempPath(), $"qingjian-render-{Guid.NewGuid():N}");
        string graphPath = Path.Combine(work, "timeline.filters");

        int sampleRate = container == ExportContainer.Mxf ? 48000 : (effectiveOptions.AudioSampleRate is 44100 ? 44100 : 48000);
        var built = BuildTimelineFilterGraph(clips, effectiveOptions, titles, audioLayers, overlays, trackStates,
            includeVideo: true, includeAudio: true, sampleRate: sampleRate);

        var args = new List<string>(built.InputArgs);
        bool attachCover = !string.IsNullOrWhiteSpace(effectiveOptions.CoverImagePath) &&
            File.Exists(effectiveOptions.CoverImagePath) && container != ExportContainer.Mxf;
        int coverInputIdx = built.NextInputIndex;
        if (attachCover)
            args.AddRange(["-protocol_whitelist", "file,pipe", "-i", effectiveOptions.CoverImagePath!]);

        args.AddRange(["-/filter_complex", graphPath, "-map", "[vout]", "-map", "[aout]"]);
        if (attachCover)
            args.AddRange(["-map", $"{coverInputIdx}:v:0"]);

        args.AddRange(["-fps_mode", "cfr", "-r:v:0", effectiveOptions.FpsExpr, "-t", Seconds(built.OutputDuration),
            "-map_metadata", "-1", "-map_chapters", "-1"]);

        // Audio codec args
        bool usePcm = container == ExportContainer.Mxf || effectiveOptions.AudioQuality == VideoAudioQuality.PcmLossless;
        if (usePcm && container == ExportContainer.Mp4) usePcm = false; // MP4 standard audio -> AAC
        if (usePcm)
            args.AddRange(["-c:a", "pcm_s16le", "-ar", sampleRate.ToString(Invariant), "-ac", "2"]);
        else
            args.AddRange(["-c:a", "aac", "-b:a", $"{effectiveOptions.EffectiveAudioBitrateKbps}k",
                "-ar", sampleRate.ToString(Invariant), "-ac", "2"]);

        if (effectiveOptions.FastStart && container != ExportContainer.Mxf)
            args.AddRange(["-movflags", "+faststart"]);

        if (effectiveOptions.Codec is ExportCodec.Hevc or ExportCodec.Hevc422 && container != ExportContainer.Mxf)
            args.AddRange(["-tag:v:0", "hvc1"]);

        // Color metadata tags
        args.AddRange(ColorSpaceArguments(effectiveOptions.ColorSpace));

        string[] coverArgs = attachCover ? ["-c:v:1", "mjpeg", "-disposition:v:1", "attached_pic"] : [];

        string softwareEncoder = SoftwareEncoderFor(effectiveOptions.Codec);
        string encoder = softwareEncoder;
        try
        {
            Directory.CreateDirectory(work);
            await PrepareTitleFilesAsync(built.ActiveTitles, work, ct);
            await File.WriteAllTextAsync(graphPath, built.FilterGraph, new UTF8Encoding(false), ct);

            if (effectiveOptions.Hardware && SupportsHardware(effectiveOptions.Codec, effectiveOptions.Effective10Bit))
                encoder = await FindEncoderAsync(effectiveOptions.Codec, effectiveOptions.Effective10Bit, softwareEncoder, ct);

            void Report(string line)
            {
                const string prefix = "out_time_us=";
                if (line.StartsWith(prefix, StringComparison.Ordinal) && long.TryParse(line.AsSpan(prefix.Length), out long micros))
                    progress.Report(Math.Clamp(micros / 1_000_000d / built.OutputDuration, 0, 0.995));
            }

            string[] BuildEncArgs(string enc) => [.. EncoderArguments(enc, effectiveOptions), .. coverArgs];
            try { await RunAsync("ffmpeg", [.. args, .. BuildEncArgs(encoder), temp], ct, Report, work); }
            catch (MediaException) when (encoder != softwareEncoder)
            {
                encoder = softwareEncoder;
                progress.Report(0);
                await RunAsync("ffmpeg", [.. args, .. BuildEncArgs(encoder), temp], ct, Report, work);
            }
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(temp) || new FileInfo(temp).Length < 256) throw new IOException("编码器未生成有效的视频文件。");
            File.Move(temp, output, overwrite);
            progress.Report(1);
            return encoder;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    public async Task ExportAudioAsync(
        IReadOnlyList<(Clip Clip, MediaAsset Asset)> clips,
        AudioExportFormat format, int bitrateKbps, int sampleRate,
        double? rangeStart, double? rangeEnd,
        string output, IProgress<double> progress, CancellationToken ct, bool overwrite = true,
        IReadOnlyList<TitleLayer>? titles = null,
        IReadOnlyList<(AudioLayer Layer, MediaAsset Asset)>? audioLayers = null,
        IReadOnlyList<(OverlayClip Clip, MediaAsset Asset)>? overlays = null,
        IReadOnlyDictionary<string, TrackFlags>? trackStates = null)
    {
        if (clips.Count == 0 && (audioLayers is null || audioLayers.Count == 0))
            throw new InvalidOperationException("时间线没有可导出的音频内容。");
        EnsureNotSourcePath(output, clips, overlays ?? [], audioLayers ?? []);

        int sr = sampleRate is 44100 ? 44100 : 48000;
        var opt = new ExportOptions(720, 30, "16:9", false) { RangeStart = rangeStart, RangeEnd = rangeEnd, AudioSampleRate = sr };
        string ext = ExportOptions.AudioExtension(format);
        string temp = Path.Combine(Path.GetDirectoryName(output)!, $".qingjian-aud-{Guid.NewGuid():N}{ext}");
        string work = Path.Combine(Path.GetTempPath(), $"qingjian-aud-{Guid.NewGuid():N}");
        string graphPath = Path.Combine(work, "audio.filters");

        var built = BuildTimelineFilterGraph(clips, opt, titles ?? [], audioLayers ?? [], overlays ?? [], trackStates,
            includeVideo: false, includeAudio: true, sampleRate: sr);

        var args = new List<string>(built.InputArgs);
        args.AddRange(["-/filter_complex", graphPath, "-map", "[aout]", "-vn", "-t", Seconds(built.OutputDuration),
            "-map_metadata", "-1", "-ar", sr.ToString(Invariant), "-ac", "2"]);

        string br = $"{Math.Clamp(bitrateKbps, 64, 512)}k";
        args.AddRange(format switch
        {
            AudioExportFormat.Mp3 => ["-c:a", "libmp3lame", "-b:a", br],
            AudioExportFormat.Wav => ["-c:a", "pcm_s16le"],
            AudioExportFormat.Aac => ["-c:a", "aac", "-b:a", br, "-movflags", "+faststart"],
            AudioExportFormat.Flac => ["-c:a", "flac"],
            AudioExportFormat.Aiff => ["-c:a", "pcm_s16be"],
            AudioExportFormat.Ogg => ["-c:a", "libvorbis", "-b:a", br],
            _ => ["-c:a", "libmp3lame", "-b:a", br]
        });

        try
        {
            Directory.CreateDirectory(work);
            await File.WriteAllTextAsync(graphPath, built.FilterGraph, new UTF8Encoding(false), ct);
            void Report(string line)
            {
                const string prefix = "out_time_us=";
                if (line.StartsWith(prefix, StringComparison.Ordinal) && long.TryParse(line.AsSpan(prefix.Length), out long micros))
                    progress.Report(Math.Clamp(micros / 1_000_000d / built.OutputDuration, 0, 0.995));
            }
            await RunAsync("ffmpeg", [.. args, temp], ct, Report, work);
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(temp) || new FileInfo(temp).Length < 64) throw new IOException("未生成有效的音频文件。");
            File.Move(temp, output, overwrite);
            progress.Report(1);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    public async Task ExportSubtitleAsync(
        IReadOnlyList<TitleLayer> titles,
        SubtitleExportFormat format,
        string output,
        CancellationToken ct,
        double? rangeStart = null,
        double? rangeEnd = null,
        bool overwrite = true)
    {
        double startBound = Math.Max(0, rangeStart ?? 0);
        double endBound = rangeEnd is > 0 ? rangeEnd.Value : double.PositiveInfinity;
        var active = titles
            .Where(t => !string.IsNullOrWhiteSpace(t.Text) && t.End > startBound && t.Start < endBound)
            .OrderBy(t => t.Start)
            .ToArray();
        if (active.Length == 0) throw new InvalidOperationException("当前导出范围内没有文字或字幕片段。");

        var sb = new StringBuilder();
        for (int i = 0; i < active.Length; i++)
        {
            double s = Math.Max(0, Math.Max(active[i].Start, startBound) - startBound);
            double e = Math.Max(s + 0.1, Math.Min(active[i].End, endBound) - startBound);
            if (format == SubtitleExportFormat.Srt)
            {
                sb.AppendLine((i + 1).ToString(Invariant));
                sb.AppendLine($"{FormatSrtTime(s)} --> {FormatSrtTime(e)}");
                sb.AppendLine(active[i].Text.Trim());
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine(active[i].Text.Trim());
            }
        }

        string temp = Path.Combine(Path.GetDirectoryName(output)!, $".qingjian-sub-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, sb.ToString(), new UTF8Encoding(false), ct);
            File.Move(temp, output, overwrite);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public async Task ExportGifAsync(
        IReadOnlyList<(Clip Clip, MediaAsset Asset)> clips,
        ExportOptions options,
        string output, IProgress<double> progress, CancellationToken ct, bool overwrite = true,
        IReadOnlyList<TitleLayer>? titles = null,
        IReadOnlyList<(OverlayClip Clip, MediaAsset Asset)>? overlays = null,
        IReadOnlyDictionary<string, TrackFlags>? trackStates = null)
    {
        if (clips.Count == 0) throw new InvalidOperationException("时间线还没有视频片段。");
        EnsureNotSourcePath(output, clips, overlays ?? [], []);

        string temp = Path.Combine(Path.GetDirectoryName(output)!, $".qingjian-gif-{Guid.NewGuid():N}.gif");
        string work = Path.Combine(Path.GetTempPath(), $"qingjian-gif-{Guid.NewGuid():N}");
        string graphPath = Path.Combine(work, "gif.filters");

        var built = BuildTimelineFilterGraph(clips, options, titles ?? [], [], overlays ?? [], trackStates,
            includeVideo: true, includeAudio: false, sampleRate: 48000);
        if (built.OutputDuration > 60)
            throw new InvalidOperationException("GIF 导出建议不超过 60 秒，请缩短范围（使用入出点选区导出）。");

        string gifGraph = built.FilterGraph +
            ";\n[vout]split[g0][g1];[g0]palettegen=stats_mode=diff[gpal];[g1][gpal]paletteuse=dither=bayer:bayer_scale=3[gifout]";

        var args = new List<string>(built.InputArgs);
        args.AddRange(["-/filter_complex", graphPath, "-map", "[gifout]", "-an", "-r", options.FpsExpr,
            "-t", Seconds(built.OutputDuration), "-loop", "0", temp]);

        try
        {
            Directory.CreateDirectory(work);
            await PrepareTitleFilesAsync(built.ActiveTitles, work, ct);
            await File.WriteAllTextAsync(graphPath, gifGraph, new UTF8Encoding(false), ct);
            void Report(string line)
            {
                const string prefix = "out_time_us=";
                if (line.StartsWith(prefix, StringComparison.Ordinal) && long.TryParse(line.AsSpan(prefix.Length), out long micros))
                    progress.Report(Math.Clamp(micros / 1_000_000d / built.OutputDuration, 0, 0.995));
            }
            await RunAsync("ffmpeg", args, ct, Report, work);
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(temp) || new FileInfo(temp).Length < 64) throw new IOException("未生成有效的 GIF 文件。");
            File.Move(temp, output, overwrite);
            progress.Report(1);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    public async Task ExportStillFrameAsync(
        IReadOnlyList<(Clip Clip, MediaAsset Asset)> clips,
        double timestamp,
        int shortEdge,
        string aspectFormat,
        StillFrameFormat imageFormat,
        string output,
        CancellationToken ct,
        bool overwrite = true,
        IReadOnlyList<TitleLayer>? titles = null,
        IReadOnlyList<(OverlayClip Clip, MediaAsset Asset)>? overlays = null,
        IReadOnlyDictionary<string, TrackFlags>? trackStates = null,
        int quality = 92,
        bool lossless = false)
    {
        if (clips.Count == 0) throw new InvalidOperationException("时间线还没有视频或图片片段。");
        EnsureNotSourcePath(output, clips, overlays ?? [], []);

        double t = Math.Max(0, timestamp);
        var opt = new ExportOptions(shortEdge, 30, aspectFormat, false)
        {
            RangeStart = t,
            RangeEnd = t + 0.08
        };

        string ext = ExportOptions.ImageExtension(imageFormat);
        bool isHeic = imageFormat == StillFrameFormat.Heic;
        string temp = Path.Combine(Path.GetDirectoryName(output)!, $".qingjian-frame-{Guid.NewGuid():N}{(isHeic ? ".mp4" : ext)}");
        string heicTemp = Path.Combine(Path.GetDirectoryName(output)!, $".qingjian-frame-{Guid.NewGuid():N}.heic");
        string work = Path.Combine(Path.GetTempPath(), $"qingjian-frame-{Guid.NewGuid():N}");
        string graphPath = Path.Combine(work, "frame.filters");

        var built = BuildTimelineFilterGraph(clips, opt, titles ?? [], [], overlays ?? [], trackStates,
            includeVideo: true, includeAudio: false, sampleRate: 48000);

        var args = new List<string>(built.InputArgs);
        args.AddRange(["-/filter_complex", graphPath, "-map", "[vout]", "-an", "-frames:v", "1"]);
        if (!isHeic && imageFormat != StillFrameFormat.Avif) args.AddRange(["-update", "1"]);
        args.AddRange(ImageEncodeArguments(imageFormat, quality, lossless));
        args.Add(temp);

        try
        {
            Directory.CreateDirectory(work);
            await PrepareTitleFilesAsync(built.ActiveTitles, work, ct);
            await File.WriteAllTextAsync(graphPath, built.FilterGraph, new UTF8Encoding(false), ct);
            await RunAsync("ffmpeg", args, ct, null, work);
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(temp) || new FileInfo(temp).Length < 64) throw new IOException("未能导出当前静帧画面。");
            if (isHeic)
            {
                WrapHevcMp4ToHeic(temp, heicTemp);
                File.Move(heicTemp, output, overwrite);
            }
            else
            {
                File.Move(temp, output, overwrite);
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (File.Exists(heicTemp)) File.Delete(heicTemp);
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    public async Task ConvertImageAsync(
        string inputPath,
        string outputPath,
        StillFrameFormat format,
        int? maxLongEdge,
        int quality,
        bool lossless,
        CancellationToken ct,
        bool overwrite = true)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException("找不到源图片文件。", inputPath);
        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("输出路径不能与源图片相同，请换个文件名或文件夹。");

        string ext = ExportOptions.ImageExtension(format);
        bool isHeic = format == StillFrameFormat.Heic;
        string temp = Path.Combine(Path.GetDirectoryName(outputPath)!, $".qingjian-img-{Guid.NewGuid():N}{(isHeic ? ".mp4" : ext)}");
        string heicTemp = Path.Combine(Path.GetDirectoryName(outputPath)!, $".qingjian-img-{Guid.NewGuid():N}.heic");
        var args = new List<string> { "-v", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-i", inputPath, "-an", "-frames:v", "1" };

        var vf = new List<string>();
        if (maxLongEdge is int maxEdge && maxEdge >= 64)
            vf.Add($"scale={maxEdge}:{maxEdge}:force_original_aspect_ratio=decrease:reset_sar=1");
        if (format is StillFrameFormat.Avif or StillFrameFormat.Heic)
            vf.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        if (vf.Count > 0)
            args.AddRange(["-vf", string.Join(",", vf)]);

        if (!isHeic && format != StillFrameFormat.Avif) args.AddRange(["-update", "1"]);
        args.AddRange(ImageEncodeArguments(format, quality, lossless));
        args.Add(temp);

        try
        {
            await RunAsync("ffmpeg", args, ct);
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(temp) || new FileInfo(temp).Length < 32) throw new IOException("图片转换失败，未生成有效输出文件。");
            if (isHeic)
            {
                WrapHevcMp4ToHeic(temp, heicTemp);
                File.Move(heicTemp, outputPath, overwrite);
            }
            else
            {
                File.Move(temp, outputPath, overwrite);
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (File.Exists(heicTemp)) File.Delete(heicTemp);
        }
    }

    private static string[] ImageEncodeArguments(StillFrameFormat format, int quality, bool lossless)
    {
        int q = Math.Clamp(quality, 50, 100);
        return format switch
        {
            StillFrameFormat.Jpg => ["-c:v", "mjpeg", "-q:v", Math.Clamp((int)Math.Round(2 + (100 - q) * 0.22), 2, 16).ToString(Invariant)],
            StillFrameFormat.Webp => lossless
                ? ["-c:v", "libwebp", "-lossless", "1", "-compression_level", "4"]
                : ["-c:v", "libwebp", "-quality", q.ToString(Invariant)],
            StillFrameFormat.Heic => ["-c:v", "libx265", "-preset", "fast",
                "-crf", Math.Clamp((int)Math.Round(12 + (100 - q) * 0.56), 12, 42).ToString(Invariant),
                "-x265-params", "log-level=error:no-info=1",
                "-pix_fmt", "yuv420p", "-color_range", "pc", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "iec61966-2-1",
                "-tag:v", "hvc1", "-f", "mp4"],
            StillFrameFormat.Bmp => ["-c:v", "bmp"],
            StillFrameFormat.Tiff => ["-c:v", "tiff", "-compression_algo", "deflate"],
            StillFrameFormat.Avif => ["-c:v", "libaom-av1", "-still-picture", "1",
                "-crf", Math.Clamp((int)Math.Round(12 + (100 - q) * 0.65), 12, 48).ToString(Invariant),
                "-cpu-used", "6", "-pix_fmt", "yuv420p", "-f", "avif"],
            _ => ["-c:v", "png"]
        };
    }

    // Packages a single-frame HEVC MP4 from libx265 into a compliant ISO/IEC 23008-12 HEIC (mif1/heic) file
    private static void WrapHevcMp4ToHeic(string hevcMp4Path, string heicOutputPath)
    {
        byte[] data = File.ReadAllBytes(hevcMp4Path);

        static uint ReadU32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
        static ushort ReadU16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
        static string ReadTag(byte[] b, int o) => Encoding.ASCII.GetString(b, o, 4);

        (int RawStart, int DataStart, int End)? FindBox(string target, int start, int end)
        {
            int i = start;
            while (i + 8 <= end)
            {
                long sz = ReadU32(data, i);
                string tag = ReadTag(data, i + 4);
                int hdr = 8;
                if (sz == 1 && i + 16 <= end)
                {
                    sz = ((long)ReadU32(data, i + 8) << 32) | ReadU32(data, i + 12);
                    hdr = 16;
                }
                else if (sz == 0) sz = end - i;
                if (sz < hdr || i + sz > end) break;
                int boxEnd = (int)(i + sz);
                int dataStart = i + hdr;
                if (tag == target) return (i, dataStart, boxEnd);
                if (tag is "moov" or "trak" or "mdia" or "minf" or "stbl")
                {
                    var found = FindBox(target, dataStart, boxEnd);
                    if (found is not null) return found;
                }
                else if (tag == "stsd" && dataStart + 8 < boxEnd)
                {
                    var found = FindBox(target, dataStart + 8, boxEnd);
                    if (found is not null) return found;
                }
                else if (tag is "hvc1" or "hev1" && dataStart + 78 < boxEnd)
                {
                    var found = FindBox(target, dataStart + 78, boxEnd);
                    if (found is not null) return found;
                }
                i = boxEnd;
            }
            return null;
        }

        var hvccBox = FindBox("hvcC", 0, data.Length) ?? throw new IOException("未能提取 HEVC hvcC 配置头。");
        var hvc1Box = FindBox("hvc1", 0, data.Length) ?? FindBox("hev1", 0, data.Length) ?? throw new IOException("未能提取 HEVC 视觉采样信息。");
        var stszBox = FindBox("stsz", 0, data.Length) ?? throw new IOException("未能提取 HEVC 帧大小。");
        var stcoBox = FindBox("stco", 0, data.Length);
        var co64Box = stcoBox is null ? FindBox("co64", 0, data.Length) : null;
        if (stcoBox is null && co64Box is null) throw new IOException("未能提取 HEVC 数据偏移。");

        int width = ReadU16(data, hvc1Box.DataStart + 24);
        int height = ReadU16(data, hvc1Box.DataStart + 26);
        uint sampleSize = ReadU32(data, stszBox.DataStart + 4);
        if (sampleSize == 0) sampleSize = ReadU32(data, stszBox.DataStart + 12);
        long chunkOffset = stcoBox is not null
            ? ReadU32(data, stcoBox.Value.DataStart + 8)
            : ((long)ReadU32(data, co64Box!.Value.DataStart + 8) << 32) | ReadU32(data, co64Box.Value.DataStart + 12);
        if (sampleSize == 0 || chunkOffset < 0 || chunkOffset + sampleSize > data.Length)
            throw new IOException("HEVC 图像帧数据无效。");

        byte[] hvccRaw = data[hvccBox.RawStart..hvccBox.End];
        byte[] payload = data[(int)chunkOffset..(int)(chunkOffset + sampleSize)];

        static byte[] Box(string tag, params byte[][] parts)
        {
            int len = 8 + parts.Sum(p => p.Length);
            var ms = new MemoryStream(len);
            WriteU32(ms, (uint)len);
            ms.Write(Encoding.ASCII.GetBytes(tag));
            foreach (var p in parts) ms.Write(p);
            return ms.ToArray();
        }
        static byte[] FullBox(string tag, byte version, uint flags, params byte[][] parts)
        {
            byte[] vf = [version, (byte)((flags >> 16) & 0xFF), (byte)((flags >> 8) & 0xFF), (byte)(flags & 0xFF)];
            return Box(tag, [vf, .. parts]);
        }
        static void WriteU32(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        static byte[] U16(ushort v) => [(byte)(v >> 8), (byte)v];
        static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

        byte[] ftyp = Box("ftyp", Encoding.ASCII.GetBytes("heic"), U32(0), Encoding.ASCII.GetBytes("mif1heic"));
        byte[] hdlr = FullBox("hdlr", 0, 0, U32(0), Encoding.ASCII.GetBytes("pict"), new byte[12], [0]);
        byte[] pitm = FullBox("pitm", 0, 0, U16(1));
        byte[] infe = FullBox("infe", 2, 0, U16(1), U16(0), Encoding.ASCII.GetBytes("hvc1"), [0]);
        byte[] iinf = FullBox("iinf", 0, 0, U16(1), infe);
        byte[] ispe = FullBox("ispe", 0, 0, U32((uint)width), U32((uint)height));
        byte[] pixi = FullBox("pixi", 0, 0, [3, 8, 8, 8]);
        byte[] colr = Box("colr", Encoding.ASCII.GetBytes("nclx"), U16(1), U16(13), U16(1), [0x80]);
        byte[] ipco = Box("ipco", ispe, hvccRaw, pixi, colr);
        // Associate item 1 with props: 1(ispe), 2(hvcC essential 0x82), 3(pixi), 4(colr)
        byte[] ipma = FullBox("ipma", 0, 0, U32(1), U16(1), [4, 0x01, 0x82, 0x03, 0x04]);
        byte[] iprp = Box("iprp", ipco, ipma);

        byte[] BuildMeta(uint mdatDataOffset)
        {
            byte[] iloc = FullBox("iloc", 0, 0, [0x44, 0x00], U16(1), U16(1), U16(0), U16(1), U32(mdatDataOffset), U32((uint)payload.Length));
            return FullBox("meta", 0, 0, hdlr, pitm, iinf, iprp, iloc);
        }

        byte[] dummyMeta = BuildMeta(0);
        uint mdatOffset = (uint)(ftyp.Length + dummyMeta.Length + 8);
        byte[] meta = BuildMeta(mdatOffset);
        byte[] mdat = Box("mdat", payload);

        using var fs = File.Create(heicOutputPath);
        fs.Write(ftyp);
        fs.Write(meta);
        fs.Write(mdat);
    }

    private sealed record BuiltGraph(
        List<string> InputArgs,
        string FilterGraph,
        double OutputDuration,
        int NextInputIndex,
        IReadOnlyList<TitleLayer> ActiveTitles);

    private static BuiltGraph BuildTimelineFilterGraph(
        IReadOnlyList<(Clip Clip, MediaAsset Asset)> clips,
        ExportOptions options,
        IReadOnlyList<TitleLayer> titles,
        IReadOnlyList<(AudioLayer Layer, MediaAsset Asset)> audioLayers,
        IReadOnlyList<(OverlayClip Clip, MediaAsset Asset)> overlays,
        IReadOnlyDictionary<string, TrackFlags>? trackStates,
        bool includeVideo,
        bool includeAudio,
        int sampleRate)
    {
        TrackFlags Flags(string id) => trackStates is not null && trackStates.TryGetValue(id, out var f) ? f : new TrackFlags();
        bool v1Hidden = Flags("V1").Hidden;
        bool a1Muted = Flags("A1").Muted || Flags("V1").Muted;
        bool v2Hidden = Flags("V2").Hidden || options.StripEffects;
        bool v2Muted = Flags("V2").Muted || options.StripEffects;
        bool t1Hidden = Flags("T1").Hidden || options.StripEffects;
        bool a2Muted = Flags("A2").Muted;

        var activeOverlays = v2Hidden ? [] : overlays;
        var activeTitles = t1Hidden ? [] : titles;
        var activeAudioLayers = a2Muted ? [] : audioLayers;

        var (width, height) = options.Size;
        double fpsVal = options.EffectiveFpsValue;
        string fpsExpr = options.FpsExpr;

        var args = new List<string> { "-v", "error", "-nostdin", "-y", "-progress", "pipe:1", "-stats_period", "0.2",
            "-filter_complex_threads", Math.Clamp(Environment.ProcessorCount / 2, 2, 8).ToString(Invariant) };
        var filter = new StringBuilder();
        var audioTracks = new StringBuilder();

        string rateFilter = $"fps={fpsExpr},settb=1/{fpsExpr},setpts=N";
        var frames = new long[clips.Count + 1];
        var overlaps = new int[clips.Count + 1];
        double sourceEnd = 0;
        for (int i = 0; i < clips.Count; i++)
        {
            sourceEnd += clips[i].Clip.Duration;
            frames[i + 1] = Math.Max(frames[i] + 1, (long)Math.Round(sourceEnd * fpsVal));
        }
        for (int i = 1; i < clips.Count; i++)
        {
            long halfLimit = Math.Min(frames[i] - frames[i - 1], frames[i + 1] - frames[i]) / 2;
            overlaps[i] = options.StripEffects ? 0 : (int)Math.Min(halfLimit,
                Math.Round(EditProject.TransitionLength(clips[i - 1].Clip, clips[i].Clip) * fpsVal / 2));
        }
        string workPixFmt = overlaps.Any(length => length > 0) ? "yuv444p" : "yuv420p";
        long totalFrames = Math.Max(frames[^1], (long)Math.Ceiling(fpsVal * Math.Max(
            overlays.Select(item => item.Clip.End).DefaultIfEmpty().Max(),
            Math.Max(
                titles.Select(title => title.End).DefaultIfEmpty().Max(),
                audioLayers.Select(item => item.Layer.End).DefaultIfEmpty().Max())) - 0.00001));
        double total = Math.Max(0.05, totalFrames / fpsVal);

        for (int i = 0; i < clips.Count; i++)
        {
            var (clip, asset) = clips[i];
            string length = Seconds((frames[i + 1] - frames[i]) / fpsVal);
            if (asset.IsImage)
                args.AddRange(["-threads", "2", "-protocol_whitelist", "file,pipe", "-i", asset.Path]);
            else
                args.AddRange(["-ss", Seconds(clip.In), "-t", Seconds(clip.Out - clip.In), "-threads", "2",
                    "-protocol_whitelist", "file,pipe", "-i", asset.Path]);

            if (includeVideo)
            {
                if (v1Hidden)
                {
                    filter.Append($"color=c=black:s={width}x{height}:r={fpsExpr}:d={length},format={workPixFmt},");
                }
                else
                {
                    filter.Append($"[{i}:{asset.VideoStream}]");
                    if (Math.Abs(clip.EffectiveSpeed - 1) > 0.001)
                        filter.Append($"setpts=PTS/{Seconds(clip.EffectiveSpeed)},");
                    filter.Append($"fps={fpsExpr}:start_time=0,");
                    if (!options.StripEffects && clip.Crop is { IsZero: false } crop)
                    {
                        filter.Append($"crop=iw*{100 - crop.Left - crop.Right}/100:ih*{100 - crop.Top - crop.Bottom}/100:" +
                            $"iw*{crop.Left}/100:ih*{crop.Top}/100,");
                    }
                    var tr = clip.EffectiveTransform;
                    if (tr.IsDefault)
                    {
                        filter.Append($"scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1,");
                        filter.Append($"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format={workPixFmt},");
                    }
                    else
                    {
                        int sw = Math.Max(2, ((int)Math.Round(width * tr.Scale)) & ~1);
                        int sh = Math.Max(2, ((int)Math.Round(height * tr.Scale)) & ~1);
                        filter.Append($"scale={sw}:{sh}:force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1,format=rgba");
                        if (Math.Abs(tr.Rotation) > 0.05)
                            filter.Append($",rotate={Seconds(tr.Rotation * Math.PI / 180)}:c=none:ow=rotw(iw):oh=roth(ih)");
                        if (tr.Opacity < 0.999)
                            filter.Append($",colorchannelmixer=aa={Seconds(tr.Opacity)}");
                        filter.Append($"[vtr{i}];color=c=black:s={width}x{height}:r={fpsExpr}:d={length}[vbg{i}];");
                        int cx = (int)Math.Round(width * (0.5 + tr.X));
                        int cy = (int)Math.Round(height * (0.5 + tr.Y));
                        filter.Append($"[vbg{i}][vtr{i}]overlay=x={cx}-w/2:y={cy}-h/2:shortest=1,format={workPixFmt},");
                    }
                }
                filter.Append($"tpad=stop_mode=clone:stop_duration={length},trim=duration={length},setpts=PTS-STARTPTS,");
                filter.Append($"tpad=start_mode=clone:stop_mode=clone:start={overlaps[i]}:stop={overlaps[i + 1]}," +
                    $"settb=1/{fpsExpr},setpts=N[v{i}];\n");
            }

            if (includeAudio)
            {
                if (!a1Muted && asset.AudioStream is int audio && clip.Volume > 0)
                {
                    filter.Append($"[{i}:{audio}]aresample={sampleRate}:async=1:first_pts=0,aformat=channel_layouts=stereo,");
                    AppendAudioSpeed(filter, clip.EffectiveSpeed);
                    filter.Append($"volume={Seconds(clip.Volume)},");
                    AppendAudioFade(filter, clip.FadeIn, clip.FadeOut, clip.Duration);
                    filter.Append("apad,");
                }
                else filter.Append($"anullsrc=r={sampleRate}:cl=stereo,");
                filter.Append($"atrim=duration={length},asetpts=PTS-STARTPTS[a{i}];\n");
                audioTracks.Append($"[a{i}]");
            }
        }

        string video = "[v0]";
        if (includeVideo)
        {
            for (int i = 1; i < clips.Count; i++)
            {
                filter.Append($"{video}[v{i}]");
                if (overlaps[i] > 0)
                {
                    string transition = Seconds(2 * overlaps[i] / fpsVal);
                    string offset = Seconds((frames[i] - overlaps[i]) / fpsVal);
                    filter.Append($"xfade=transition=fade:duration={transition}:offset={offset},");
                }
                else filter.Append("concat=n=2:v=1:a=0,");
                video = $"[video{i}]";
                filter.Append($"{rateFilter}{video};\n");
            }
            filter.Append($"{video}tpad=stop={totalFrames - frames[^1]}:color=black,format=yuv420p[vbase];\n");
            video = "[vbase]";
        }

        int nextInput = clips.Count;
        List<string> extraAudioLabels = [];
        for (int i = 0; i < activeOverlays.Count; i++)
        {
            var (ov, asset) = activeOverlays[i];
            bool needOvAudio = includeAudio && !v2Muted && asset.AudioStream is not null && ov.Volume > 0;
            if (!includeVideo && !needOvAudio) continue;

            int inputIdx = nextInput++;
            if (asset.IsImage)
                args.AddRange(["-threads", "2", "-protocol_whitelist", "file,pipe", "-i", asset.Path]);
            else
                args.AddRange(["-ss", Seconds(ov.In), "-t", Seconds(ov.Out - ov.In), "-threads", "2",
                    "-protocol_whitelist", "file,pipe", "-i", asset.Path]);

            if (includeVideo)
            {
                var tr = ov.EffectiveTransform;
                int sw = Math.Max(2, ((int)Math.Round(width * tr.Scale)) & ~1);
                int sh = Math.Max(2, ((int)Math.Round(height * tr.Scale)) & ~1);
                filter.Append($"[{inputIdx}:{asset.VideoStream}]");
                if (!asset.IsImage && Math.Abs(ov.EffectiveSpeed - 1) > 0.001)
                    filter.Append($"setpts=PTS/{Seconds(ov.EffectiveSpeed)},");
                filter.Append($"fps={fpsExpr}:start_time=0,");
                if (asset.IsImage)
                    filter.Append($"tpad=stop_mode=clone:stop_duration={Seconds(ov.Duration)},trim=duration={Seconds(ov.Duration)},setpts=PTS-STARTPTS,");
                if (ov.Crop is { IsZero: false } crop)
                {
                    filter.Append($"crop=iw*{100 - crop.Left - crop.Right}/100:ih*{100 - crop.Top - crop.Bottom}/100:" +
                        $"iw*{crop.Left}/100:ih*{crop.Top}/100,");
                }
                filter.Append($"scale={sw}:{sh}:force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1,format=rgba");
                if (Math.Abs(tr.Rotation) > 0.05)
                    filter.Append($",rotate={Seconds(tr.Rotation * Math.PI / 180)}:c=none:ow=rotw(iw):oh=roth(ih)");
                if (tr.Opacity < 0.999)
                    filter.Append($",colorchannelmixer=aa={Seconds(tr.Opacity)}");
                filter.Append($",setpts=PTS-STARTPTS+{Seconds(ov.Start)}/TB[ov{i}];\n");
                int cx = (int)Math.Round(width * (0.5 + tr.X));
                int cy = (int)Math.Round(height * (0.5 + tr.Y));
                string nextVideo = $"[vov{i}]";
                filter.Append($"{video}[ov{i}]overlay=x={cx}-w/2:y={cy}-h/2:eof_action=pass:enable='gte(t,{Seconds(ov.Start)})*lt(t,{Seconds(ov.End)})'{nextVideo};\n");
                video = nextVideo;
            }

            if (needOvAudio && asset.AudioStream is int ovAudio)
            {
                long delay = (long)Math.Round(ov.Start * sampleRate);
                filter.Append($"[{inputIdx}:{ovAudio}]aresample={sampleRate}:async=1:first_pts=0,aformat=channel_layouts=stereo,");
                AppendAudioSpeed(filter, ov.EffectiveSpeed);
                filter.Append($"volume={Seconds(ov.Volume)},");
                AppendAudioFade(filter, ov.FadeIn, ov.FadeOut, ov.Duration);
                filter.Append($"adelay={delay}S:all=1[ova{i}];\n");
                extraAudioLabels.Add($"[ova{i}]");
            }
        }

        double rangeStart = Math.Clamp(options.RangeStart ?? 0, 0, Math.Max(0, total - 0.05));
        double rangeEnd = options.RangeEnd is double re && re > rangeStart + 0.02 ? Math.Min(total, re) : total;
        bool hasRange = rangeStart > 0.001 || rangeEnd < total - 0.001;
        double outDuration = Math.Max(0.05, rangeEnd - rangeStart);

        if (includeVideo)
        {
            if (activeTitles.Count > 0)
            {
                filter.Append($"{video}format=yuv420p");
                double scaleFactor = Math.Min(width, height) / 1080.0;
                for (int i = 0; i < activeTitles.Count; i++)
                {
                    var title = activeTitles[i];
                    var st = title.EffectiveStyle;
                    int pxSize = Math.Max(14, (int)Math.Round(st.FontSize * scaleFactor));
                    string fontColor = FfmpegColor(st.ColorHex, "white");
                    string borderSpec = st.Outline && st.OutlineWidth > 0
                        ? $":borderw={Seconds(Math.Max(1, st.OutlineWidth * scaleFactor))}:bordercolor={FfmpegColor(st.OutlineColorHex, "black")}"
                        : ":borderw=0";
                    string boxSpec = st.BackgroundBox
                        ? $":box=1:boxcolor={FfmpegColor(st.BackgroundHex, "black@0.7")}:boxborderw={Math.Max(6, (int)Math.Round(12 * scaleFactor))}"
                        : "";
                    string xExpr = $"w*{Seconds(0.5 + st.X)}-tw/2";
                    string yExpr = $"h*{Seconds(0.5 + st.Y)}-th/2";
                    filter.Append($",drawtext=fontfile=title-font.ttc:textfile=title-{i}.txt:expansion=none:" +
                        $"fontcolor={fontColor}:fontsize={pxSize}{borderSpec}{boxSpec}:" +
                        $"x={xExpr}:y={yExpr}:enable='gte(t,{Seconds(title.Start)})*lt(t,{Seconds(title.End)})'");
                }
                filter.Append("[vpre];\n");
            }
            else
            {
                filter.Append($"{video}format=yuv420p[vpre];\n");
            }

            if (hasRange)
                filter.Append($"[vpre]trim=start={Seconds(rangeStart)}:end={Seconds(rangeEnd)},setpts=PTS-STARTPTS[vout]");
            else
                filter.Append("[vpre]null[vout]");
            if (includeAudio) filter.Append(";\n");
        }

        if (includeAudio)
        {
            for (int i = 0; i < activeAudioLayers.Count; i++)
            {
                var (layer, asset) = activeAudioLayers[i];
                int stream = asset.AudioStream ?? throw new InvalidOperationException("音频片段的素材没有音轨。");
                int inputIdx = nextInput++;
                args.AddRange(["-ss", Seconds(layer.In), "-t", Seconds(layer.Out - layer.In), "-threads", "2",
                    "-protocol_whitelist", "file,pipe", "-i", asset.Path]);
                long delay = (long)Math.Round(layer.Start * sampleRate);
                filter.Append($"[{inputIdx}:{stream}]aresample={sampleRate}:async=1:first_pts=0,aformat=channel_layouts=stereo,");
                AppendAudioSpeed(filter, layer.EffectiveSpeed);
                filter.Append($"volume={Seconds(layer.Volume)},");
                AppendAudioFade(filter, layer.FadeIn, layer.FadeOut, layer.Duration);
                filter.Append($"adelay={delay}S:all=1[layer{i}];\n");
                extraAudioLabels.Add($"[layer{i}]");
            }

            filter.Append($"{audioTracks}concat=n={clips.Count}:v=0:a=1,apad,atrim=duration={Seconds(total)}");
            if (extraAudioLabels.Count == 0)
            {
                filter.Append("[apre]");
            }
            else
            {
                filter.Append("[original];\n[original]");
                foreach (string lbl in extraAudioLabels) filter.Append(lbl);
                filter.Append($"amix=inputs={extraAudioLabels.Count + 1}:duration=first:normalize=0," +
                    "alimiter=limit=0.98:latency=1[apre]");
            }
            if (hasRange)
                filter.Append($";\n[apre]atrim=start={Seconds(rangeStart)}:end={Seconds(rangeEnd)},asetpts=PTS-STARTPTS[aout]");
            else
                filter.Append(";\n[apre]anull[aout]");
        }

        return new BuiltGraph(args, filter.ToString(), outDuration, nextInput, activeTitles);
    }

    private static void EnsureNotSourcePath(string output,
        IReadOnlyList<(Clip Clip, MediaAsset Asset)> clips,
        IReadOnlyList<(OverlayClip Clip, MediaAsset Asset)> overlays,
        IReadOnlyList<(AudioLayer Layer, MediaAsset Asset)> audioLayers)
    {
        if (clips.Select(item => item.Asset.Path)
            .Concat(overlays.Select(item => item.Asset.Path))
            .Concat(audioLayers.Select(item => item.Asset.Path))
            .Any(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("请选择其他导出位置，以免覆盖项目素材。");
    }

    private static async Task PrepareTitleFilesAsync(IReadOnlyList<TitleLayer> activeTitles, string work, CancellationToken ct)
    {
        if (activeTitles.Count == 0) return;
        string font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc");
        if (!File.Exists(font))
            font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
        if (!File.Exists(font)) throw new IOException("未找到用于绘制文字的 Windows 字体。");
        File.Copy(font, Path.Combine(work, "title-font.ttc"), true);
        for (int i = 0; i < activeTitles.Count; i++)
            await File.WriteAllTextAsync(Path.Combine(work, $"title-{i}.txt"), activeTitles[i].Text, new UTF8Encoding(false), ct);
    }

    private static string FormatSrtTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }

    private static void AppendAudioSpeed(StringBuilder filter, double speed)
    {
        if (Math.Abs(speed - 1) <= 0.001) return;
        double remaining = Math.Clamp(speed, 0.25, 8);
        while (remaining > 2.0) { filter.Append("atempo=2.0,"); remaining /= 2.0; }
        while (remaining < 0.5) { filter.Append("atempo=0.5,"); remaining /= 0.5; }
        filter.Append($"atempo={Seconds(remaining)},");
    }

    private static void AppendAudioFade(StringBuilder filter, double fadeIn, double fadeOut, double duration)
    {
        if (fadeIn > 0.01) filter.Append($"afade=t=in:st=0:d={Seconds(Math.Min(fadeIn, duration / 2))},");
        if (fadeOut > 0.01)
        {
            double d = Math.Min(fadeOut, duration / 2);
            filter.Append($"afade=t=out:st={Seconds(Math.Max(0, duration - d))}:d={Seconds(d)},");
        }
    }

    private static string FfmpegColor(string hex, string fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string s = hex.Trim().TrimStart('#');
        if (s.Length == 6) return "0x" + s;
        if (s.Length == 8 && byte.TryParse(s[..2], NumberStyles.HexNumber, Invariant, out byte a))
            return $"0x{s[2..]}@{Seconds(a / 255.0)}";
        return fallback;
    }

    private static string[] ColorSpaceArguments(ExportColorSpace cs) => cs switch
    {
        ExportColorSpace.Rec2020Sdr => ["-colorspace", "bt2020nc", "-color_primaries", "bt2020", "-color_trc", "bt709"],
        ExportColorSpace.Rec2100Hlg => ["-colorspace", "bt2020nc", "-color_primaries", "bt2020", "-color_trc", "arib-std-b67"],
        ExportColorSpace.Rec2100Pq => ["-colorspace", "bt2020nc", "-color_primaries", "bt2020", "-color_trc", "smpte2084"],
        _ => ["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"]
    };

    private static string SoftwareEncoderFor(ExportCodec codec) => codec switch
    {
        ExportCodec.Hevc or ExportCodec.Hevc422 => "libx265",
        ExportCodec.Av1 => "libsvtav1",
        ExportCodec.ProResProxy or ExportCodec.ProResLt or ExportCodec.ProRes422 or ExportCodec.ProRes422Hq or ExportCodec.ProRes4444 => "prores_ks",
        ExportCodec.Rle => "qtrle",
        _ => "libx264"
    };

    private static bool SupportsHardware(ExportCodec codec, bool is10Bit) => codec switch
    {
        ExportCodec.H264 => !is10Bit,
        ExportCodec.Hevc => true,
        ExportCodec.Av1 => true,
        _ => false
    };

    private async Task<string> FindEncoderAsync(ExportCodec codec, bool is10Bit, string softwareEncoder, CancellationToken ct)
    {
        string prefix = codec switch
        {
            ExportCodec.Hevc => "hevc",
            ExportCodec.Av1 => "av1",
            _ => "h264"
        };
        var probeOpt = new ExportOptions(256, 30, "16:9", true, codec) { BitDepth10 = is10Bit };
        foreach (string suffix in new[] { "nvenc", "qsv", "amf" })
        {
            string encoder = $"{prefix}_{suffix}";
            try
            {
                await RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "color=black:s=256x144",
                    "-frames:v", "1", .. EncoderArguments(encoder, probeOpt), "-f", "null", "-"], ct);
                return encoder;
            }
            catch (MediaException) { }
        }
        return softwareEncoder;
    }

    private static string[] EncoderArguments(string encoder, ExportOptions opt)
    {
        var list = new List<string> { "-c:v:0", encoder };

        if (encoder == "prores_ks")
        {
            int profile = opt.Codec switch
            {
                ExportCodec.ProResProxy => 0,
                ExportCodec.ProResLt => 1,
                ExportCodec.ProRes422 => 2,
                ExportCodec.ProRes422Hq => 3,
                ExportCodec.ProRes4444 => 4,
                _ => 2
            };
            list.AddRange(["-profile:v:0", profile.ToString(Invariant),
                "-pix_fmt:v:0", opt.Codec == ExportCodec.ProRes4444 ? "yuva444p10le" : "yuv422p10le"]);
            return [.. list];
        }
        if (encoder == "qtrle")
        {
            list.AddRange(["-pix_fmt:v:0", "rgb24"]);
            return [.. list];
        }

        // Pixel format for H.264 / HEVC / AV1
        string pixFmt = opt.Codec == ExportCodec.Hevc422
            ? "yuv422p10le"
            : opt.Effective10Bit
                ? (encoder.EndsWith("_nvenc") || encoder.EndsWith("_qsv") || encoder.EndsWith("_amf") ? "p010le" : "yuv420p10le")
                : "yuv420p";
        list.AddRange(["-pix_fmt:v:0", pixFmt]);

        int? bitrate = opt.TargetVideoBitrateKbps();
        bool cbr = opt.RateControl == RateControlMode.Cbr;

        if (bitrate is int average)
        {
            string rate = $"{average.ToString(Invariant)}k";
            string maxRate = $"{(cbr ? average : (int)(average * 1.5)).ToString(Invariant)}k";
            string bufSize = $"{(average * 2).ToString(Invariant)}k";

            if (encoder is "h264_nvenc" or "hevc_nvenc" or "av1_nvenc")
            {
                list.AddRange(["-preset", "p4", "-rc", cbr ? "cbr" : "vbr", "-b:v", rate, "-maxrate", maxRate, "-bufsize", bufSize]);
            }
            else if (encoder is "h264_qsv" or "hevc_qsv" or "av1_qsv")
            {
                list.AddRange(["-preset", "fast", "-b:v", rate, "-maxrate", maxRate, "-bufsize", bufSize]);
            }
            else if (encoder is "h264_amf" or "hevc_amf" or "av1_amf")
            {
                list.AddRange(["-quality", "balanced", "-rc", cbr ? "cbr" : "vbr_peak", "-b:v", rate, "-maxrate", maxRate, "-bufsize", bufSize]);
            }
            else if (encoder == "libsvtav1")
            {
                list.AddRange(["-preset", "8", "-rc", cbr ? "2" : "1", "-b:v", rate]);
            }
            else
            {
                list.AddRange(["-preset", "veryfast", "-threads", "8", "-b:v", rate, "-maxrate", maxRate, "-bufsize", bufSize]);
                if (cbr) list.AddRange(["-minrate", rate]);
            }
            return [.. list];
        }

        // Quality-based default VBR
        if (encoder is "h264_nvenc" or "hevc_nvenc" or "av1_nvenc")
            list.AddRange(["-preset", "p4", "-cq", "20", "-b:v", "0"]);
        else if (encoder is "h264_qsv" or "hevc_qsv" or "av1_qsv")
            list.AddRange(["-preset", "fast", "-global_quality", "20"]);
        else if (encoder is "h264_amf" or "hevc_amf" or "av1_amf")
            list.AddRange(["-quality", "balanced", "-rc", "cqp", "-qp_i", "20", "-qp_p", "22"]);
        else if (encoder == "libsvtav1")
            list.AddRange(["-preset", "8", "-crf", "30"]);
        else if (encoder == "libx265")
            list.AddRange(["-preset", "veryfast", "-crf", "23", "-threads", "8"]);
        else
            list.AddRange(["-preset", "veryfast", "-crf", "19", "-threads", "8"]);

        return [.. list];
    }

    private static string Text(JsonElement data, string key) => data.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static double Number(JsonElement data, string key) =>
        double.TryParse(Text(data, key), NumberStyles.Float, Invariant, out double value) ? value : 0;

    private async Task<string> RunAsync(string binary, IEnumerable<string> args, CancellationToken ct,
        Action<string>? line = null, string? workingDirectory = null)
    {
        ct.ThrowIfCancellationRequested();
        if (directory is null) throw new InvalidOperationException("请选择同时包含 ffmpeg.exe 和 ffprobe.exe 的文件夹。");
        var start = new ProcessStartInfo(Path.Combine(directory, binary + ".exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException($"无法启动 {binary}。");
        using var registration = ct.Register(() =>
        {
            try { process.Kill(true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var errors = process.StandardError.ReadToEndAsync();
        var stdout = new StringBuilder();
        while (await process.StandardOutput.ReadLineAsync() is string value)
        {
            if (line is null) stdout.AppendLine(value);
            else line(value);
        }
        await process.WaitForExitAsync();
        string error = await errors;
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new MediaException(string.IsNullOrWhiteSpace(error) ? $"{binary} 退出，返回码 {process.ExitCode}。" : error.Trim());
        return stdout.ToString();
    }
}

public sealed class MediaException(string message) : IOException(message);
