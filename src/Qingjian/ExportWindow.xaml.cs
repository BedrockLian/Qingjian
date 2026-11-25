using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Qingjian;

public partial class ExportWindow : Window
{
    private readonly string format;
    private readonly double duration;
    private readonly IReadOnlyList<string> protectedPaths;
    private readonly Func<CancellationToken, Task<ImageSource?>> readFrame;
    private readonly int titleCount;
    private readonly string? selectedClipName;
    private readonly double? selectedClipStart;
    private readonly double? selectedClipEnd;
    private readonly double selectedClipDuration;
    private string? customCoverPath;
    private ImageSource? defaultPreviewImage;
    private bool ready;

    public string? Output { get; private set; }
    public bool ReplaceExisting { get; private set; }
    public ExportOptions Options => BuildVideoOptions();
    public ExportBatchPlan? Plan { get; private set; }

    public ExportWindow(string format, double duration, string filename, string directory, IReadOnlyList<string> protectedPaths,
        ImageSource? fallback, Func<CancellationToken, Task<ImageSource?>> readFrame,
        int titleCount = 0, string? selectedClipName = null, double? selectedClipStart = null,
        double? selectedClipEnd = null, double? initialRangeStart = null, double? initialRangeEnd = null)
    {
        this.format = format;
        this.duration = Math.Max(0.1, duration);
        this.protectedPaths = protectedPaths;
        this.readFrame = readFrame;
        this.titleCount = titleCount;
        this.selectedClipName = selectedClipName;
        this.selectedClipStart = selectedClipStart;
        this.selectedClipEnd = selectedClipEnd;
        this.selectedClipDuration = selectedClipStart is double cs && selectedClipEnd is double ce ? Math.Max(0.1, ce - cs) : 0;

        InitializeComponent();
        ProjectLabel.Text = filename;
        FileNameBox.Text = filename;
        FolderBox.Text = directory;
        FrameImage.Source = fallback;
        defaultPreviewImage = fallback;

        double rs = initialRangeStart ?? 0;
        double re = initialRangeEnd is double ie && ie > rs + 0.05 ? Math.Min(this.duration, ie) : this.duration;
        RangeStartBox.Text = rs.ToString("0.00", CultureInfo.InvariantCulture);
        RangeEndBox.Text = re.ToString("0.00", CultureInfo.InvariantCulture);
        if (initialRangeStart is not null && initialRangeEnd is not null) ScopeRange.IsChecked = true;

        bool hasSelectedClip = !string.IsNullOrWhiteSpace(selectedClipName) && selectedClipDuration > 0;
        ScopeClip.IsEnabled = hasSelectedClip;
        SelectedClipDesc.Text = hasSelectedClip
            ? $"选中主视频片段：{selectedClipName}（{EditTime.Display(selectedClipDuration, true)}）"
            : "请先在时间线 V1 选中一个片段";

        ExportSubtitleCheck.IsEnabled = titleCount > 0;
        SubtitleAvailText.Text = titleCount > 0 ? $"共 {titleCount} 条字幕 · SRT / TXT" : "当前时间线无文字或字幕";

        ready = true;
        UpdateSummary();
    }

    private ExportCodec SelectedCodec()
    {
        string tag = (CodecCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "H264";
        return Enum.TryParse<ExportCodec>(tag, out var codec) ? codec : ExportCodec.H264;
    }

    private ExportContainer SelectedContainer() =>
        ContainerMov.IsChecked == true ? ExportContainer.Mov :
        ContainerMxf.IsChecked == true ? ExportContainer.Mxf :
        ExportContainer.Mp4;

    private ExportColorSpace SelectedColorSpace()
    {
        string tag = (ColorSpaceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Rec709Sdr";
        return Enum.TryParse<ExportColorSpace>(tag, out var cs) ? cs : ExportColorSpace.Rec709Sdr;
    }

    private VideoAudioQuality SelectedVideoAudioQuality()
    {
        string tag = (VideoAudioCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Aac192";
        return Enum.TryParse<VideoAudioQuality>(tag, out var q) ? q : VideoAudioQuality.Aac192;
    }

    private AudioExportFormat SelectedAudioFormat() =>
        AudWav.IsChecked == true ? AudioExportFormat.Wav :
        AudAac.IsChecked == true ? AudioExportFormat.Aac :
        AudFlac.IsChecked == true ? AudioExportFormat.Flac :
        AudAiff.IsChecked == true ? AudioExportFormat.Aiff :
        AudOgg.IsChecked == true ? AudioExportFormat.Ogg :
        AudioExportFormat.Mp3;

    private ExportScope SelectedScope() =>
        ScopeRange.IsChecked == true ? ExportScope.Range :
        ScopeClip.IsChecked == true ? ExportScope.SelectedClip :
        ExportScope.FullTimeline;

    private double EffectiveDuration(out double? rangeStart, out double? rangeEnd)
    {
        rangeStart = null;
        rangeEnd = null;
        var scope = SelectedScope();
        if (scope == ExportScope.SelectedClip && selectedClipDuration > 0)
            return selectedClipDuration;
        if (scope == ExportScope.Range)
        {
            if (double.TryParse(RangeStartBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double rs) &&
                double.TryParse(RangeEndBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double re))
            {
                rs = Math.Clamp(rs, 0, Math.Max(0, duration - 0.1));
                re = Math.Clamp(re, rs + 0.1, duration);
                rangeStart = rs;
                rangeEnd = re;
                return re - rs;
            }
        }
        return duration;
    }

    private ExportOptions BuildVideoOptions()
    {
        string resTag = (ResolutionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "1080";
        int shortEdge = int.TryParse(resTag, out int edge) ? edge : 1080;

        string fpsTag = (FpsCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "30";
        double exactFps = double.TryParse(fpsTag, NumberStyles.Float, CultureInfo.InvariantCulture, out double fp) ? fp : 30;
        int nominalFps = (int)Math.Round(exactFps);

        var codec = SelectedCodec();
        var container = SelectedContainer();
        var preset = BitrateLow.IsChecked == true ? BitratePreset.Low
            : BitrateHigh.IsChecked == true ? BitratePreset.High
            : BitrateCustom.IsChecked == true ? BitratePreset.Custom
            : BitratePreset.Recommended;
        int? customRate = preset == BitratePreset.Custom &&
            int.TryParse(BitrateBox.Text.Trim(), out int rate) && rate is >= 250 and <= 200_000
            ? rate : null;

        var rateMode = RateCbr.IsChecked == true ? RateControlMode.Cbr : RateControlMode.Vbr;
        var colorSpace = SelectedColorSpace();
        bool bit10 = BitDepth10Check.IsChecked == true || colorSpace != ExportColorSpace.Rec709Sdr;
        var audQ = SelectedVideoAudioQuality();
        int sr = VideoSr441.IsChecked == true ? 44100 : 48000;

        EffectiveDuration(out double? rangeStart, out double? rangeEnd);
        string? coverPath = ExportWithCover.IsChecked == true && !string.IsNullOrWhiteSpace(customCoverPath) && File.Exists(customCoverPath)
            ? customCoverPath : null;

        return new ExportOptions(shortEdge, nominalFps, format, Hardware.IsChecked == true, codec, customRate)
        {
            Container = container,
            ExactFps = exactFps,
            BitratePreset = preset,
            RateControl = rateMode,
            ColorSpace = colorSpace,
            BitDepth10 = bit10,
            FastStart = FastStartCheck.IsChecked == true && container != ExportContainer.Mxf,
            AudioQuality = audQ,
            AudioSampleRate = sr,
            CoverImagePath = coverPath,
            RangeStart = rangeStart,
            RangeEnd = rangeEnd,
            StripEffects = SelectedScope() == ExportScope.SelectedClip && IncludeClipEffects.IsChecked != true
        };
    }

    private void ToggleAdvVideoClick(object sender, RoutedEventArgs e)
    {
        bool open = AdvVideoPanel.Visibility != Visibility.Visible;
        AdvVideoPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ToggleAdvVideoBtn.Content = open ? "收起高级 ▴" : "高级设置 ▾";
    }

    private void CodecChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        var codec = SelectedCodec();
        if (codec is ExportCodec.ProResProxy or ExportCodec.ProResLt or ExportCodec.ProRes422
            or ExportCodec.ProRes422Hq or ExportCodec.ProRes4444 or ExportCodec.Rle)
        {
            ContainerMov.IsChecked = true;
        }
        if (codec == ExportCodec.Hevc422)
        {
            BitDepth10Check.IsChecked = true;
        }
        SettingsChanged(sender, e);
    }

    private void ContainerChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        var container = SelectedContainer();
        if (container == ExportContainer.Mxf)
        {
            FastStartCheck.IsChecked = false;
            FastStartCheck.IsEnabled = false;
            VideoSr48.IsChecked = true;
            VideoSr441.IsEnabled = false;
            ExportWithCover.IsEnabled = false;
        }
        else
        {
            FastStartCheck.IsEnabled = true;
            VideoSr441.IsEnabled = true;
            ExportWithCover.IsEnabled = true;
        }
        SettingsChanged(sender, e);
    }

    private void ScopeChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        var scope = SelectedScope();
        RangePanel.Visibility = scope == ExportScope.Range ? Visibility.Visible : Visibility.Collapsed;
        ClipScopePanel.Visibility = scope == ExportScope.SelectedClip ? Visibility.Visible : Visibility.Collapsed;
        if (scope == ExportScope.SelectedClip)
        {
            ExportSubtitleCheck.IsChecked = false;
            ExportSubtitleCheck.IsEnabled = false;
        }
        else
        {
            ExportSubtitleCheck.IsEnabled = titleCount > 0;
        }
        SettingsChanged(sender, e);
    }

    private void ComboChanged(object sender, SelectionChangedEventArgs e) => SettingsChanged(sender, e);

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        Validation.Visibility = Visibility.Collapsed;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var scope = SelectedScope();
        RangePanel.Visibility = scope == ExportScope.Range ? Visibility.Visible : Visibility.Collapsed;
        ClipScopePanel.Visibility = scope == ExportScope.SelectedClip ? Visibility.Visible : Visibility.Collapsed;

        bool expVideo = ExportVideoCheck.IsChecked == true;
        bool expAudio = ExportAudioCheck.IsChecked == true;
        bool expSub = ExportSubtitleCheck.IsChecked == true && ExportSubtitleCheck.IsEnabled;
        bool expGif = ExportGifCheck.IsChecked == true;

        VideoOptionsBody.Visibility = expVideo ? Visibility.Visible : Visibility.Collapsed;
        ToggleAdvVideoBtn.Visibility = expVideo ? Visibility.Visible : Visibility.Collapsed;
        AudioOptionsBody.Visibility = expAudio ? Visibility.Visible : Visibility.Collapsed;
        SubtitleOptionsBody.Visibility = expSub ? Visibility.Visible : Visibility.Collapsed;
        GifOptionsBody.Visibility = expGif ? Visibility.Visible : Visibility.Collapsed;

        var opt = BuildVideoOptions();
        bool fixedBitrateCodec = opt.IsProRes || opt.Codec == ExportCodec.Rle;
        BitrateSection.Visibility = fixedBitrateCodec ? Visibility.Collapsed : Visibility.Visible;
        CustomBitrateWrap.Visibility = !fixedBitrateCodec && BitrateCustom.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        var audFmt = SelectedAudioFormat();
        bool losslessAud = audFmt is AudioExportFormat.Wav or AudioExportFormat.Flac or AudioExportFormat.Aiff;
        AudioBitratePanel.IsEnabled = !losslessAud;
        AudioBitratePanel.Opacity = losslessAud ? 0.42 : 1.0;

        double effDuration = EffectiveDuration(out _, out _);
        var (width, height) = opt.Size;
        PreviewGeometry.Text = $"{format}  ·  {width} × {height}";

        int count = (expVideo ? 1 : 0) + (expAudio ? 1 : 0) + (expSub ? 1 : 0) + (expGif ? 1 : 0);
        StartExportBtn.Content = count > 1 ? $"开始导出 ({count} 项)" : "开始导出";
        StartExportBtn.IsEnabled = count > 0;

        var parts = new List<string>();
        var files = new List<string>();
        double estMb = 0;
        string name = string.IsNullOrWhiteSpace(FileNameBox.Text) ? "成片" : FileNameBox.Text.Trim();

        if (expVideo)
        {
            parts.Add($"{opt.ShortEdge}P {opt.EffectiveFpsValue:0.##}fps {ExportOptions.CodecDisplayName(opt.Codec)}");
            files.Add(name + opt.Extension);
            estMb += effDuration * (opt.EstimatedVideoBitrateKbps() + opt.EffectiveAudioBitrateKbps) / 8000.0;
        }
        if (expAudio)
        {
            int abr = AudBr192.IsChecked == true ? 192 : AudBr320.IsChecked == true ? 320 : 256;
            int effAbr = audFmt switch
            {
                AudioExportFormat.Wav or AudioExportFormat.Aiff => (AudSr441.IsChecked == true ? 44100 : 48000) * 32 / 1000,
                AudioExportFormat.Flac => 900,
                _ => abr
            };
            parts.Add($"音频 {audFmt.ToString().ToUpperInvariant()}");
            files.Add(name + ExportOptions.AudioExtension(audFmt));
            estMb += effDuration * effAbr / 8000.0;
        }
        if (expSub)
        {
            var sf = SubTxt.IsChecked == true ? SubtitleExportFormat.Txt : SubtitleExportFormat.Srt;
            parts.Add($"字幕 {sf.ToString().ToUpperInvariant()}");
            files.Add(name + ExportOptions.SubtitleExtension(sf));
            estMb += 0.01;
        }
        if (expGif)
        {
            int gRes = Gif240.IsChecked == true ? 240 : Gif480.IsChecked == true ? 480 : Gif640.IsChecked == true ? 640 : 320;
            int gFps = GifFps10.IsChecked == true ? 10 : GifFps20.IsChecked == true ? 20 : 15;
            parts.Add($"GIF {gRes}P {gFps}fps");
            files.Add(name + ".gif");
            estMb += effDuration * (gRes * gRes / 120_000.0) * (gFps / 15.0);

            if (effDuration > 60)
            {
                GifDurationWarn.Text = "当前导出时长超过 60 秒，建议切换至「入出点选区」缩短 GIF 范围。";
                GifDurationWarn.Visibility = Visibility.Visible;
            }
            else GifDurationWarn.Visibility = Visibility.Collapsed;
        }

        Summary.Text = parts.Count > 0 ? string.Join("  +  ", parts) : "请至少勾选一项导出产物";
        OutputFilesSummary.Text = files.Count > 0 ? string.Join("  ·  ", files) : "";
        FileEstimate.Text = $"导出时长 {EditTime.Display(effDuration, true)}  ·  预估约 {FormatSize(estMb)}";

        UpdateDiskTelemetry(estMb, opt);
    }

    private void UpdateDiskTelemetry(double estMb, ExportOptions opt)
    {
        DiskWarningText.Visibility = Visibility.Collapsed;
        DiskSpaceText.Text = "";
        try
        {
            string folder = FolderBox.Text.Trim();
            if (!Path.IsPathFullyQualified(folder)) return;
            string? root = Path.GetPathRoot(folder);
            if (string.IsNullOrEmpty(root)) return;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;
            double freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
            DiskSpaceText.Text = $"{drive.Name.TrimEnd('\\')} 可用 {freeGb:0.1} GB";

            var warnings = new List<string>();
            if (freeGb * 1024.0 < Math.Max(200, estMb * 1.5))
                warnings.Add("目标磁盘可用空间偏低，建议清理空间或更换导出盘。");
            if (drive.DriveFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) && estMb > 3800)
                warnings.Add($"目标盘为 {drive.DriveFormat} 格式，单个文件不能超过 4 GB。");
            if (ExportVideoCheck.IsChecked == true && opt.ShortEdge >= 4320)
                warnings.Add("8K 编码对显存和内存占用极高，请确保系统内存充足。");

            if (warnings.Count > 0)
            {
                DiskWarningText.Text = string.Join(" ", warnings);
                DiskWarningText.Visibility = Visibility.Visible;
            }
        }
        catch { }
    }

    private static string FormatSize(double mb) => mb >= 1024 ? $"{mb / 1024.0:0.##} GB" : mb < 1 ? $"{Math.Max(1, mb * 1024):0} KB" : $"{mb:0.##} MB";

    private void ResizePreview(object sender, SizeChangedEventArgs e)
    {
        var (width, height) = new ExportOptions(720, 30, format, false).Size;
        double ratio = width / (double)height;
        double available = Math.Min(PreviewHost.ActualWidth, PreviewHost.ActualHeight * ratio);
        PreviewFrame.Width = Math.Max(20, available);
        PreviewFrame.Height = Math.Max(20, available / ratio);
    }

    private async void LoadPreview(object sender, RoutedEventArgs e)
    {
        using var operation = new CancellationTokenSource();
        void CancelPreview(object? s, EventArgs args) => operation.Cancel();
        Closed += CancelPreview;
        try
        {
            ImageSource? frame = await readFrame(operation.Token);
            if (operation.IsCancellationRequested) return;
            if (frame is not null)
            {
                defaultPreviewImage = frame;
                if (customCoverPath is null) FrameImage.Source = frame;
            }
            PreviewKind.Text = customCoverPath is not null ? "已选自定义封面" : frame is not null ? "成片首帧" : "素材缩略图";
        }
        catch (OperationCanceledException) { }
        catch { PreviewKind.Text = FrameImage.Source is not null ? "素材缩略图" : "画面预览暂不可用"; }
        finally { Closed -= CancelPreview; }
    }

    private void ChooseCoverClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择自定义封面图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.webp|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(dialog.FileName);
            bmp.EndInit();
            bmp.Freeze();
            customCoverPath = dialog.FileName;
            FrameImage.Source = bmp;
            ExportWithCover.IsChecked = true;
            ResetCoverBtn.Visibility = Visibility.Visible;
            PreviewKind.Text = "自定义封面：" + Path.GetFileName(dialog.FileName);
            SettingsChanged(sender, e);
        }
        catch (Exception ex) { ShowValidation("无法加载封面图片：" + ex.Message); }
    }

    private void ResetCoverClick(object sender, RoutedEventArgs e)
    {
        customCoverPath = null;
        FrameImage.Source = defaultPreviewImage;
        ResetCoverBtn.Visibility = Visibility.Collapsed;
        PreviewKind.Text = "成片首帧";
        SettingsChanged(sender, e);
    }

    private void ChooseDirectory(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择导出文件夹" };
        if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void ConfirmOutput(object sender, RoutedEventArgs e)
    {
        bool expVideo = ExportVideoCheck.IsChecked == true;
        bool expAudio = ExportAudioCheck.IsChecked == true;
        bool expSub = ExportSubtitleCheck.IsChecked == true && ExportSubtitleCheck.IsEnabled;
        bool expGif = ExportGifCheck.IsChecked == true;
        if (!expVideo && !expAudio && !expSub && !expGif)
        {
            ShowValidation("请至少勾选一项导出产物。");
            return;
        }

        string name = FileNameBox.Text.Trim();
        foreach (string ext in new[] { ".mp4", ".mov", ".mxf", ".mp3", ".wav", ".gif", ".srt", ".txt" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name[..^ext.Length].TrimEnd();
        if (string.IsNullOrEmpty(name) || name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowValidation("请输入有效的文件名。");
            FileNameBox.Focus();
            return;
        }

        string folder = FolderBox.Text.Trim();
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
        {
            ShowValidation("请选择一个已存在的导出文件夹。");
            return;
        }

        var videoOpt = BuildVideoOptions();
        if (expVideo && !videoOpt.IsProRes && videoOpt.Codec != ExportCodec.Rle && BitrateCustom.IsChecked == true && videoOpt.BitrateKbps is null)
        {
            ShowValidation("自定义码率请输入 250 至 200000 之间的整数（kbps）。");
            BitrateBox.Focus();
            return;
        }

        if (SelectedScope() == ExportScope.Range)
        {
            if (!double.TryParse(RangeStartBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double rs) ||
                !double.TryParse(RangeEndBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double re) ||
                rs < 0 || re <= rs + 0.05 || re > duration + 0.05)
            {
                ShowValidation($"入出点选区无效，请输入 0 至 {duration:0.00} 秒之间的有效区间。");
                return;
            }
        }

        if (expGif && EffectiveDuration(out _, out _) > 60)
        {
            ShowValidation("GIF 导出最长支持 60 秒，请缩短入出点范围或取消勾选 GIF。");
            return;
        }

        int audBr = AudBr192.IsChecked == true ? 192 : AudBr320.IsChecked == true ? 320 : 256;
        int audSr = AudSr441.IsChecked == true ? 44100 : 48000;
        var subFmt = SubTxt.IsChecked == true ? SubtitleExportFormat.Txt : SubtitleExportFormat.Srt;
        int gifEdge = Gif240.IsChecked == true ? 240 : Gif480.IsChecked == true ? 480 : Gif640.IsChecked == true ? 640 : 320;
        int gifFps = GifFps10.IsChecked == true ? 10 : GifFps20.IsChecked == true ? 20 : 15;

        var plan = new ExportBatchPlan(
            name, folder, false, SelectedScope(), IncludeClipEffects.IsChecked == true,
            expVideo, videoOpt, expAudio, SelectedAudioFormat(), audBr, audSr,
            expSub, subFmt, expGif, gifEdge, gifFps);

        var outputs = plan.PlannedOutputs();
        foreach (var (_, fullPath) in outputs)
        {
            if (protectedPaths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                ShowValidation($"输出文件「{Path.GetFileName(fullPath)}」与项目源素材同名，请更换文件名或文件夹。");
                return;
            }
        }

        var existing = outputs.Where(o => File.Exists(o.Path)).Select(o => Path.GetFileName(o.Path)).ToArray();
        if (existing.Length > 0 && MessageBox.Show(this,
                $"目标文件夹已存在以下文件：\n{string.Join("、", existing)}\n\n是否替换它们？",
                "确认替换文件", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        ReplaceExisting = existing.Length > 0;
        Plan = plan with { ReplaceExisting = ReplaceExisting };
        Output = outputs[0].Path;
        DialogResult = true;
    }

    private void ShowValidation(string message)
    {
        Validation.Text = message;
        Validation.Visibility = Visibility.Visible;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowColors.Apply(this);
    }
}
