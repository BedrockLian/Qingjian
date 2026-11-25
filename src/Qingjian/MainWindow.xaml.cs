using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Qingjian;

public partial class MainWindow : Window
{
    private sealed record AssetRow(MediaAsset Asset, ImageSource? Thumbnail);
    private sealed record EditState(EditProject Project, TimelineSelection Selection, double Head);
    private sealed record HomeDraftRow(string Path, string Name, string Format, string DurationText, string MetaText, ImageSource? Thumbnail);

    private readonly MediaTools tools = new();
    private readonly ObservableCollection<AssetRow> rows = [];
    private readonly ObservableCollection<AssetRow> audioRows = [];
    private readonly ICollectionView assetView;
    private readonly Dictionary<string, AssetRow> assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> imageFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float[]> waveforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> proxies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> attemptedProxies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<EditState> undo = new(), redo = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(24) };
    private readonly string cache = Path.Combine(Path.GetTempPath(), $"Qingjian-{Guid.NewGuid():N}");
    private EditProject project = EditProject.Empty;
    private EditProject saved;
    private string? projectPath, sourcePath, lastExportFolder, exportedPath;
    private string assetQuery = "";
    private AssetRow? draggedAsset;
    private Point assetDragStart;
    private const string AssetDragFormat = "Qingjian.AssetPath";
    private TimelineSelection selection = TimelineSelection.None;
    private int activeClip = -1;
    private bool ready, refreshing, fitting, scaleFollowsFit = true, playing, playerReady, mainShowingImage, inHomeScreen = true, importing, saving, configuringExport, closing;
    private double head, activeStart, pendingLocal, duration, fitScale = 66, imagePlayStartHead;
    private DateTime imagePlayAnchorUtc;
    private CancellationTokenSource? exporting;
    private CancellationTokenSource? previewing;
    private EditProject? renderedProject;
    private string? renderedPreview;
    private bool UsingRender => renderedPreview is not null && ReferenceEquals(renderedProject, project) &&
        sourcePath == renderedPreview;
    private bool Dirty => !ReferenceEquals(project, saved);
    private bool Busy => importing || saving || configuringExport || exporting is not null || previewing is not null ||
        exportedPath is not null || closing;

    public MainWindow()
    {
        saved = project;
        InitializeComponent();
        Directory.CreateDirectory(cache);
        assetView = CollectionViewSource.GetDefaultView(rows);
        assetView.Filter = item => item is AssetRow row && row.Asset.Name.Contains(assetQuery, StringComparison.OrdinalIgnoreCase);
        AssetList.ItemsSource = assetView;
        AudioAssetList.ItemsSource = audioRows;
        Timeline.GetAsset = path => assets.GetValueOrDefault(path)?.Asset;
        Timeline.GetThumbnail = path => assets.GetValueOrDefault(path)?.Thumbnail;
        Timeline.GetWaveform = path => waveforms.GetValueOrDefault(path);
        Timeline.GetTrackFlags = id => project.GetTrack(id);
        Timeline.SelectionChanged += SelectItem;
        Timeline.Seeked += position => Seek(position, false);
        Timeline.MainClipTrimmed += TrimMainClip;
        Timeline.OverlayEdited += ApplyOverlayTimelineEdit;
        Timeline.TitleEdited += ApplyTitleTimelineEdit;
        Timeline.AudioEdited += ApplyAudioTimelineEdit;
        Timeline.ItemDoubleClicked += OnTimelineItemDoubleClicked;
        Timeline.ContextMenuRequested += ShowTimelineContextMenu;
        timer.Tick += PlaybackTick;
        timer.Start();
        ready = true;
        Refresh();
        ShowHomeScreen();
        if (!tools.Available) SetStatus("导入时会提示选择 FFmpeg 所在文件夹。");
    }

    // --- Home Launcher Screen ---

    private void ShowHomeScreen()
    {
        Pause();
        inHomeScreen = true;
        HomeScreen.Visibility = Visibility.Visible;
        EditorTopNav.Visibility = Visibility.Collapsed;
        EditorTopCenter.Visibility = Visibility.Collapsed;
        ExportButton.Visibility = Visibility.Collapsed;
        RefreshHomeDrafts();
    }

    private void EnterEditorScreen()
    {
        inHomeScreen = false;
        HomeScreen.Visibility = Visibility.Collapsed;
        EditorTopNav.Visibility = Visibility.Visible;
        EditorTopCenter.Visibility = Visibility.Visible;
        ExportButton.Visibility = Visibility.Visible;
        Refresh();
        RefreshLiveCanvas();
    }

    private void RefreshHomeDrafts()
    {
        string query = HomeDraftSearch.Text.Trim();
        var all = RecentProjectsStore.Load();
        var filtered = string.IsNullOrEmpty(query)
            ? all
            : all.Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        var cards = filtered.Select(e =>
        {
            string sizeStr = "";
            try
            {
                if (File.Exists(e.Path))
                {
                    long b = new FileInfo(e.Path).Length;
                    sizeStr = b >= 1024 * 1024 ? $"{b / (1024.0 * 1024.0):0.#} MB" : $"{Math.Max(1, b / 1024.0):0.#} KB";
                }
            }
            catch { }
            string timeStr = e.LastOpenedUtc.ToLocalTime().ToString("MM/dd HH:mm");
            string meta = string.IsNullOrEmpty(sizeStr) ? timeStr : $"{sizeStr}  ·  {timeStr}";
            string durText = e.DurationSeconds > 0 ? EditTime.Display(e.DurationSeconds) : $"{e.ClipCount} 段";
            return new HomeDraftRow(e.Path, e.Name, string.IsNullOrWhiteSpace(e.Format) ? "16:9" : e.Format,
                durText, meta, TryLoadCachedThumb(e.ThumbnailPath));
        }).ToList();

        HomeDraftList.ItemsSource = cards;
        HomeDraftCount.Text = string.IsNullOrEmpty(query) ? $"{all.Count}" : $"{cards.Count}/{all.Count}";
        HomeDraftEmpty.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeDraftEmptyTitle.Text = all.Count == 0 ? "暂无本地草稿" : "没有匹配的草稿";
        HomeDraftEmptyHint.Text = all.Count == 0
            ? "点击上方「开始创作」新建项目，或直接将 .qingjian 草稿与视频文件拖入窗口"
            : "试试其他草稿名称";
    }

    private static ImageSource? TryLoadCachedThumb(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private string? SaveDraftCoverThumb(string projectFilePath)
    {
        try
        {
            if (project.Clips.Length == 0) return null;
            if (!assets.TryGetValue(project.Clips[0].Path, out var row) || row.Thumbnail is not BitmapSource src) return null;
            Directory.CreateDirectory(RecentProjectsStore.ThumbDirectory);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(projectFilePath).ToLowerInvariant())))[..16];
            string outPath = Path.Combine(RecentProjectsStore.ThumbDirectory, $"{hash}.jpg");
            var enc = new JpegBitmapEncoder { QualityLevel = 84 };
            enc.Frames.Add(BitmapFrame.Create(src));
            using var fs = File.Create(outPath);
            enc.Save(fs);
            return outPath;
        }
        catch { return null; }
    }

    private void HomeStartEditClick(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        ApplyProject(EditProject.Empty, null);
        EnterEditorScreen();
        SetStatus("已新建本地视频项目");
    }

    private void HomeImageConvertClick(object sender, RoutedEventArgs e) => OpenImageConverter([]);

    private async void HomeOpenDraftFileClick(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        var dialog = new OpenFileDialog { Title = "打开清剪项目", Filter = "清剪项目|*.qingjian" };
        if (dialog.ShowDialog(this) != true || !EnsureTools()) return;
        await OpenProjectFileAsync(dialog.FileName);
    }

    private async void HomeDraftCardClick(object sender, RoutedEventArgs e)
    {
        if (Busy || sender is not FrameworkElement { Tag: HomeDraftRow card }) return;
        if (!File.Exists(card.Path))
        {
            ShowError("找不到该草稿文件，可能已被移动或删除。");
            RecentProjectsStore.Remove(card.Path);
            RefreshHomeDrafts();
            return;
        }
        if (!EnsureTools()) return;
        await OpenProjectFileAsync(card.Path);
    }

    private void HomeDraftRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Busy || sender is not FrameworkElement { Tag: HomeDraftRow card } el) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = el, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        var openItem = new MenuItem { Header = "打开草稿" };
        openItem.Click += async (_, _) => { if (EnsureTools()) await OpenProjectFileAsync(card.Path); };
        menu.Items.Add(openItem);

        var revealItem = new MenuItem { Header = "打开文件所在位置" };
        revealItem.Click += (_, _) => RevealInExplorer(card.Path);
        menu.Items.Add(revealItem);

        menu.Items.Add(new Separator());

        var removeItem = new MenuItem { Header = "从草稿列表移除" };
        removeItem.Click += (_, _) =>
        {
            RecentProjectsStore.Remove(card.Path);
            RefreshHomeDrafts();
        };
        menu.Items.Add(removeItem);

        menu.IsOpen = true;
    }

    private void HomeDraftSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (ready) RefreshHomeDrafts();
    }

    private async void BackToHomeClick(object sender, RoutedEventArgs e)
    {
        if (Busy || !await ConfirmSaveAsync()) return;
        Pause();
        StopMedia("把视频加入时间线后即可预览");
        ShowHomeScreen();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowColors.Apply(this);
    }

    // --- Frameless WindowChrome Controls ---

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindowClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        bool max = WindowState == WindowState.Maximized;
        WindowRootBorder.Padding = max ? new Thickness(7) : new Thickness(0);
        WindowRootBorder.BorderThickness = max ? new Thickness(0) : new Thickness(1);
        MaximizeGlyph.Data = Geometry.Parse(max
            ? "M 2,0.5 L 9.5,0.5 L 9.5,8 M 0.5,2.5 L 7.5,2.5 L 7.5,9.5 L 0.5,9.5 Z"
            : "M 0.5,0.5 L 9.5,0.5 L 9.5,9.5 L 0.5,9.5 Z");
    }

    // --- Top Bar Recent Projects Menu ---

    private void ProjectMenuClick(object sender, RoutedEventArgs e)
    {
        var recents = RecentProjectsStore.Load();
        RecentList.ItemsSource = recents;
        RecentEmptyText.Visibility = recents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentPopup.IsOpen = !RecentPopup.IsOpen;
    }

    private async void RecentProjectClick(object sender, RoutedEventArgs e)
    {
        RecentPopup.IsOpen = false;
        if (Busy || sender is not FrameworkElement { Tag: string path } || !File.Exists(path)) return;
        if (!await ConfirmSaveAsync() || !EnsureTools()) return;
        await OpenProjectFileAsync(path);
    }

    private async void SaveAsClick(object sender, RoutedEventArgs e)
    {
        RecentPopup.IsOpen = false;
        if (!Busy) await SaveAsync(forceSaveAs: true);
    }

    private void OpenFromMenuClick(object sender, RoutedEventArgs e)
    {
        RecentPopup.IsOpen = false;
        OpenClick(sender, e);
    }

    // --- Left 3-Tab Resource Panel ---

    private void ResourceTabChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        MediaTabView.Visibility = TabMedia.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AudioTabView.Visibility = TabAudio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TextTabView.Visibility = TabText.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool EnsureTools()
    {
        if (tools.Available) return true;
        var folder = new OpenFolderDialog { Title = "请选择包含 ffmpeg.exe 和 ffprobe.exe 的文件夹" };
        if (folder.ShowDialog(this) != true) return false;
        if (tools.UseDirectory(folder.FolderName)) return true;
        ShowError("此文件夹需要同时包含 ffmpeg.exe 和 ffprobe.exe。");
        return false;
    }

    private async void ImportClick(object sender, RoutedEventArgs e)
    {
        if (Busy || !EnsureTools()) return;
        var dialog = new OpenFileDialog
        {
            Title = "导入本地视频、图片或音频", Multiselect = true,
            Filter = "媒体文件|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v;*.ts;*.mts;*.m2ts;*.wmv;*.mpg;*.mxf;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.avif;*.heic;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.aiff|" +
                     "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.avif;*.heic;*.heif|" +
                     "视频文件|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v;*.ts;*.mts;*.m2ts;*.wmv;*.mpg;*.mxf|" +
                     "音频文件|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.aiff|" +
                     "所有文件|*.*"
        };
        if (dialog.ShowDialog(this) == true) await ImportAsync(dialog.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !Busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Busy || e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0 || !EnsureTools()) return;
        string? projFile = files.FirstOrDefault(f => f.EndsWith(".qingjian", StringComparison.OrdinalIgnoreCase));
        if (projFile is not null)
        {
            if (inHomeScreen || await ConfirmSaveAsync()) await OpenProjectFileAsync(projFile);
            return;
        }
        if (inHomeScreen)
        {
            ApplyProject(EditProject.Empty, null);
            EnterEditorScreen();
        }
        await ImportAsync(files);
    }

    private async Task ImportAsync(IEnumerable<string> files)
    {
        importing = true;
        RefreshButtons();
        List<string> added = [], errors = [];
        try
        {
            foreach (string file in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string path = Path.GetFullPath(file);
                if (project.Assets.Contains(path, StringComparer.OrdinalIgnoreCase) || added.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                SetStatus($"正在读取 {Path.GetFileName(path)}…");
                try
                {
                    await ReadAssetAsync(path, allowAudioOnly: true);
                    added.Add(path);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    errors.Add($"{Path.GetFileName(path)} · {FirstLine(error.Message)}");
                }
            }
            if (added.Count > 0) Commit(project with { Assets = [.. project.Assets, .. added] }, selection, head);
            if (added.Count > 0)
                AssetList.SelectedItem = added.Select(path => assets[path]).FirstOrDefault(assetView.Contains);
            SetStatus(added.Count > 0 ? $"已导入 {added.Count} 个本地素材" : "没有添加新素材");
        }
        catch (OperationCanceledException) { }
        finally
        {
            importing = false;
            RefreshButtons();
        }
        if (errors.Count > 0) ShowError(string.Join("\n", errors.Take(8)) + (errors.Count > 8 ? $"\n另有 {errors.Count - 8} 个文件。" : ""));
    }

    private async Task ReadAssetAsync(string path, bool allowAudioOnly = false)
    {
        var asset = await tools.ProbeAsync(path, lifetime.Token, allowAudioOnly);
        var thumbTask = asset.VideoStream < 0 ? Task.FromResult<ImageSource?>(null) : ReadThumbnailAsync(asset, lifetime.Token);
        var waveTask = asset.AudioStream is null || waveforms.ContainsKey(path)
            ? Task.FromResult<float[]?>(null)
            : tools.WaveformAsync(asset, lifetime.Token);
        var imgTask = asset.IsImage && !imageFrames.ContainsKey(path)
            ? ReadFullImageAsync(asset, lifetime.Token)
            : Task.FromResult<ImageSource?>(null);
        await Task.WhenAll(thumbTask, waveTask, imgTask);
        assets[path] = new AssetRow(asset, thumbTask.Result ?? imgTask.Result);
        if (imgTask.Result is not null) imageFrames[path] = imgTask.Result;
        if (waveTask.Result is { Length: > 0 } peaks) waveforms[path] = peaks;
    }

    private async Task<ImageSource?> ReadFullImageAsync(MediaAsset asset, CancellationToken token)
    {
        string decoded = Path.Combine(cache, $"{Guid.NewGuid():N}.png");
        try
        {
            await tools.DecodeImagePreviewAsync(asset, decoded, token);
            if (!File.Exists(decoded)) return null;
            using var input = File.OpenRead(decoded);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = input;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
        finally { if (File.Exists(decoded)) File.Delete(decoded); }
    }

    private async Task<ImageSource?> ReadThumbnailAsync(MediaAsset asset, CancellationToken token, double? timestamp = null)
    {
        string preview = Path.Combine(cache, $"{Guid.NewGuid():N}.jpg");
        try
        {
            await tools.ThumbnailAsync(asset, preview, token, timestamp);
            if (!File.Exists(preview)) return null;
            using var input = File.OpenRead(preview);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = input;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (error is MediaException or NotSupportedException or FileFormatException) { return null; }
        finally { File.Delete(preview); }
    }

    private void AssetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready) RefreshButtons();
    }

    private void AssetSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready) return;
        assetQuery = AssetSearch.Text.Trim();
        assetView.Refresh();
        RefreshAssetPanel();
        RefreshButtons();
    }

    private void RefreshAssetPanel()
    {
        int visible = AssetList.Items.Count;
        bool hasAny = rows.Count > 0;
        AssetCount.Text = visible == rows.Count ? $"{visible}" : $"{visible}/{rows.Count}";
        ImportButton.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
        AssetEmpty.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        AssetEmpty.IsHitTestVisible = !hasAny;
        AssetEmptyKbd.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
        AssetEmptyTitle.Text = hasAny ? "没有匹配的素材" : "导入或拖入素材";
        AssetEmptyHint.Text = hasAny ? "试试其他文件名" : "点击导入，或直接拖入视频、图片、音频";
        AudioEmptyHint.Visibility = audioRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AssetMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && FindAncestor<Button>(src) is not null) { draggedAsset = null; return; }
        draggedAsset = (ItemsControl.ContainerFromElement(AssetList, e.OriginalSource as DependencyObject) as ListBoxItem)?.Content as AssetRow;
        assetDragStart = e.GetPosition(AssetList);
    }

    private void AssetRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Busy) return;
        var container = ItemsControl.ContainerFromElement(AssetList, e.OriginalSource as DependencyObject) as ListBoxItem;
        var row = container?.Content as AssetRow;
        if (row is not null) AssetList.SelectedItem = row;
        e.Handled = true;
        ShowAssetContextMenu(row, AssetList, audioTab: false);
    }

    private void AudioAssetRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Busy || sender is not FrameworkElement { Tag: AssetRow row } el) return;
        e.Handled = true;
        ShowAssetContextMenu(row, el, audioTab: true);
    }

    private void ShowAssetContextMenu(AssetRow? row, FrameworkElement target, bool audioTab)
    {
        var menu = new ContextMenu();
        int unusedCount = CountUnusedAssets();

        if (row is not null)
        {
            var asset = row.Asset;
            if (audioTab || asset.IsAudioOnly)
            {
                var addA2 = new MenuItem { Header = "添加到独立音轨（A2）" };
                addA2.Click += (_, _) => AddAudio(asset, head);
                menu.Items.Add(addA2);
            }
            else
            {
                var addV1 = new MenuItem { Header = "添加到主轨末尾（V1）" };
                addV1.Click += (_, _) => InsertMain(asset, project.Clips.Length, duration);
                menu.Items.Add(addV1);

                var addV2 = new MenuItem { Header = "在播放头添加为画中画（V2）" };
                addV2.Click += (_, _) => InsertOverlay(asset, head);
                menu.Items.Add(addV2);

                if (asset.AudioStream is not null)
                {
                    var addAud = new MenuItem { Header = "在播放头仅提取音频到 A2" };
                    addAud.Click += (_, _) => AddAudio(asset, head);
                    menu.Items.Add(addAud);
                }

                if (asset.IsImage)
                {
                    var convItem = new MenuItem { Header = "转换图片格式…" };
                    convItem.Click += (_, _) => OpenImageConverter([asset.Path]);
                    menu.Items.Add(convItem);
                }
            }

            menu.Items.Add(new Separator());

            var replaceItem = new MenuItem { Header = "替换素材文件…" };
            replaceItem.Click += async (_, _) => await ReplaceAssetAsync(asset);
            menu.Items.Add(replaceItem);

            var revealItem = new MenuItem { Header = "打开文件所在位置" };
            revealItem.Click += (_, _) => RevealInExplorer(asset.Path);
            menu.Items.Add(revealItem);

            var copyPathItem = new MenuItem { Header = "复制文件路径" };
            copyPathItem.Click += (_, _) =>
            {
                try { Clipboard.SetText(asset.Path); SetStatus("已复制素材路径到剪贴板"); }
                catch { }
            };
            menu.Items.Add(copyPathItem);

            var infoItem = new MenuItem { Header = "素材信息" };
            infoItem.Click += (_, _) => ShowAssetInfo(asset);
            menu.Items.Add(infoItem);

            menu.Items.Add(new Separator());

            var removeItem = new MenuItem { Header = "从素材库移除", InputGestureText = "Delete" };
            removeItem.Click += (_, _) => RemoveAssetFromLibrary(asset);
            menu.Items.Add(removeItem);
        }
        else
        {
            var impItem = new MenuItem { Header = "导入本地素材…", InputGestureText = "Ctrl+I" };
            impItem.Click += (_, _) => ImportClick(this, new RoutedEventArgs());
            menu.Items.Add(impItem);

            var convAny = new MenuItem { Header = "图片格式转换…" };
            convAny.Click += (_, _) => OpenImageConverter([]);
            menu.Items.Add(convAny);

            menu.Items.Add(new Separator());
        }

        var cleanItem = new MenuItem
        {
            Header = unusedCount > 0 ? $"清理未使用素材（{unusedCount} 项）" : "清理未使用素材",
            IsEnabled = unusedCount > 0
        };
        cleanItem.Click += (_, _) => RemoveUnusedAssets();
        menu.Items.Add(cleanItem);

        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private int CountAssetUsage(string path) =>
        project.Clips.Count(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) +
        project.OverlayTrack.Count(o => o.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) +
        project.AudioTrack.Count(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    private int CountUnusedAssets() => project.Assets.Count(p => CountAssetUsage(p) == 0);

    private void RemoveAssetFromLibrary(MediaAsset asset)
    {
        if (Busy) return;
        int used = CountAssetUsage(asset.Path);
        if (used > 0 && MessageBox.Show(this,
                $"「{asset.Name}」已添加到时间线（共 {used} 处）。\n移除素材后，轨道中对应片段将被一同删除，确定移除吗？",
                "从素材库移除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        string[] nextAssets = project.Assets.Where(p => !p.Equals(asset.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
        Clip[] nextClips = project.Clips.Where(c => !c.Path.Equals(asset.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
        OverlayClip[] nextOverlays = project.OverlayTrack.Where(o => !o.Path.Equals(asset.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
        AudioLayer[] nextAudios = project.AudioTrack.Where(a => !a.Path.Equals(asset.Path, StringComparison.OrdinalIgnoreCase)).ToArray();

        Commit(project with { Assets = nextAssets, Clips = nextClips, Overlays = nextOverlays, Audio = nextAudios },
            TimelineSelection.None, head);
        SetStatus(used > 0 ? $"已从素材库及时间线移除：{asset.Name}" : $"已从素材库移除：{asset.Name}");
    }

    private void RemoveUnusedAssets()
    {
        if (Busy) return;
        string[] keep = project.Assets.Where(p => CountAssetUsage(p) > 0).ToArray();
        int removed = project.Assets.Length - keep.Length;
        if (removed <= 0) { SetStatus("没有未使用的素材"); return; }
        Commit(project with { Assets = keep }, selection, head);
        SetStatus($"已清理 {removed} 个未使用素材");
    }

    private async Task ReplaceAssetAsync(MediaAsset oldAsset)
    {
        if (Busy || !EnsureTools()) return;
        var dialog = new OpenFileDialog
        {
            Title = $"替换素材「{oldAsset.Name}」",
            Filter = oldAsset.IsAudioOnly
                ? "音频文件|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.aiff;*.mp4;*.mov|所有文件|*.*"
                : "视频与图片|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v;*.ts;*.mts;*.m2ts;*.wmv;*.mpg;*.mxf;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.avif;*.heic|所有文件|*.*"
        };
        if (Directory.Exists(Path.GetDirectoryName(oldAsset.Path)))
            dialog.InitialDirectory = Path.GetDirectoryName(oldAsset.Path);
        if (dialog.ShowDialog(this) != true) return;

        string newPath = Path.GetFullPath(dialog.FileName);
        if (newPath.Equals(oldAsset.Path, StringComparison.OrdinalIgnoreCase)) return;

        importing = true;
        RefreshButtons();
        try
        {
            SetStatus($"正在读取替换素材 {Path.GetFileName(newPath)}…");
            await ReadAssetAsync(newPath, allowAudioOnly: oldAsset.IsAudioOnly);
            var newAsset = assets[newPath].Asset;
            if (!oldAsset.IsAudioOnly && newAsset.IsAudioOnly)
            {
                ShowError("所选文件仅包含音频，无法替换视频或图片素材。");
                return;
            }

            string[] nextAssets = project.Assets
                .Select(p => p.Equals(oldAsset.Path, StringComparison.OrdinalIgnoreCase) ? newPath : p)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Clip[] nextClips = project.Clips.Select(c =>
            {
                if (!c.Path.Equals(oldAsset.Path, StringComparison.OrdinalIgnoreCase)) return c;
                double maxDur = newAsset.Duration;
                double nIn = Math.Clamp(c.In, 0, Math.Max(0, maxDur - 0.1));
                double nOut = Math.Clamp(c.Out, nIn + 0.1, maxDur);
                return c with { Path = newPath, In = nIn, Out = nOut };
            }).ToArray();

            OverlayClip[] nextOverlays = project.OverlayTrack.Select(o =>
            {
                if (!o.Path.Equals(oldAsset.Path, StringComparison.OrdinalIgnoreCase)) return o;
                double maxDur = newAsset.Duration;
                double nIn = Math.Clamp(o.In, 0, Math.Max(0, maxDur - 0.1));
                double nOut = Math.Clamp(o.Out, nIn + 0.1, maxDur);
                return o with { Path = newPath, In = nIn, Out = nOut };
            }).ToArray();

            AudioLayer[] nextAudios = project.AudioTrack.Select(a =>
            {
                if (!a.Path.Equals(oldAsset.Path, StringComparison.OrdinalIgnoreCase)) return a;
                if (newAsset.AudioStream is null) return a;
                double maxDur = newAsset.Duration;
                double nIn = Math.Clamp(a.In, 0, Math.Max(0, maxDur - 0.1));
                double nOut = Math.Clamp(a.Out, nIn + 0.1, maxDur);
                return a with { Path = newPath, In = nIn, Out = nOut };
            }).ToArray();

            Commit(project with { Assets = nextAssets, Clips = nextClips, Overlays = nextOverlays, Audio = nextAudios },
                selection, head);
            AssetList.SelectedItem = assets.GetValueOrDefault(newPath);
            SetStatus($"已将「{oldAsset.Name}」替换为「{newAsset.Name}」");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowError("替换素材失败：" + FirstLine(error.Message));
        }
        finally
        {
            importing = false;
            RefreshButtons();
        }
    }

    private void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(Path.GetDirectoryName(path)))
                Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true });
            else
                ShowError("找不到该文件，可能已被移动或删除。");
        }
        catch (Win32Exception error) { ShowError(FirstLine(error.Message)); }
    }

    private void ShowAssetInfo(MediaAsset asset)
    {
        string type = asset.IsAudioOnly ? "本地音频" : asset.IsImage ? "本地图片" : "本地视频";
        string sizeText = "未知";
        try
        {
            if (File.Exists(asset.Path))
            {
                double bytes = new FileInfo(asset.Path).Length;
                sizeText = bytes >= 1024 * 1024 * 1024 ? $"{bytes / (1024 * 1024 * 1024):0.##} GB"
                    : bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024):0.##} MB"
                    : $"{bytes / 1024:0.#} KB";
            }
        }
        catch { }

        int used = CountAssetUsage(asset.Path);
        string details =
            $"文件名称：{asset.Name}\n" +
            $"素材类型：{type}\n" +
            (asset.IsAudioOnly ? "" : $"画面尺寸：{asset.Width} × {asset.Height}\n") +
            (asset.IsImage ? "默认时长：5.0 秒（入轨后可自由拖拽至 300 秒）\n" : $"素材时长：{EditTime.Display(asset.Duration, true)}\n") +
            $"编码格式：{asset.Codec.ToUpperInvariant()}\n" +
            $"音频轨道：{(asset.AudioStream is not null ? "包含音轨" : "无音频")}\n" +
            $"文件大小：{sizeText}\n" +
            $"时间线引用：{(used > 0 ? $"已引用 {used} 处" : "暂未加入轨道")}\n" +
            $"文件路径：{asset.Path}";
        MessageBox.Show(this, details, "素材信息", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void AssetMouseUp(object sender, MouseButtonEventArgs e) => draggedAsset = null;

    private void AssetMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) draggedAsset = null;
        if (Busy || draggedAsset is not { } row) return;
        Vector delta = e.GetPosition(AssetList) - assetDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        draggedAsset = null;
        var data = new DataObject(AssetDragFormat, row.Asset.Path);
        try { DragDrop.DoDragDrop(AssetList, data, DragDropEffects.Copy); }
        finally { Timeline.ShowDropIndicator(null, null); }
    }

    private void TimelineDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(AssetDragFormat)) return;
        e.Effects = Busy ? DragDropEffects.None : DragDropEffects.Copy;
        if (!Busy)
        {
            var (trackId, _, time) = Timeline.DropTargetAt(e.GetPosition(Timeline));
            Timeline.ShowDropIndicator(trackId, time);
        }
        e.Handled = true;
    }

    private void TimelineDragLeave(object sender, DragEventArgs e) => Timeline.ShowDropIndicator(null, null);

    private void TimelineDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(AssetDragFormat)) return;
        e.Handled = true;
        Timeline.ShowDropIndicator(null, null);
        if (Busy || e.Data.GetData(AssetDragFormat) is not string path ||
            !project.Assets.Contains(path, StringComparer.OrdinalIgnoreCase) || !assets.TryGetValue(path, out var row)) return;
        var (trackId, index, time) = Timeline.DropTargetAt(e.GetPosition(Timeline));
        if (row.Asset.IsAudioOnly || trackId == "A2")
            AddAudio(row.Asset, time);
        else if (trackId == "V2")
            InsertOverlay(row.Asset, time);
        else
            InsertMain(row.Asset, index, time);
    }

    private void AssetDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(AssetList, e.OriginalSource as DependencyObject) is ListBoxItem) Append();
    }

    private void AppendClick(object sender, RoutedEventArgs e) => Append();

    private void AddSelectedOverlayClick(object sender, RoutedEventArgs e)
    {
        if (Busy || AssetList.SelectedItem is not AssetRow row || row.Asset.IsAudioOnly) return;
        InsertOverlay(row.Asset, head);
    }

    private void QuickAppendClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Busy || sender is not FrameworkElement { Tag: AssetRow row }) return;
        AssetList.SelectedItem = row;
        if (row.Asset.IsAudioOnly) AddAudio(row.Asset, head);
        else InsertMain(row.Asset, project.Clips.Length, duration);
    }

    private void QuickOverlayClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Busy || sender is not FrameworkElement { Tag: AssetRow row } || row.Asset.IsAudioOnly) return;
        AssetList.SelectedItem = row;
        InsertOverlay(row.Asset, head);
    }

    private void Append()
    {
        if (Busy || AssetList.SelectedItem is not AssetRow row) return;
        if (row.Asset.IsAudioOnly) AddAudio(row.Asset, head);
        else InsertMain(row.Asset, project.Clips.Length, duration);
    }

    private void InsertMain(MediaAsset asset, int index, double time)
    {
        double dur = asset.IsImage ? MediaAsset.DefaultImageDuration : asset.Duration;
        var clip = new Clip(asset.Path, 0, dur);
        Commit(project with { Clips = [.. project.Clips[..index], clip, .. project.Clips[index..]] },
            new TimelineSelection(SelectionKind.MainClip, index), time);
        SetStatus($"已添加到主轨 V1：{asset.Name}");
    }

    private void InsertOverlay(MediaAsset asset, double start)
    {
        double dur = asset.IsImage ? MediaAsset.DefaultImageDuration : asset.Duration;
        var ov = new OverlayClip(asset.Path, Math.Max(0, start), 0, dur, 0, 1, null, new TransformSpec(0, 0, 0.45, 0, 1));
        var next = project.OverlayTrack.Append(ov).ToArray();
        Commit(project with { Overlays = next }, new TimelineSelection(SelectionKind.Overlay, next.Length - 1), Math.Max(0, start));
        SetStatus($"已添加画中画到 V2：{asset.Name}（可在画布直接拖拽位置与缩放）");
    }

    // --- Commit & History ---

    private void Commit(EditProject next, TimelineSelection nextSelection, double position)
    {
        Pause();
        undo.Push(new EditState(project, selection, head));
        redo.Clear();
        project = next;
        selection = NormalizeSelection(next, nextSelection);
        Refresh();
        Seek(position);
    }

    private static TimelineSelection NormalizeSelection(EditProject p, TimelineSelection sel) => sel.Kind switch
    {
        SelectionKind.MainClip when sel.Index >= 0 && sel.Index < p.Clips.Length => sel,
        SelectionKind.Overlay when sel.Index >= 0 && sel.Index < p.OverlayTrack.Length => sel,
        SelectionKind.Title when sel.Index >= 0 && sel.Index < p.TitleTrack.Length => sel,
        SelectionKind.Audio when sel.Index >= 0 && sel.Index < p.AudioTrack.Length => sel,
        _ => TimelineSelection.None
    };

    private void ApplyHistory(Stack<EditState> from, Stack<EditState> to)
    {
        if (Busy || !from.TryPop(out var edit)) return;
        Pause();
        to.Push(new EditState(project, selection, head));
        project = edit.Project;
        selection = NormalizeSelection(project, edit.Selection);
        cropMode = false;
        CropModeToggle.IsChecked = false;
        Refresh();
        Seek(edit.Head);
    }

    private void UndoClick(object sender, RoutedEventArgs e) => ApplyHistory(undo, redo);
    private void RedoClick(object sender, RoutedEventArgs e) => ApplyHistory(redo, undo);
    private void SplitClick(object sender, RoutedEventArgs e) => Split();
    private void DuplicateClick(object sender, RoutedEventArgs e) => DuplicateSelected();
    private void DeleteClick(object sender, RoutedEventArgs e) => Delete();
    private void LeftClick(object sender, RoutedEventArgs e) => Move(-1);
    private void RightClick(object sender, RoutedEventArgs e) => Move(1);

    private int ClipAt(double time, out double start)
    {
        start = 0;
        for (int i = 0; i < project.Clips.Length; i++)
        {
            double end = start + project.Clips[i].Duration;
            if (time < end - 0.00001 || i == project.Clips.Length - 1) return i;
            start = end;
        }
        return -1;
    }

    private double StartOf(int index) => project.Clips.Take(index).Sum(clip => clip.Duration);

    private void Split()
    {
        if (Busy) return;
        // Split selected overlay if playhead lies inside it
        if (selection.Kind == SelectionKind.Overlay && selection.Index >= 0 && selection.Index < project.OverlayTrack.Length)
        {
            var ov = project.OverlayTrack[selection.Index];
            if (head > ov.Start + EditTime.MinimumClip && head < ov.End - EditTime.MinimumClip)
            {
                double cut = ov.In + (head - ov.Start) * ov.EffectiveSpeed;
                var leftOv = ov with { Out = cut };
                var rightOv = ov with { Start = head, In = cut };
                var next = project.OverlayTrack[..selection.Index].Append(leftOv).Append(rightOv)
                    .Concat(project.OverlayTrack[(selection.Index + 1)..]).ToArray();
                Commit(project with { Overlays = next }, new TimelineSelection(SelectionKind.Overlay, selection.Index + 1), head);
                SetStatus("已在播放头处分割画中画");
                return;
            }
        }
        if (project.Clips.Length == 0) return;
        int index = ClipAt(head, out double start);
        var clip = project.Clips[index];
        double cutPoint = clip.In + (head - start) * clip.EffectiveSpeed;
        if (cutPoint - clip.In < EditTime.MinimumClip || clip.Out - cutPoint < EditTime.MinimumClip)
        {
            SetStatus("请将播放头放在片段内部，再分割。");
            return;
        }
        Commit(project with { Clips = [.. project.Clips[..index], clip with { Out = cutPoint, Dissolve = 0 }, clip with { In = cutPoint },
            .. project.Clips[(index + 1)..]] }, new TimelineSelection(SelectionKind.MainClip, index + 1), head);
        SetStatus("已在播放头处分割");
    }

    private void DuplicateSelected()
    {
        if (Busy || selection.IsNone) return;
        switch (selection.Kind)
        {
            case SelectionKind.MainClip:
            {
                var c = project.Clips[selection.Index] with { Dissolve = 0 };
                int insertAt = selection.Index + 1;
                Commit(project with { Clips = [.. project.Clips[..insertAt], c, .. project.Clips[insertAt..]] },
                    new TimelineSelection(SelectionKind.MainClip, insertAt), StartOf(selection.Index) + c.Duration);
                SetStatus("已复制主轨片段");
                break;
            }
            case SelectionKind.Overlay:
            {
                var ov = project.OverlayTrack[selection.Index];
                var copy = ov with { Start = ov.End };
                var next = project.OverlayTrack.Append(copy).ToArray();
                Commit(project with { Overlays = next }, new TimelineSelection(SelectionKind.Overlay, next.Length - 1), copy.Start);
                SetStatus("已复制画中画片段");
                break;
            }
            case SelectionKind.Title:
            {
                var t = project.TitleTrack[selection.Index];
                var copy = t with { Start = t.End };
                var next = project.TitleTrack.Append(copy).ToArray();
                Commit(project with { Titles = next }, new TimelineSelection(SelectionKind.Title, next.Length - 1), copy.Start);
                SetStatus("已复制文字片段");
                break;
            }
            case SelectionKind.Audio:
            {
                var a = project.AudioTrack[selection.Index];
                var copy = a with { Start = a.End };
                var next = project.AudioTrack.Append(copy).ToArray();
                Commit(project with { Audio = next }, new TimelineSelection(SelectionKind.Audio, next.Length - 1), copy.Start);
                SetStatus("已复制独立音频片段");
                break;
            }
        }
    }

    private void Delete()
    {
        if (Busy || selection.IsNone) return;
        cropMode = false;
        CropModeToggle.IsChecked = false;
        switch (selection.Kind)
        {
            case SelectionKind.MainClip:
            {
                double start = StartOf(selection.Index);
                int nextIdx = Math.Min(selection.Index, project.Clips.Length - 2);
                Commit(project with { Clips = [.. project.Clips[..selection.Index], .. project.Clips[(selection.Index + 1)..]] },
                    nextIdx >= 0 ? new TimelineSelection(SelectionKind.MainClip, nextIdx) : TimelineSelection.None, start);
                SetStatus("已删除主轨片段");
                break;
            }
            case SelectionKind.Overlay:
            {
                var next = project.OverlayTrack[..selection.Index].Concat(project.OverlayTrack[(selection.Index + 1)..]).ToArray();
                Commit(project with { Overlays = next }, TimelineSelection.None, head);
                SetStatus("已删除画中画片段");
                break;
            }
            case SelectionKind.Title:
            {
                var next = project.TitleTrack[..selection.Index].Concat(project.TitleTrack[(selection.Index + 1)..]).ToArray();
                Commit(project with { Titles = next }, TimelineSelection.None, head);
                SetStatus("已删除文字片段");
                break;
            }
            case SelectionKind.Audio:
            {
                var next = project.AudioTrack[..selection.Index].Concat(project.AudioTrack[(selection.Index + 1)..]).ToArray();
                Commit(project with { Audio = next }, TimelineSelection.None, head);
                SetStatus("已删除独立音频片段");
                break;
            }
        }
    }

    private void Move(int direction)
    {
        if (Busy || selection.Kind != SelectionKind.MainClip) return;
        int next = selection.Index + direction;
        if (next < 0 || next >= project.Clips.Length) return;
        var items = (Clip[])project.Clips.Clone();
        (items[selection.Index], items[next]) = (items[next], items[selection.Index]);
        Commit(project with { Clips = items }, new TimelineSelection(SelectionKind.MainClip, next),
            items.Take(next).Sum(clip => clip.Duration));
    }

    private void SelectItem(TimelineSelection next)
    {
        if (selection == next || Busy) return;
        selection = NormalizeSelection(project, next);
        cropMode = false;
        CropModeToggle.IsChecked = false;
        liveTransformDraft = null;
        liveCropDraft = null;
        liveTextStyleDraft = null;
        Timeline.Selection = selection;
        Timeline.InvalidateVisual();
        RefreshInspector();
        RefreshButtons();
        RefreshLiveCanvas();
    }

    private void OnTimelineItemDoubleClicked(TimelineSelection sel)
    {
        if (sel.Kind == SelectionKind.Title)
        {
            TitleTextBox.Focus();
            TitleTextBox.SelectAll();
        }
    }

    private void ShowTimelineContextMenu(TimelineSelection sel, Point pt)
    {
        if (Busy) return;
        var menu = new ContextMenu();
        if (!sel.IsNone)
        {
            if (sel.Kind is SelectionKind.MainClip or SelectionKind.Overlay)
            {
                var splitItem = new MenuItem { Header = "在播放头分割", InputGestureText = "Ctrl+B" };
                splitItem.Click += (_, _) => Split();
                menu.Items.Add(splitItem);
            }
            var dupItem = new MenuItem { Header = "复制片段", InputGestureText = "Ctrl+D" };
            dupItem.Click += (_, _) => DuplicateSelected();
            menu.Items.Add(dupItem);

            if (sel.Kind == SelectionKind.MainClip)
            {
                var clip = project.Clips[sel.Index];
                var asset = assets.GetValueOrDefault(clip.Path)?.Asset;
                var sepItem = new MenuItem { Header = "分离为独立音轨", IsEnabled = asset?.AudioStream is not null && clip.Volume > 0 };
                sepItem.Click += (_, _) => SeparateClick(this, new RoutedEventArgs());
                menu.Items.Add(sepItem);

                var expClipItem = new MenuItem { Header = "导出当前选中片段…" };
                expClipItem.Click += (_, _) => StartExportDialog(preferSelectedClip: true);
                menu.Items.Add(expClipItem);
            }
            if (sel.Kind is SelectionKind.MainClip or SelectionKind.Overlay)
            {
                var cropItem = new MenuItem { Header = "画布交互式裁剪" };
                cropItem.Click += (_, _) => EnterCropModeClick(this, new RoutedEventArgs());
                menu.Items.Add(cropItem);

                var resetItem = new MenuItem { Header = "重置变换与裁剪" };
                resetItem.Click += (_, _) =>
                {
                    if (sel.Kind == SelectionKind.MainClip)
                        UpdateMainClip(sel.Index, project.Clips[sel.Index] with { Transform = null, Crop = null });
                    else
                        UpdateOverlayClip(sel.Index, project.OverlayTrack[sel.Index] with { Transform = new TransformSpec(0, 0, 0.45, 0, 1), Crop = null });
                };
                menu.Items.Add(resetItem);
            }
            menu.Items.Add(new Separator());
            var delItem = new MenuItem { Header = "删除", InputGestureText = "Delete" };
            delItem.Click += (_, _) => Delete();
            menu.Items.Add(delItem);
        }
        else
        {
            var addTxt = new MenuItem { Header = "在播放头添加文字", IsEnabled = project.Clips.Length > 0 };
            addTxt.Click += (_, _) => AddTitleClick(this, new RoutedEventArgs());
            menu.Items.Add(addTxt);
            var addAud = new MenuItem { Header = "在播放头导入本地音频" };
            addAud.Click += (_, _) => ImportAudioClick(this, new RoutedEventArgs());
            menu.Items.Add(addAud);
        }
        menu.PlacementTarget = Timeline;
        menu.IsOpen = true;
    }

    // --- Trim & Volume (MainClip & Overlay) ---

    private void ApplyTrimClick(object sender, RoutedEventArgs e) => ApplyTrim();

    private void TrimTextKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyTrim();
        e.Handled = true;
    }

    private void ApplyTrim()
    {
        if (Busy) return;
        if (!EditTime.TryParse(InBox.Text, out double from) || !EditTime.TryParse(OutBox.Text, out double to))
        {
            SetStatus("请输入秒数或时:分:秒.毫秒。");
            return;
        }
        if (selection.Kind == SelectionKind.MainClip)
        {
            TrimMainClip(selection.Index, from, to);
        }
        else if (selection.Kind == SelectionKind.Overlay)
        {
            if (!EditTime.TryParse(OverlayStartBox.Text, out double start))
            {
                SetStatus("请输入有效的时间线起点。");
                return;
            }
            ApplyOverlayTimelineEdit(selection.Index, start, from, to);
        }
    }

    private void TrimMainClip(int index, double from, double to)
    {
        if (Busy || index < 0 || index >= project.Clips.Length) return;
        var clip = project.Clips[index];
        if (!assets.TryGetValue(clip.Path, out var row)) return;
        double maximum = row.Asset.Duration;
        if (Math.Abs(to - maximum) < 0.0006) to = maximum;
        if (from < 0 || to > maximum || to - from + 0.000001 < Math.Min(EditTime.MinimumClip, clip.Out - clip.In))
        {
            SetStatus($"请输入有效范围（0 至 {EditTime.Display(maximum, true)}）。");
            return;
        }
        if (Math.Abs(clip.In - from) < 0.00001 && Math.Abs(clip.Out - to) < 0.00001) return;
        var items = (Clip[])project.Clips.Clone();
        items[index] = clip with { In = from, Out = to };
        Commit(project with { Clips = items }, new TimelineSelection(SelectionKind.MainClip, index), StartOf(index));
        SetStatus("已更新片段入点与出点");
    }

    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || refreshing) return;
        VolumeValue.Text = $"{e.NewValue:0}%";
        if (!UsingRender && selection.Kind == SelectionKind.MainClip && activeClip == selection.Index)
            Player.Volume = project.GetTrack("A1").Muted ? 0 : Math.Min(1, e.NewValue / 100);
        else if (!UsingRender && selection.Kind == SelectionKind.Overlay && activeOverlayIndex == selection.Index)
            OverlayPlayer.Volume = project.GetTrack("V2").Muted ? 0 : Math.Min(1, e.NewValue / 100);
    }

    private void VolumeCommit(object sender, MouseButtonEventArgs e) => ApplyVolume();
    private void VolumeKeyUp(object sender, KeyEventArgs e) => ApplyVolume();
    private void VolumeLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyVolume();

    private void ApplyVolume()
    {
        if (!ready || refreshing || Busy) return;
        double volume = Math.Round(VolumeSlider.Value / 100.0, 2);
        if (selection.Kind == SelectionKind.MainClip && selection.Index >= 0 && selection.Index < project.Clips.Length)
        {
            var clip = project.Clips[selection.Index];
            if (Math.Abs(clip.Volume - volume) < 0.001) return;
            UpdateMainClip(selection.Index, clip with { Volume = volume });
        }
        else if (selection.Kind == SelectionKind.Overlay && selection.Index >= 0 && selection.Index < project.OverlayTrack.Length)
        {
            var ov = project.OverlayTrack[selection.Index];
            if (Math.Abs(ov.Volume - volume) < 0.001) return;
            UpdateOverlayClip(selection.Index, ov with { Volume = volume });
        }
    }

    // --- Aspect & Zoom ---

    private void AspectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || refreshing || Busy || AspectBox.SelectedItem is not ComboBoxItem { Content: string format } || format == project.Format) return;
        Commit(project with { Format = format }, selection, head);
    }

    private void MonitorSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ready) ResizePicture();
    }

    private void ResizePicture()
    {
        double aspect = project.Format switch { "9:16" => 9d / 16, "1:1" => 1, _ => 16d / 9 };
        double width = Math.Min(MonitorHost.ActualWidth, MonitorHost.ActualHeight * aspect);
        Picture.Width = Math.Max(0, width);
        Picture.Height = Math.Max(0, width / aspect);
        Dispatcher.InvokeAsync(RefreshLiveCanvas, DispatcherPriority.Render);
    }

    private void TimelineSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimelineViewport();

    private void TimelineScrolled(object sender, ScrollChangedEventArgs e)
    {
        Timeline.ViewOffset = e.HorizontalOffset;
        if (e.ViewportWidthChange != 0) UpdateTimelineViewport();
        Timeline.InvalidateVisual();
    }

    private void UpdateTimelineViewport()
    {
        double width = TimelineScroll.ViewportWidth;
        if (!ready || width <= 0 || Math.Abs(width - Timeline.ViewWidth) < 0.01) return;
        Timeline.ViewWidth = width;
        UpdateScale();
    }

    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || fitting) return;
        scaleFollowsFit = false;
        UpdateScale();
        EnsureHeadVisible();
    }

    private void FitClick(object sender, RoutedEventArgs e)
    {
        scaleFollowsFit = true;
        UpdateScale();
        TimelineScroll.ScrollToHorizontalOffset(0);
    }

    private void ZoomOutClick(object sender, RoutedEventArgs e) =>
        ZoomSlider.Value = Math.Max(ZoomSlider.Minimum, ZoomSlider.Value - 0.6);

    private void ZoomInClick(object sender, RoutedEventArgs e) =>
        ZoomSlider.Value = Math.Min(ZoomSlider.Maximum, ZoomSlider.Value + 0.6);

    private void TimelineMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + (e.Delta > 0 ? 0.45 : -0.45), ZoomSlider.Minimum, ZoomSlider.Maximum);
        e.Handled = true;
    }

    private void UpdateScale()
    {
        double ideal = duration > 0 ? Math.Max(25, Timeline.ViewWidth - 78) / duration : 66;
        if (scaleFollowsFit)
        {
            fitScale = Math.Min(420, ideal);
            fitting = true;
            ZoomSlider.Value = 0;
            fitting = false;
        }
        Timeline.SetScale(fitScale * Math.Pow(1.7, ZoomSlider.Value));
    }

    // --- UI Refresh ---

    private void Refresh()
    {
        refreshing = true;
        var chosen = AssetList.SelectedItem;
        var wanted = project.Assets.Where(assets.ContainsKey).Select(path => assets[path]).ToArray();
        if (!rows.SequenceEqual(wanted))
        {
            rows.Clear();
            foreach (var row in wanted) rows.Add(row);
            if (chosen is not null && assetView.Contains(chosen)) AssetList.SelectedItem = chosen;
        }
        var wantedAudio = wanted.Where(r => r.Asset.AudioStream is not null).ToArray();
        if (!audioRows.SequenceEqual(wantedAudio))
        {
            audioRows.Clear();
            foreach (var r in wantedAudio) audioRows.Add(r);
        }
        RefreshAssetPanel();
        AspectBox.SelectedIndex = project.Format switch { "9:16" => 1, "1:1" => 2, _ => 0 };
        UpdateCompositeStageGeometry();
        ResizePicture();
        duration = project.Duration;
        Timeline.Clips = project.Clips;
        Timeline.Overlays = project.OverlayTrack;
        Timeline.Titles = project.TitleTrack;
        Timeline.Audio = project.AudioTrack;
        Timeline.Duration = duration;
        Timeline.Selection = selection;
        UpdateScale();
        Timeline.Refresh();
        SyncTrackHeaderButtons();

        int totalSegs = project.Clips.Length + project.OverlayTrack.Length + project.TitleTrack.Length + project.AudioTrack.Length;
        TotalTime.Text = $" / {EditTime.Display(duration, true)}";
        StatusMeta.Text = $"{totalSegs} 个片段  ·  {project.Format}  ·  {EditTime.Display(duration)}";
        string displayName = projectPath is null ? "未命名项目" : Path.GetFileNameWithoutExtension(projectPath);
        ProjectTitle.Text = displayName;
        ProjectDirtyDot.Visibility = Dirty ? Visibility.Visible : Visibility.Collapsed;
        Title = "清剪 — " + displayName + (Dirty ? " •" : "");
        ProjectInspectorName.Text = displayName;
        ProjectInspectorFormat.Text = project.Format;
        ProjectInspectorDuration.Text = EditTime.Display(duration, false);
        ProjectInspectorAssets.Text = $"{project.Assets.Length} 项";
        ProjectInspectorClips.Text = $"{totalSegs} 段";
        refreshing = false;
        RefreshInspector();
        RefreshButtons();
        RefreshLiveCanvas();
    }

    private void RefreshButtons()
    {
        bool idle = !Busy;
        var selectedAsset = AssetList.SelectedItem as AssetRow;
        UndoButton.IsEnabled = idle && undo.Count > 0;
        RedoButton.IsEnabled = idle && redo.Count > 0;
        ImportButton.IsEnabled = idle;
        AssetEmpty.IsEnabled = idle;
        AssetSearch.IsEnabled = idle;
        ExportButton.IsEnabled = idle && project.Clips.Length > 0;
        StillFrameButton.IsEnabled = idle && project.Clips.Length > 0;
        AppendButton.IsEnabled = idle && selectedAsset is not null;
        AddOverlayButton.IsEnabled = idle && selectedAsset is { Asset.IsAudioOnly: false };
        PlayButton.IsEnabled = idle && project.Clips.Length > 0;
        SplitButton.IsEnabled = idle && (project.Clips.Length > 0 || selection.Kind == SelectionKind.Overlay);
        DuplicateButton.IsEnabled = idle && !selection.IsNone;
        DeleteButton.IsEnabled = idle && !selection.IsNone;
        LeftButton.IsEnabled = idle && selection.Kind == SelectionKind.MainClip && selection.Index > 0;
        RightButton.IsEnabled = idle && selection.Kind == SelectionKind.MainClip && selection.Index >= 0 && selection.Index < project.Clips.Length - 1;
        TitleButton.IsEnabled = idle && project.Clips.Length > 0;
        AddDefaultTextBtn.IsEnabled = idle && project.Clips.Length > 0;
        AudioButton.IsEnabled = idle;
        Timeline.IsEnabled = idle;
        AspectBox.IsEnabled = idle;
        Inspector.IsEnabled = idle;
    }

    private void RefreshInspector()
    {
        refreshing = true;
        InspectEmpty.Visibility = selection.IsNone ? Visibility.Visible : Visibility.Collapsed;
        InspectorVideoScroll.Visibility = selection.Kind is SelectionKind.MainClip or SelectionKind.Overlay ? Visibility.Visible : Visibility.Collapsed;
        InspectorTitleScroll.Visibility = selection.Kind == SelectionKind.Title ? Visibility.Visible : Visibility.Collapsed;
        InspectorAudioScroll.Visibility = selection.Kind == SelectionKind.Audio ? Visibility.Visible : Visibility.Collapsed;

        switch (selection.Kind)
        {
            case SelectionKind.None:
                InspectorTitle.Text = "项目参数";
                break;

            case SelectionKind.MainClip when selection.Index < project.Clips.Length:
            {
                var clip = project.Clips[selection.Index];
                var asset = assets.GetValueOrDefault(clip.Path)?.Asset;
                InspectorTitle.Text = "主视频属性";
                ClipTrackLabel.Text = "V1 主轨";
                ClipTrackLabel.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                ClipTrackPill.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
                ClipTrackPill.SetResourceReference(Border.BorderBrushProperty, "AccentBorder");
                PopulateVideoInspector(clip.Name, clip.Path, clip.Duration, clip.In, clip.Out, clip.Volume, clip.Crop, clip.EffectiveTransform, asset, null);
                SeparateButton.Visibility = Visibility.Visible;
                SeparateButton.IsEnabled = asset?.AudioStream is not null && clip.Volume > 0;
                bool canDissolve = selection.Index < project.Clips.Length - 1;
                DissolveCard.Visibility = canDissolve ? Visibility.Visible : Visibility.Collapsed;
                if (canDissolve)
                {
                    double limit = Math.Min(2, Math.Min(clip.Duration, project.Clips[selection.Index + 1].Duration));
                    DissolveSlider.Maximum = Math.Max(0.1, Math.Round(limit, 1));
                    DissolveSlider.Value = Math.Min(clip.Dissolve, DissolveSlider.Maximum);
                    DissolveBadge.Text = clip.Dissolve > 0 ? $"{clip.Dissolve:0.#}s" : "无";
                    DissolveBadge.SetResourceReference(TextBlock.ForegroundProperty, clip.Dissolve > 0 ? "Accent" : "Muted");
                }
                break;
            }

            case SelectionKind.Overlay when selection.Index < project.OverlayTrack.Length:
            {
                var ov = project.OverlayTrack[selection.Index];
                var asset = assets.GetValueOrDefault(ov.Path)?.Asset;
                InspectorTitle.Text = "画中画属性";
                ClipTrackLabel.Text = "V2 画中画";
                ClipTrackLabel.SetResourceReference(TextBlock.ForegroundProperty, "TrackOverlayTag");
                ClipTrackPill.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x3D));
                ClipTrackPill.SetResourceReference(Border.BorderBrushProperty, "TrackOverlayTag");
                PopulateVideoInspector(ov.Name, ov.Path, ov.Duration, ov.In, ov.Out, ov.Volume, ov.Crop, ov.EffectiveTransform, asset, ov.Start);
                SeparateButton.Visibility = Visibility.Collapsed;
                DissolveCard.Visibility = Visibility.Collapsed;
                break;
            }

            case SelectionKind.Title when selection.Index < project.TitleTrack.Length:
            {
                var t = project.TitleTrack[selection.Index];
                var st = t.EffectiveStyle;
                InspectorTitle.Text = "文字属性";
                TitleTextBox.Text = t.Text;
                TitleSizeSlider.Value = st.FontSize;
                TitleSizeValue.Text = $"{st.FontSize:0} pt";
                TitleOutlineCheck.IsChecked = st.Outline;
                TitleBgCheck.IsChecked = st.BackgroundBox;
                TitleStartBox.Text = DisplayValue(t.Start);
                TitleDurBox.Text = DisplayValue(t.Duration);
                int fontIdx = 0;
                for (int i = 0; i < TitleFontBox.Items.Count; i++)
                    if ((TitleFontBox.Items[i] as ComboBoxItem)?.Content as string == st.FontFamily) { fontIdx = i; break; }
                TitleFontBox.SelectedIndex = fontIdx;
                break;
            }

            case SelectionKind.Audio when selection.Index < project.AudioTrack.Length:
            {
                var a = project.AudioTrack[selection.Index];
                InspectorTitle.Text = "音频属性";
                AudioClipName.Text = a.Name;
                AudioClipName.ToolTip = a.Path;
                AudioVolumeSlider.Value = a.Volume * 100;
                AudioVolumeValue.Text = $"{a.Volume * 100:0}%";
                AudioFadeInBox.Text = DisplayValue(a.FadeIn);
                AudioFadeOutBox.Text = DisplayValue(a.FadeOut);
                AudioStartBox.Text = DisplayValue(a.Start);
                AudioInBox.Text = DisplayValue(a.In);
                AudioOutBox.Text = DisplayValue(a.Out);
                break;
            }
        }
        refreshing = false;
    }

    private void PopulateVideoInspector(string name, string path, double clipDur, double clipIn, double clipOut,
        double vol, CropMargins? crop, TransformSpec tr, MediaAsset? asset, double? overlayStart)
    {
        ClipName.Text = name;
        ClipName.ToolTip = path;
        ClipDetail.Text = asset is null ? "素材文件缺失" : $"时长  {EditTime.Display(clipDur, true)}";
        ScaleSlider.Value = tr.Scale * 100;
        ScaleValueText.Text = $"{tr.Scale * 100:0}%";
        RotateSlider.Value = tr.Rotation;
        RotateValueText.Text = $"{tr.Rotation:0}°";
        OpacitySlider.Value = tr.Opacity * 100;
        OpacityValueText.Text = $"{tr.Opacity * 100:0}%";
        PositionReadout.Text = $"位置 X {tr.X:P0}  ·  Y {tr.Y:P0}（可在画布直接拖动）";

        if (crop is { IsZero: false })
        {
            CropBadge.Text = $"{crop.Left}/{crop.Top}/{crop.Right}/{crop.Bottom}%";
            CropBadge.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        }
        else
        {
            CropBadge.Text = "未裁剪";
            CropBadge.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        }

        OverlayStartRow.Visibility = overlayStart is not null ? Visibility.Visible : Visibility.Collapsed;
        if (overlayStart is double os) OverlayStartBox.Text = DisplayValue(os);
        InBox.Text = EditTime.Display(clipIn, true);
        OutBox.Text = EditTime.Display(clipOut, true);

        VolumeSlider.IsEnabled = asset?.AudioStream is not null;
        VolumeSlider.Value = vol * 100;
        VolumeValue.Text = asset?.AudioStream is not null ? $"{vol * 100:0}%" : "无原声";
    }

    // --- Playback & Live Multi-Layer Seeking ---

    private void Seek(double position, bool scroll = true)
    {
        UpdateHead(Math.Clamp(position, 0, duration), scroll);
        if (ReferenceEquals(project, renderedProject) && renderedPreview is not null)
        {
            activeClip = -1;
            StopOverlayMedia();
            pendingLocal = Math.Min(head, Math.Max(0, duration - 0.018));
            LoadPreviewSource(renderedPreview, "转场合成预览");
            RefreshLiveCanvas();
            return;
        }
        activeClip = ClipAt(head, out activeStart);
        SyncOverlayPlayer(head);

        if (activeClip < 0)
        {
            StopMedia("把视频加入时间线后即可预览");
            RefreshLiveCanvas();
            return;
        }
        var clip = project.Clips[activeClip];
        if (head > activeStart + clip.Duration + 0.00001)
        {
            Player.Pause();
            PreviewMode.Text = "实时合成 · 尾段";
            PreviewHint.Visibility = Visibility.Collapsed;
            RefreshLiveCanvas();
            return;
        }
        if (!assets.TryGetValue(clip.Path, out var row))
        {
            StopMedia("找不到该片段对应的素材文件");
            Pause();
            return;
        }
        if (playing && duration - head <= 0.00001) Pause();
        if (row.Asset.IsImage)
        {
            if (sourcePath is not null)
            {
                Player.Close();
                Player.Source = null;
                sourcePath = null;
            }
            mainShowingImage = true;
            playerReady = true;
            Player.Visibility = Visibility.Collapsed;
            MainImageLayer.Source = imageFrames.GetValueOrDefault(row.Asset.Path) ?? row.Thumbnail;
            MainImageLayer.Visibility = Visibility.Visible;
            PreviewHint.Visibility = Visibility.Collapsed;
            PreviewMode.Text = "实时合成 · 图片";
            imagePlayStartHead = head;
            imagePlayAnchorUtc = DateTime.UtcNow;
            RefreshLiveCanvas();
            return;
        }
        if (mainShowingImage)
        {
            mainShowingImage = false;
            MainImageLayer.Visibility = Visibility.Collapsed;
            MainImageLayer.Source = null;
            Player.Visibility = Visibility.Visible;
        }
        double lastFrame = Math.Max(clip.In, clip.Out - 0.018);
        pendingLocal = Math.Clamp(clip.In + (head - activeStart) * clip.EffectiveSpeed, clip.In, lastFrame);
        string media = proxies.GetValueOrDefault(row.Asset.Path) ?? row.Asset.Path;
        string mode = project.NeedsBakedPreview ? "实时合成 · 含叠化" : media == row.Asset.Path ? "实时合成" : "代理 · 实时合成";
        LoadPreviewSource(media, mode);
        if (!playerReady && attemptedProxies.Contains(row.Asset.Path) && media == row.Asset.Path)
            ShowPreviewHint("正在准备兼容预览…");
        RefreshLiveCanvas();
    }

    private void LoadPreviewSource(string media, string mode)
    {
        PreviewMode.Text = mode;
        if (!string.Equals(sourcePath, media, StringComparison.OrdinalIgnoreCase))
        {
            playerReady = false;
            sourcePath = media;
            ShowPreviewHint("正在加载画面…");
            Player.Position = TimeSpan.Zero;
            Player.Source = new Uri(media);
            Player.Play();
        }
        else if (playerReady) ResumeAtPending();
    }

    private void StopMedia(string hint)
    {
        Player.Close();
        Player.Source = null;
        sourcePath = null;
        playerReady = false;
        mainShowingImage = false;
        MainImageLayer.Visibility = Visibility.Collapsed;
        MainImageLayer.Source = null;
        Player.Visibility = Visibility.Visible;
        StopOverlayMedia();
        PreviewMode.Text = "实时合成";
        ShowPreviewHint(hint);
    }

    private void PlayerOpened(object sender, RoutedEventArgs e)
    {
        if ((activeClip < 0 && !UsingRender) || sourcePath is null) return;
        playerReady = true;
        PreviewHint.Visibility = Visibility.Collapsed;
        ResumeAtPending();
        RefreshLiveCanvas();
    }

    private void ResumeAtPending()
    {
        if (activeClip < 0 && !UsingRender) return;
        bool a1Muted = project.GetTrack("A1").Muted || project.GetTrack("V1").Muted;
        Player.Volume = UsingRender ? 1 : a1Muted ? 0 : Math.Min(1, project.Clips[activeClip].Volume);
        Player.Position = TimeSpan.FromSeconds(pendingLocal);
        if (playing) Player.Play();
        else Player.Pause();
    }

    private void ShowPreviewHint(string hint)
    {
        PreviewMessage.Text = hint;
        PreviewHint.Visibility = Visibility.Visible;
    }

    private async void PlayerFailed(object sender, ExceptionRoutedEventArgs e)
    {
        playerReady = false;
        if (UsingRender)
        {
            Pause();
            ShowPreviewHint("系统播放器无法打开合成预览。可以直接导出视频。");
            return;
        }
        if (activeClip < 0 || !assets.TryGetValue(project.Clips[activeClip].Path, out var row)) return;
        var asset = row.Asset;
        if (!attemptedProxies.Add(asset.Path))
        {
            ShowPreviewHint("无法预览该视频，可尝试导出。");
            Pause();
            return;
        }
        ShowPreviewHint("正在准备兼容预览…");
        string proxy = Path.Combine(cache, $"{Guid.NewGuid():N}.mp4");
        try
        {
            await tools.ProxyAsync(asset, proxy, lifetime.Token);
            proxies[asset.Path] = proxy;
            if (activeClip >= 0 && project.Clips[activeClip].Path.Equals(asset.Path, StringComparison.OrdinalIgnoreCase)) Seek(head);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (activeClip >= 0 && project.Clips[activeClip].Path.Equals(asset.Path, StringComparison.OrdinalIgnoreCase))
            {
                Pause();
                ShowPreviewHint("无法预览该视频，可尝试导出。");
                SetStatus(FirstLine(error.Message));
            }
        }
    }

    private void PlaybackTick(object? sender, EventArgs e)
    {
        if (!playing || !playerReady) return;
        if (UsingRender)
        {
            UpdateHead(Math.Min(duration, Player.Position.TotalSeconds), true);
            return;
        }
        if (activeClip < 0) return;
        var clip = project.Clips[activeClip];
        if (mainShowingImage)
        {
            double elapsed = (DateTime.UtcNow - imagePlayAnchorUtc).TotalSeconds;
            double nextHead = imagePlayStartHead + elapsed;
            if (nextHead >= activeStart + clip.Duration) Advance();
            else
            {
                UpdateHead(nextHead, true);
                SyncOverlayPlayer(nextHead);
                RefreshLiveCanvas();
            }
            return;
        }
        double local = Player.Position.TotalSeconds;
        if (local >= clip.Out) Advance();
        else if (local >= clip.In)
        {
            double nextHead = activeStart + (local - clip.In) / clip.EffectiveSpeed;
            UpdateHead(nextHead, true);
            SyncOverlayPlayer(nextHead);
            RefreshLiveCanvas();
        }
    }

    private void PlayerEnded(object sender, RoutedEventArgs e)
    {
        if (playing && UsingRender)
        {
            Pause();
            UpdateHead(duration, true);
            return;
        }
        if (playing && playerReady) Advance();
    }

    private void Advance()
    {
        if (activeClip < 0) return;
        double next = activeStart + project.Clips[activeClip].Duration;
        if (next >= duration - 0.00001) Pause();
        Seek(next);
    }

    private void UpdateHead(double position, bool scroll)
    {
        head = position;
        CurrentTime.Text = EditTime.Display(head, true);
        Timeline.Head = head;
        Timeline.InvalidateVisual();
        if (scroll) EnsureHeadVisible();
    }

    private void EnsureHeadVisible()
    {
        double x = Timeline.PositionOf(head);
        double offset = TimelineScroll.HorizontalOffset;
        if (x < offset + 5) TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - 25));
        else if (x > offset + Timeline.ViewWidth - 24)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - Timeline.ViewWidth * 0.72));
    }

    private void Pause()
    {
        playing = false;
        if (playerReady) Player.Pause();
        if (overlayPlayerReady) OverlayPlayer.Pause();
        PlayIcon.Visibility = Visibility.Visible;
        PauseIcon.Visibility = Visibility.Collapsed;
    }

    private async void TogglePlay()
    {
        if (Busy || project.Clips.Length == 0) return;
        if (playing) { Pause(); return; }
        // Only require baked FFmpeg preview when dissolves or speed changes are present;
        // transforms, crop, PiP overlays, and styled text play in real time!
        if (project.NeedsBakedPreview && !ReferenceEquals(project, renderedProject) && !await RenderPreviewAsync()) return;
        playing = true;
        PlayIcon.Visibility = Visibility.Collapsed;
        PauseIcon.Visibility = Visibility.Visible;
        Seek(head >= duration - 0.00001 ? 0 : head);
    }

    private void PlayClick(object sender, RoutedEventArgs e) => TogglePlay();
    private void StartClick(object sender, RoutedEventArgs e) { if (!Busy) Seek(0); }
    private void EndClick(object sender, RoutedEventArgs e) { if (!Busy) Seek(duration); }

    // --- Project File Open / Save / Export ---

    private async Task<bool> ConfirmSaveAsync()
    {
        if (!Dirty) return true;
        return MessageBox.Show(this, "保存当前项目的修改？", "清剪", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => await SaveAsync(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private async void NewClick(object sender, RoutedEventArgs e)
    {
        if (Busy || !await ConfirmSaveAsync()) return;
        ApplyProject(EditProject.Empty, null);
        SetStatus("已建立新项目");
    }

    private async void OpenClick(object sender, RoutedEventArgs e)
    {
        if (Busy || !await ConfirmSaveAsync()) return;
        var dialog = new OpenFileDialog { Title = "打开清剪项目", Filter = "清剪项目|*.qingjian" };
        if (dialog.ShowDialog(this) != true || !EnsureTools()) return;
        await OpenProjectFileAsync(dialog.FileName);
    }

    private async Task OpenProjectFileAsync(string filePath)
    {
        Pause();
        importing = true;
        RefreshButtons();
        List<string> missing = [];
        try
        {
            var opened = await EditProject.ReadAsync(filePath);
            foreach (string path in opened.Assets.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool videoReferenced = opened.Clips.Any(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) ||
                    opened.OverlayTrack.Any(o => o.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                try { await ReadAssetAsync(path, !videoReferenced); }
                catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or UnauthorizedAccessException)
                {
                    assets.Remove(path);
                    missing.Add(Path.GetFileName(path));
                }
            }
            ApplyProject(opened, filePath);
            EnterEditorScreen();
            string? thumbPath = SaveDraftCoverThumb(filePath);
            RecentProjectsStore.Record(filePath, opened.Format, duration, thumbPath, opened.Clips.Length);
            SetStatus(missing.Count > 0 ? $"已打开项目，{missing.Count} 个素材不可用" : "已打开项目");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(FirstLine(error.Message)); }
        finally
        {
            importing = false;
            RefreshButtons();
        }
        if (missing.Count > 0) ShowError("以下素材不可用。将文件恢复到原路径后重新打开项目。\n" + string.Join("\n", missing.Take(10)));
    }

    private void ApplyProject(EditProject next, string? path)
    {
        Pause();
        StopMedia("把视频加入时间线后即可预览");
        undo.Clear();
        redo.Clear();
        project = saved = next;
        projectPath = path;
        AssetSearch.Clear();
        selection = TimelineSelection.None;
        cropMode = false;
        CropModeToggle.IsChecked = false;
        scaleFollowsFit = true;
        Refresh();
        Seek(0);
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!Busy) await SaveAsync();
    }

    private async Task<bool> SaveAsync(bool forceSaveAs = false)
    {
        if (saving) return false;
        saving = true;
        RefreshButtons();
        try
        {
            string? path = forceSaveAs ? null : projectPath;
            if (path is null)
            {
                var dialog = new SaveFileDialog
                {
                    Title = "保存清剪项目", Filter = "清剪项目|*.qingjian",
                    FileName = projectPath is null ? "未命名项目" : Path.GetFileNameWithoutExtension(projectPath),
                    DefaultExt = ".qingjian", AddExtension = true
                };
                if (dialog.ShowDialog(this) != true) return false;
                path = dialog.FileName;
            }
            var snapshot = project with { Version = 3 };
            await snapshot.WriteAsync(path);
            project = saved = snapshot;
            projectPath = path;
            string? thumbPath = SaveDraftCoverThumb(path);
            RecentProjectsStore.Record(path, snapshot.Format, duration, thumbPath, snapshot.Clips.Length);
            Refresh();
            SetStatus($"已保存 {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception error)
        {
            ShowError(FirstLine(error.Message));
            return false;
        }
        finally
        {
            saving = false;
            RefreshButtons();
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        if (importing || saving || configuringExport || exporting is not null || previewing is not null)
        {
            e.Cancel = true;
            SetStatus(exporting is not null || previewing is not null ? "请先取消当前渲染或等待它完成。" : "请完成当前操作后再关闭。");
            return;
        }
        if (Dirty)
        {
            e.Cancel = true;
            if (!await ConfirmSaveAsync()) return;
            closing = true;
            await Dispatcher.InvokeAsync(Close);
            return;
        }
        closing = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        timer.Stop();
        Player.Close();
        OverlayPlayer.Close();
        lifetime.Cancel();
        try { Directory.Delete(cache, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        base.OnClosed(e);
    }

    private void ExportClick(object sender, RoutedEventArgs e) => StartExportDialog(preferSelectedClip: false);

    private async void StartExportDialog(bool preferSelectedClip)
    {
        if (Busy || project.Clips.Length == 0 || !EnsureTools()) return;
        if (!TryBuildComposition(out var clips, out var audio, out var overlays)) return;
        Pause();

        string filename = projectPath is null ? "清剪视频" : Path.GetFileNameWithoutExtension(projectPath);
        string folder = lastExportFolder ?? (projectPath is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) : Path.GetDirectoryName(projectPath)!);
        if (!Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        string? selClipName = null;
        double? selClipStart = null;
        double? selClipEnd = null;
        int selClipIndex = selection.Kind == SelectionKind.MainClip && selection.Index >= 0 && selection.Index < clips.Count
            ? selection.Index : -1;
        if (selClipIndex >= 0)
        {
            double acc = 0;
            for (int i = 0; i < selClipIndex; i++) acc += clips[i].Clip.Duration;
            selClipStart = acc;
            selClipEnd = acc + clips[selClipIndex].Clip.Duration;
            selClipName = Path.GetFileNameWithoutExtension(clips[selClipIndex].Clip.Path);
        }

        var (firstClip, firstAsset) = clips[0];
        var dialog = new ExportWindow(project.Format, duration, filename, folder, project.Assets,
            assets[firstClip.Path].Thumbnail, token => ReadThumbnailAsync(firstAsset, token, firstClip.In),
            project.TitleTrack.Length, selClipName, selClipStart, selClipEnd)
        { Owner = this };
        if (preferSelectedClip && selClipIndex >= 0) dialog.ScopeClip.IsChecked = true;

        configuringExport = true;
        RefreshButtons();
        ExportBatchPlan? plan;
        try
        {
            if (dialog.ShowDialog() != true || dialog.Plan is null) return;
            plan = dialog.Plan;
        }
        finally
        {
            configuringExport = false;
            RefreshButtons();
        }

        // Prepare effective composition according to selected scope
        IReadOnlyList<(Clip Clip, MediaAsset Asset)> exportClips = clips;
        IReadOnlyList<TitleLayer> exportTitles = project.TitleTrack;
        IReadOnlyList<(AudioLayer Layer, MediaAsset Asset)> exportAudio = audio;
        IReadOnlyList<(OverlayClip Clip, MediaAsset Asset)> exportOverlays = overlays;

        if (plan.Scope == ExportScope.SelectedClip && selClipIndex >= 0 && selClipStart is double cs && selClipEnd is double ce)
        {
            exportClips = [clips[selClipIndex]];
            if (plan.IncludeEffectsInClipExport)
            {
                exportTitles = project.TitleTrack
                    .Where(t => t.End > cs && t.Start < ce)
                    .Select(t =>
                    {
                        double newStart = Math.Max(0, t.Start - cs);
                        double newEnd = Math.Min(ce - cs, t.End - cs);
                        return t with { Start = newStart, Duration = Math.Max(0.1, newEnd - newStart) };
                    })
                    .ToArray();
                exportOverlays = overlays
                    .Where(o => o.Clip.End > cs && o.Clip.Start < ce)
                    .Select(o =>
                    {
                        double trimHead = Math.Max(0, cs - o.Clip.Start);
                        double newStart = Math.Max(0, o.Clip.Start - cs);
                        double newDur = Math.Min(o.Clip.End, ce) - Math.Max(o.Clip.Start, cs);
                        double newIn = o.Clip.In + trimHead * o.Clip.EffectiveSpeed;
                        double newOut = Math.Min(o.Clip.Out, newIn + newDur * o.Clip.EffectiveSpeed);
                        return (o.Clip with { Start = newStart, In = newIn, Out = newOut }, o.Asset);
                    })
                    .Where(o => o.Item1.Out > o.Item1.In + 0.02)
                    .ToArray();
                exportAudio = audio
                    .Where(a => a.Layer.End > cs && a.Layer.Start < ce)
                    .Select(a =>
                    {
                        double trimHead = Math.Max(0, cs - a.Layer.Start);
                        double newStart = Math.Max(0, a.Layer.Start - cs);
                        double newDur = Math.Min(a.Layer.End, ce) - Math.Max(a.Layer.Start, cs);
                        double newIn = a.Layer.In + trimHead * a.Layer.EffectiveSpeed;
                        double newOut = Math.Min(a.Layer.Out, newIn + newDur * a.Layer.EffectiveSpeed);
                        return (a.Layer with { Start = newStart, In = newIn, Out = newOut }, a.Asset);
                    })
                    .Where(a => a.Item1.Out > a.Item1.In + 0.02)
                    .ToArray();
            }
            else
            {
                exportTitles = [];
                exportOverlays = [];
                exportAudio = [];
            }
        }

        using var operation = new CancellationTokenSource();
        exporting = operation;
        RefreshButtons();
        ExportOverlay.Visibility = Visibility.Visible;
        ExportHeading.Text = plan.PlannedOutputs().Count > 1 ? $"正在导出 ({plan.PlannedOutputs().Count} 项产物)" : "正在导出";
        ExportProgress.Visibility = Visibility.Visible;
        ExportResultActions.Visibility = Visibility.Collapsed;
        CancelExportButton.Visibility = Visibility.Visible;
        CancelExportButton.IsEnabled = true;
        ExportProgress.Value = 0;
        ExportStatus.Text = "正在准备编码器…";
        CancelExportButton.Focus();
        exportedPath = null;

        var progress = new Progress<double>(fraction =>
        {
            if (exporting != operation || operation.IsCancellationRequested) return;
            ExportProgress.Value = fraction * 100;
            ExportStatus.Text = $"正在导出  {fraction:P0}";
        });
        try
        {
            var result = await tools.ExportBatchAsync(exportClips, plan, progress, operation.Token,
                exportTitles, exportAudio, exportOverlays, project.TrackStates);
            exportedPath = result.CreatedFiles[0];
            lastExportFolder = Path.GetDirectoryName(exportedPath);
            string summaryNames = string.Join(" + ", result.CreatedFiles.Select(Path.GetFileName));
            string encNote = result.VideoEncoderUsed is not null ? $"  ·  {result.VideoEncoderUsed}" : "";
            SetStatus($"已导出 {summaryNames}{encNote}");
            ExportHeading.Text = result.CreatedFiles.Count > 1 ? $"导出完成（共 {result.CreatedFiles.Count} 项）" : "导出完成";
            ExportStatus.Text = string.Join("\n", result.CreatedFiles);
            ExportProgress.Visibility = Visibility.Collapsed;
            CancelExportButton.Visibility = Visibility.Collapsed;
            ExportResultActions.Visibility = Visibility.Visible;
            ExportDoneButton.Focus();
        }
        catch (OperationCanceledException) { SetStatus("已取消导出"); }
        catch (Exception error) { ShowError(error.Message.Length > 650 ? error.Message[..650] + "…" : error.Message); }
        finally
        {
            if (exportedPath is null) ExportOverlay.Visibility = Visibility.Collapsed;
            exporting = null;
            RefreshButtons();
        }
    }

    private async void ExportStillFrameClick(object sender, RoutedEventArgs e)
    {
        if (Busy || project.Clips.Length == 0 || !EnsureTools()) return;
        if (!TryBuildComposition(out var clips, out _, out var overlays)) return;
        Pause();

        string baseName = (projectPath is null ? "静帧" : Path.GetFileNameWithoutExtension(projectPath)) +
            $"_{TimeSpan.FromSeconds(Math.Max(0, head)):mmss}";
        string folder = lastExportFolder ?? (projectPath is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) : Path.GetDirectoryName(projectPath)!);
        if (!Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var dialog = new StillFrameWindow(baseName, folder, head) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.OutputPath is null) return;

        try
        {
            SetStatus("正在导出当前静帧画面…");
            await tools.ExportStillFrameAsync(clips, head, dialog.ShortEdge, project.Format, dialog.ImageFormat,
                dialog.OutputPath, lifetime.Token, dialog.ReplaceExisting, project.TitleTrack, overlays, project.TrackStates,
                dialog.Quality, dialog.Lossless);
            exportedPath = dialog.OutputPath;
            lastExportFolder = Path.GetDirectoryName(dialog.OutputPath);
            SetStatus($"已导出静帧：{Path.GetFileName(dialog.OutputPath)}");
            ExportOverlay.Visibility = Visibility.Visible;
            ExportHeading.Text = "静帧导出完成";
            ExportStatus.Text = dialog.OutputPath;
            ExportProgress.Visibility = Visibility.Collapsed;
            CancelExportButton.Visibility = Visibility.Collapsed;
            ExportResultActions.Visibility = Visibility.Visible;
            ExportDoneButton.Focus();
        }
        catch (Exception error) { ShowError(FirstLine(error.Message)); }
    }

    private void ConvertImageClick(object sender, RoutedEventArgs e)
    {
        var initialSources = new List<string>();
        if (AssetList.SelectedItem is AssetRow selRow && selRow.Asset.IsImage && File.Exists(selRow.Asset.Path))
            initialSources.Add(selRow.Asset.Path);
        OpenImageConverter(initialSources);
    }

    private async void OpenImageConverter(IReadOnlyList<string> initialSources)
    {
        if (Busy || !EnsureTools()) return;
        Pause();

        string folder = lastExportFolder ?? (initialSources.Count > 0
            ? Path.GetDirectoryName(initialSources[0])!
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        if (!Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var dialog = new StillFrameWindow(initialSources, folder) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.OutputPath is null || dialog.SourceFiles.Count == 0) return;

        using var operation = new CancellationTokenSource();
        exporting = operation;
        RefreshButtons();
        ExportOverlay.Visibility = Visibility.Visible;
        ExportHeading.Text = dialog.SourceFiles.Count > 1 ? $"正在批量转换图片 ({dialog.SourceFiles.Count} 张)" : "正在转换图片格式";
        ExportProgress.Visibility = Visibility.Visible;
        ExportResultActions.Visibility = Visibility.Collapsed;
        CancelExportButton.Visibility = Visibility.Visible;
        CancelExportButton.IsEnabled = true;
        ExportProgress.Value = 0;
        ExportStatus.Text = "正在处理…";
        exportedPath = null;

        try
        {
            var created = new List<string>();
            string ext = ExportOptions.ImageExtension(dialog.ImageFormat);
            for (int i = 0; i < dialog.SourceFiles.Count; i++)
            {
                string src = dialog.SourceFiles[i];
                string dst = dialog.SourceFiles.Count == 1
                    ? dialog.OutputPath
                    : Path.GetFullPath(Path.Combine(dialog.OutputDirectory, Path.GetFileNameWithoutExtension(src) + ext));
                ExportStatus.Text = $"正在转换 ({i + 1}/{dialog.SourceFiles.Count})：{Path.GetFileName(src)}";
                await tools.ConvertImageAsync(src, dst, dialog.ImageFormat, dialog.MaxLongEdge,
                    dialog.Quality, dialog.Lossless, operation.Token, dialog.ReplaceExisting);
                created.Add(dst);
                ExportProgress.Value = (i + 1) * 100.0 / dialog.SourceFiles.Count;
            }
            exportedPath = created[0];
            lastExportFolder = Path.GetDirectoryName(exportedPath);
            SetStatus(created.Count > 1 ? $"已转换 {created.Count} 张图片为 {dialog.ImageFormat.ToString().ToUpperInvariant()}" : $"已转换图片：{Path.GetFileName(created[0])}");
            ExportHeading.Text = created.Count > 1 ? $"批量转换完成（共 {created.Count} 张）" : "图片格式转换完成";
            ExportStatus.Text = string.Join("\n", created.Take(6)) + (created.Count > 6 ? $"\n… 另有 {created.Count - 6} 张" : "");
            ExportProgress.Visibility = Visibility.Collapsed;
            CancelExportButton.Visibility = Visibility.Collapsed;
            ExportResultActions.Visibility = Visibility.Visible;
            ExportDoneButton.Focus();
        }
        catch (OperationCanceledException) { SetStatus("已取消图片转换"); }
        catch (Exception error) { ShowError(FirstLine(error.Message)); }
        finally
        {
            if (exportedPath is null) ExportOverlay.Visibility = Visibility.Collapsed;
            exporting = null;
            RefreshButtons();
        }
    }

    private void CancelExportClick(object sender, RoutedEventArgs e)
    {
        CancelExportButton.IsEnabled = false;
        ExportStatus.Text = "正在取消…";
        exporting?.Cancel();
        previewing?.Cancel();
    }

    private void OpenExportFolder(object sender, RoutedEventArgs e)
    {
        if (exportedPath is null) return;
        try
        {
            if (File.Exists(exportedPath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{exportedPath}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(Path.GetDirectoryName(exportedPath)!) { UseShellExecute = true });
        }
        catch (Win32Exception error) { ShowError(FirstLine(error.Message)); }
    }

    private void CloseExportResult(object sender, RoutedEventArgs e)
    {
        exportedPath = null;
        ExportOverlay.Visibility = Visibility.Collapsed;
        RefreshButtons();
        ExportButton.Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (exporting is not null || previewing is not null)
        {
            if (e.Key == Key.Escape) CancelExportClick(sender, e);
            e.Handled = e.Key == Key.Escape;
            return;
        }
        if (exportedPath is not null && e.Key == Key.Escape)
        {
            CloseExportResult(sender, e);
            e.Handled = true;
            return;
        }
        if (cropMode && e.Key == Key.Escape)
        {
            ExitCropModeClick(sender, e);
            e.Handled = true;
            return;
        }
        if (Busy) return;
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (inHomeScreen)
        {
            if (control && e.Key == Key.N) HomeStartEditClick(sender, e);
            else if (control && e.Key == Key.O) HomeOpenDraftFileClick(sender, e);
            else return;
            e.Handled = true;
            return;
        }
        if (control && e.Key == Key.I) ImportClick(sender, e);
        else if (control && e.Key == Key.N) NewClick(sender, e);
        else if (control && e.Key == Key.O) OpenClick(sender, e);
        else if (control && e.Key == Key.S) SaveClick(sender, e);
        else if (Keyboard.FocusedElement is TextBox or Slider or ComboBoxItem or ComboBox) return;
        else if (control && e.Key == Key.B) Split();
        else if (control && e.Key == Key.D) DuplicateSelected();
        else if (control && e.Key == Key.Z) ApplyHistory(undo, redo);
        else if (control && e.Key == Key.Y) ApplyHistory(redo, undo);
        else if (!control && e.Key == Key.Delete)
        {
            if (AssetList.IsKeyboardFocusWithin && AssetList.SelectedItem is AssetRow delRow)
                RemoveAssetFromLibrary(delRow.Asset);
            else
                Delete();
        }
        else if (!control && e.Key == Key.Space && e.OriginalSource is not Button) TogglePlay();
        else if (!control && e.Key == Key.Home) Seek(0);
        else if (!control && e.Key == Key.End) Seek(duration);
        else return;
        e.Handled = true;
    }

    private void SetStatus(string message) { Status.Text = message; Status.ToolTip = message; }
    private void ShowError(string message) => MessageBox.Show(this, message, "清剪", MessageBoxButton.OK, MessageBoxImage.Warning);
    private static string FirstLine(string message) => message.Split('\n')[0].Trim();
}
