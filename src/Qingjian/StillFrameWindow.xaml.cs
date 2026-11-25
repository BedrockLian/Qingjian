using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace Qingjian;

public partial class StillFrameWindow : Window
{
    private readonly bool convertMode;
    private string[] sourceFiles = [];
    private bool ready;

    public string? OutputPath { get; private set; }
    public string OutputDirectory => FolderBox.Text.Trim();
    public IReadOnlyList<string> SourceFiles => sourceFiles;
    public int ShortEdge => Res720.IsChecked == true ? 720 : Res1440.IsChecked == true ? 1440 : Res2160.IsChecked == true ? 2160 : 1080;
    public int? MaxLongEdge => ResOrig.IsChecked == true ? null : Res720.IsChecked == true ? 1280 : Res1080.IsChecked == true ? 1920 : Res1440.IsChecked == true ? 2560 : 3840;
    public StillFrameFormat ImageFormat =>
        FmtJpg.IsChecked == true ? StillFrameFormat.Jpg :
        FmtWebp.IsChecked == true ? StillFrameFormat.Webp :
        FmtHeic.IsChecked == true ? StillFrameFormat.Heic :
        FmtBmp.IsChecked == true ? StillFrameFormat.Bmp :
        FmtTiff.IsChecked == true ? StillFrameFormat.Tiff :
        FmtAvif.IsChecked == true ? StillFrameFormat.Avif :
        StillFrameFormat.Png;
    public int Quality => (int)Math.Round(QualitySlider.Value);
    public bool Lossless => ImageFormat == StillFrameFormat.Webp && WebpLosslessCheck.IsChecked == true;
    public bool ReplaceExisting { get; private set; }

    public StillFrameWindow(string defaultName, string defaultFolder, double timestamp)
    {
        convertMode = false;
        InitializeComponent();
        Title = "导出静帧画面";
        DialogTitleText.Text = "导出静帧画面";
        TimeBadge.Text = $"当前帧  {EditTime.Display(timestamp, true)}";
        NameBox.Text = defaultName;
        FolderBox.Text = defaultFolder;
        ready = true;
        RefreshFormatState();
    }

    public StillFrameWindow(IReadOnlyList<string> initialSources, string defaultFolder)
    {
        convertMode = true;
        sourceFiles = initialSources.Where(File.Exists).ToArray();
        InitializeComponent();
        Title = "图片格式转换";
        DialogTitleText.Text = "图片格式转换";
        SourceSection.Visibility = Visibility.Visible;
        ResOrig.Visibility = Visibility.Visible;
        ResOrig.IsChecked = true;
        FolderBox.Text = defaultFolder;
        ConfirmBtn.Content = "开始转换";
        UpdateSourceSummary();
        // Default target format to something different from source if single file is PNG
        if (sourceFiles.Length == 1 && sourceFiles[0].EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            FmtWebp.IsChecked = true;
        ready = true;
        RefreshFormatState();
    }

    private void UpdateSourceSummary()
    {
        if (sourceFiles.Length == 0)
        {
            SourceSummaryBox.Text = "未选择源图片（点击右侧按钮选择）";
            TimeBadge.Text = "0 张图片";
            NameSection.Visibility = Visibility.Visible;
        }
        else if (sourceFiles.Length == 1)
        {
            SourceSummaryBox.Text = Path.GetFileName(sourceFiles[0]);
            TimeBadge.Text = $"1 张图片 · {Path.GetExtension(sourceFiles[0]).TrimStart('.').ToUpperInvariant()}";
            NameSection.Visibility = Visibility.Visible;
            NameBox.Text = Path.GetFileNameWithoutExtension(sourceFiles[0]) + "_converted";
        }
        else
        {
            SourceSummaryBox.Text = $"已选 {sourceFiles.Length} 张图片（{Path.GetFileName(sourceFiles[0])} 等）";
            TimeBadge.Text = $"批量 {sourceFiles.Length} 张";
            NameSection.Visibility = Visibility.Collapsed;
        }
    }

    private void PickSourcesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要转换格式的本地图片",
            Multiselect = true,
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.avif;*.heic;*.heif|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        sourceFiles = dialog.FileNames;
        if (string.IsNullOrWhiteSpace(FolderBox.Text) && sourceFiles.Length > 0)
            FolderBox.Text = Path.GetDirectoryName(sourceFiles[0]) ?? "";
        UpdateSourceSummary();
    }

    private void FormatChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        RefreshFormatState();
    }

    private void QualitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        QualityValText.Text = $"{Quality}";
    }

    private void RefreshFormatState()
    {
        var fmt = ImageFormat;
        bool hasQuality = fmt is StillFrameFormat.Jpg or StillFrameFormat.Webp or StillFrameFormat.Heic or StillFrameFormat.Avif;
        QualityPanel.Visibility = hasQuality ? Visibility.Visible : Visibility.Collapsed;
        WebpLosslessCheck.Visibility = fmt == StillFrameFormat.Webp ? Visibility.Visible : Visibility.Collapsed;
        QualitySlider.IsEnabled = !(fmt == StillFrameFormat.Webp && WebpLosslessCheck.IsChecked == true);
        QualityValText.Text = QualitySlider.IsEnabled ? $"{Quality}" : "无损";
        FormatDescText.Text = fmt switch
        {
            StillFrameFormat.Jpg => "JPG 高清有损压缩 · 体积小，兼容全部平台与设备",
            StillFrameFormat.Webp => Lossless ? "WebP 无损模式 · 体积比 PNG 更小且零失真" : "WebP 现代高压缩 · 同画质体积显著小于 JPG/PNG",
            StillFrameFormat.Heic => "HEIC（HEIF / H.265）· 苹果与现代相册标准，体积仅为 JPG 一半左右",
            StillFrameFormat.Bmp => "BMP 未压缩位图 · 零编码损耗，适合 Windows 原生处理",
            StillFrameFormat.Tiff => "TIFF 无损归档（Deflate）· 适合后期修图与印刷",
            StillFrameFormat.Avif => "AVIF 新一代 AV1 静态图 · 极致压缩比与高画质",
            _ => "PNG 无损压缩 · 保留完整像素与透明通道"
        };
    }

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择图片保存文件夹" };
        if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (convertMode && sourceFiles.Length == 0)
        {
            ShowErr("请先选择至少一张源图片。");
            return;
        }
        string folder = FolderBox.Text.Trim();
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
        {
            ShowErr("请选择有效的保存文件夹。");
            return;
        }
        string ext = ExportOptions.ImageExtension(ImageFormat);

        if (convertMode && sourceFiles.Length > 1)
        {
            var planned = sourceFiles.Select(src => Path.GetFullPath(Path.Combine(folder, Path.GetFileNameWithoutExtension(src) + ext))).ToArray();
            if (planned.Any(p => sourceFiles.Contains(p, StringComparer.OrdinalIgnoreCase)))
            {
                ShowErr("批量输出路径与源图片同名，请更换目标文件夹或目标格式。");
                return;
            }
            var existing = planned.Where(File.Exists).Select(Path.GetFileName).ToArray();
            if (existing.Length > 0 && MessageBox.Show(this,
                    $"目标文件夹已存在 {existing.Length} 个同名文件，是否替换？",
                    "确认替换", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            ReplaceExisting = existing.Length > 0;
            OutputPath = planned[0];
            DialogResult = true;
            return;
        }

        string name = NameBox.Text.Trim();
        foreach (string strip in new[] { ".png", ".jpg", ".jpeg", ".webp", ".heic", ".heif", ".bmp", ".tif", ".tiff", ".avif" })
            if (name.EndsWith(strip, StringComparison.OrdinalIgnoreCase)) name = name[..^strip.Length].TrimEnd();
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowErr("请输入有效的文件名。");
            return;
        }
        string full = Path.GetFullPath(Path.Combine(folder, name + ext));
        if (convertMode && sourceFiles.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            ShowErr("输出文件不能覆盖源图片，请修改文件名、格式或目录。");
            return;
        }
        bool exists = File.Exists(full);
        if (exists && MessageBox.Show(this, $"「{Path.GetFileName(full)}」已存在，是否替换？",
                "确认替换", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        ReplaceExisting = exists;
        OutputPath = full;
        DialogResult = true;
    }

    private void ShowErr(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowColors.Apply(this);
    }
}
