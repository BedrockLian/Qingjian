using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;

namespace Qingjian;

public partial class MainWindow
{
    // --- Video / PiP Transform, Crop, Dissolve & Separate ---

    private void TransformSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || refreshing || selection.Kind is not (SelectionKind.MainClip or SelectionKind.Overlay)) return;
        var baseTr = selection.Kind == SelectionKind.MainClip
            ? project.Clips[selection.Index].EffectiveTransform
            : project.OverlayTrack[selection.Index].EffectiveTransform;
        var next = baseTr with
        {
            Scale = Math.Round(ScaleSlider.Value / 100.0, 3),
            Rotation = Math.Round(RotateSlider.Value, 1),
            Opacity = Math.Round(OpacitySlider.Value / 100.0, 3)
        };
        ScaleValueText.Text = $"{ScaleSlider.Value:0}%";
        RotateValueText.Text = $"{RotateSlider.Value:0}°";
        OpacityValueText.Text = $"{OpacitySlider.Value:0}%";
        liveTransformDraft = next;
        RefreshLiveCanvas();
    }

    private void TransformSliderCommit(object sender, MouseButtonEventArgs e)
    {
        if (!ready || refreshing || Busy || liveTransformDraft is not { } tr) return;
        liveTransformDraft = null;
        ApplyVideoTransform(tr);
    }

    private void ResetTransformClick(object sender, RoutedEventArgs e)
    {
        if (Busy || selection.Kind is not (SelectionKind.MainClip or SelectionKind.Overlay)) return;
        var def = selection.Kind == SelectionKind.Overlay ? new TransformSpec(0, 0, 0.45, 0, 1) : TransformSpec.Default;
        ApplyVideoTransform(def);
    }

    private void ApplyVideoTransform(TransformSpec tr)
    {
        if (selection.Kind == SelectionKind.MainClip && selection.Index >= 0 && selection.Index < project.Clips.Length)
        {
            var clip = project.Clips[selection.Index];
            var normalized = tr.IsDefault ? null : tr;
            if (clip.Transform == normalized) { RefreshLiveCanvas(); return; }
            UpdateMainClip(selection.Index, clip with { Transform = normalized });
        }
        else if (selection.Kind == SelectionKind.Overlay && selection.Index >= 0 && selection.Index < project.OverlayTrack.Length)
        {
            var ov = project.OverlayTrack[selection.Index];
            if (ov.Transform == tr) { RefreshLiveCanvas(); return; }
            UpdateOverlayClip(selection.Index, ov with { Transform = tr });
        }
    }

    private void ApplyVideoCrop(CropMargins? crop)
    {
        if (selection.Kind == SelectionKind.MainClip && selection.Index >= 0 && selection.Index < project.Clips.Length)
        {
            var clip = project.Clips[selection.Index];
            if (clip.Crop == crop) { RefreshLiveCanvas(); return; }
            UpdateMainClip(selection.Index, clip with { Crop = crop });
        }
        else if (selection.Kind == SelectionKind.Overlay && selection.Index >= 0 && selection.Index < project.OverlayTrack.Length)
        {
            var ov = project.OverlayTrack[selection.Index];
            if (ov.Crop == crop) { RefreshLiveCanvas(); return; }
            UpdateOverlayClip(selection.Index, ov with { Crop = crop });
        }
    }

    private void ResetCropClick(object sender, RoutedEventArgs e)
    {
        if (Busy || selection.Kind is not (SelectionKind.MainClip or SelectionKind.Overlay)) return;
        ApplyVideoCrop(null);
    }

    private void CropClick(object sender, RoutedEventArgs e)
    {
        if (Busy || selection.Kind is not (SelectionKind.MainClip or SelectionKind.Overlay)) return;
        var current = selection.Kind == SelectionKind.MainClip
            ? project.Clips[selection.Index].Crop
            : project.OverlayTrack[selection.Index].Crop;
        var crop = current ?? new CropMargins(0, 0, 0, 0);
        CropMargins? next = crop;
        var editor = new EditFields("精确裁剪画面", "输入各边裁去的百分比，保留区域至少为原画面的 10%。",
            [("左侧", $"{crop.Left}"), ("上侧", $"{crop.Top}"), ("右侧", $"{crop.Right}"), ("下侧", $"{crop.Bottom}")],
            fields =>
            {
                if (int.TryParse(fields[0], out int left) && int.TryParse(fields[1], out int top) &&
                    int.TryParse(fields[2], out int right) && int.TryParse(fields[3], out int bottom) &&
                    new CropMargins(left, top, right, bottom) is { IsValid: true } value)
                {
                    next = value.IsZero ? null : value;
                    return null;
                }
                return "请输入有效的 0 至 90 整数，左右及上下分别最多裁去 90%。";
            }) { Owner = this };
        if (editor.ShowDialog() == true && next != current) ApplyVideoCrop(next);
    }

    private void DissolveSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || refreshing || selection.Kind != SelectionKind.MainClip) return;
        double val = Math.Round(DissolveSlider.Value, 1);
        DissolveBadge.Text = val > 0 ? $"{val:0.#}s" : "无";
        DissolveBadge.SetResourceReference(TextBlock.ForegroundProperty, val > 0 ? "Accent" : "Muted");
    }

    private void DissolveSliderCommit(object sender, MouseButtonEventArgs e)
    {
        if (!ready || refreshing || Busy || selection.Kind != SelectionKind.MainClip || selection.Index >= project.Clips.Length - 1) return;
        var clip = project.Clips[selection.Index];
        double limit = Math.Min(2, Math.Min(clip.Duration, project.Clips[selection.Index + 1].Duration));
        double val = Math.Clamp(Math.Round(DissolveSlider.Value, 1), 0, limit);
        if (Math.Abs(val - clip.Dissolve) < 0.001) return;
        UpdateMainClip(selection.Index, clip with { Dissolve = val });
    }

    private void UpdateMainClip(int index, Clip clip)
    {
        var items = (Clip[])project.Clips.Clone();
        items[index] = clip;
        Commit(project with { Clips = items }, new TimelineSelection(SelectionKind.MainClip, index), head);
    }

    private void UpdateOverlayClip(int index, OverlayClip ov)
    {
        var items = (OverlayClip[])project.OverlayTrack.Clone();
        items[index] = ov;
        Commit(project with { Overlays = items }, new TimelineSelection(SelectionKind.Overlay, index), head);
    }

    private void SeparateClick(object sender, RoutedEventArgs e)
    {
        if (Busy || selection.Kind != SelectionKind.MainClip || selection.Index < 0) return;
        var clip = project.Clips[selection.Index];
        if (clip.Volume == 0 || assets.GetValueOrDefault(clip.Path)?.Asset.AudioStream is null) return;
        var layer = new AudioLayer(clip.Path, StartOf(selection.Index), clip.In, clip.Out, clip.Volume);
        var clips = (Clip[])project.Clips.Clone();
        clips[selection.Index] = clip with { Volume = 0 };
        Commit(project with { Clips = clips, Audio = [.. project.AudioTrack, layer] },
            new TimelineSelection(SelectionKind.Audio, project.AudioTrack.Length), head);
        SetStatus("已分离到 A2 独立音轨，可直接拖动与调参。");
    }

    // --- Titles & Inline Styles ---

    private void AddTitleClick(object sender, RoutedEventArgs e)
    {
        if (Busy || project.Clips.Length == 0) return;
        InsertTitleWithStyle("默认文字", TextStyle.Default);
    }

    private void TextPresetClick(object sender, RoutedEventArgs e)
    {
        if (Busy || sender is not FrameworkElement { Tag: string preset }) return;
        var style = preset switch
        {
            "bar" => new TextStyle("Microsoft YaHei UI", 42, "#FFFFFF", false, "#000000", 0, true, "#C8000000", "Center", 0, 0.34),
            "headline" => new TextStyle("Microsoft YaHei UI", 64, "#FFE45E", true, "#000000", 3, false, "#B3000000", "Center", 0, 0),
            "accent" => new TextStyle("Microsoft YaHei UI", 36, "#18D2E6", false, "#000000", 0, true, "#C80C1F26", "Center", 0, 0.30),
            _ => new TextStyle("Microsoft YaHei UI", 44, "#FFFFFF", true, "#000000", 2.5, false, "#B3000000", "Center", 0, 0.32)
        };
        if (selection.Kind == SelectionKind.Title && selection.Index >= 0 && selection.Index < project.TitleTrack.Length)
        {
            var cur = project.TitleTrack[selection.Index].EffectiveStyle;
            UpdateSelectedTitle(t => t with { Style = style with { X = cur.X, Y = cur.Y } });
            SetStatus("已应用文字样式预设");
            return;
        }
        if (project.Clips.Length == 0)
        {
            SetStatus("请先向主视频轨添加视频，再添加文字。");
            return;
        }
        string sample = preset switch { "bar" => "黑底字幕条", "headline" => "居中醒目标题", "accent" => "画中标注", _ => "影院字幕" };
        InsertTitleWithStyle(sample, style);
    }

    private void InsertTitleWithStyle(string text, TextStyle style)
    {
        Pause();
        double start = FindNonOverlappingTitleStart(head, 3.0);
        var nextTitles = project.TitleTrack.Append(new TitleLayer(text, start, 3.0, 0, style)).ToArray();
        Commit(project with { Titles = nextTitles }, new TimelineSelection(SelectionKind.Title, nextTitles.Length - 1), start);
        TitleTextBox.Focus();
        TitleTextBox.SelectAll();
        SetStatus("已在播放头添加文字，可直接在画布拖动或右侧改字。");
    }

    private double FindNonOverlappingTitleStart(double desired, double len)
    {
        double s = Math.Max(0, desired);
        for (int guard = 0; guard < 16; guard++)
        {
            var conflict = project.TitleTrack.FirstOrDefault(t => s < t.End - 0.001 && s + len > t.Start + 0.001);
            if (conflict is null) return s;
            s = conflict.End;
        }
        return s;
    }

    private void UpdateSelectedTitle(Func<TitleLayer, TitleLayer> transform)
    {
        if (selection.Kind != SelectionKind.Title || selection.Index < 0 || selection.Index >= project.TitleTrack.Length) return;
        var list = (TitleLayer[])project.TitleTrack.Clone();
        var updated = transform(list[selection.Index]);
        if (updated == list[selection.Index]) return;
        list[selection.Index] = updated;
        Commit(project with { Titles = list }, selection, head);
    }

    private void TitleTextLiveChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || refreshing || selection.Kind != SelectionKind.Title) return;
        RefreshLiveCanvas();
    }

    private void TitleSizeLiveChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || refreshing || selection.Kind != SelectionKind.Title) return;
        TitleSizeValue.Text = $"{TitleSizeSlider.Value:0} pt";
        var cur = project.TitleTrack[selection.Index].EffectiveStyle;
        liveTextStyleDraft = cur with { FontSize = Math.Round(TitleSizeSlider.Value) };
        RefreshLiveCanvas();
    }

    private void TitleStyleControlChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || refreshing || Busy || selection.Kind != SelectionKind.Title) return;
        CommitTitleInspectorChanges();
    }

    private void TitleColorClick(object sender, RoutedEventArgs e)
    {
        if (Busy || selection.Kind != SelectionKind.Title || sender is not FrameworkElement { Tag: string hex }) return;
        UpdateSelectedTitle(t => t with { Style = t.EffectiveStyle with { ColorHex = hex } });
    }

    private void TitleStyleCommit(object sender, RoutedEventArgs e)
    {
        if (!ready || refreshing || Busy || selection.Kind != SelectionKind.Title) return;
        liveTextStyleDraft = null;
        CommitTitleInspectorChanges();
    }

    private void CommitTitleInspectorChanges()
    {
        if (selection.Kind != SelectionKind.Title || selection.Index < 0 || selection.Index >= project.TitleTrack.Length) return;
        var cur = project.TitleTrack[selection.Index];
        string txt = TitleTextBox.Text.Trim();
        if (string.IsNullOrEmpty(txt)) txt = cur.Text;
        if (txt.Length > 240) txt = txt[..240];
        string font = (TitleFontBox.SelectedItem as ComboBoxItem)?.Content as string ?? cur.EffectiveStyle.FontFamily;
        var nextStyle = cur.EffectiveStyle with
        {
            FontFamily = font,
            FontSize = Math.Round(TitleSizeSlider.Value),
            Outline = TitleOutlineCheck.IsChecked == true,
            BackgroundBox = TitleBgCheck.IsChecked == true
        };
        UpdateSelectedTitle(t => t with { Text = txt, Style = nextStyle });
    }

    private void ResetTitlePosClick(object sender, RoutedEventArgs e)
    {
        if (Busy || selection.Kind != SelectionKind.Title) return;
        UpdateSelectedTitle(t => t with { Style = t.EffectiveStyle with { X = 0, Y = 0.32 } });
    }

    private void TitleTimeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyTitleTime(); e.Handled = true; }
    }

    private void ApplyTitleTimeClick(object sender, RoutedEventArgs e) => ApplyTitleTime();

    private void ApplyTitleTime()
    {
        if (Busy || selection.Kind != SelectionKind.Title) return;
        if (!EditTime.TryParse(TitleStartBox.Text, out double start) || !EditTime.TryParse(TitleDurBox.Text, out double len) || !EditTime.ValidSpan(start, len))
        {
            SetStatus("请输入有效的起点与时长（秒或时:分:秒.毫秒）。");
            return;
        }
        ApplyTitleTimelineEdit(selection.Index, start, len);
    }

    private void ApplyTitleTimelineEdit(int index, double start, double length)
    {
        if (Busy || index < 0 || index >= project.TitleTrack.Length || !EditTime.ValidSpan(start, length)) return;
        var orig = project.TitleTrack[index];
        if (Math.Abs(orig.Start - start) < 0.001 && Math.Abs(orig.Duration - length) < 0.001) return;
        var list = (TitleLayer[])project.TitleTrack.Clone();
        list[index] = orig with { Start = start, Duration = length };
        Commit(project with { Titles = list }, new TimelineSelection(SelectionKind.Title, index), head);
    }

    // --- Overlays (V2 PiP) ---

    private void ApplyOverlayTimelineEdit(int index, double start, double from, double to)
    {
        if (Busy || index < 0 || index >= project.OverlayTrack.Length) return;
        var orig = project.OverlayTrack[index];
        if (Math.Abs(orig.Start - start) < 0.001 && Math.Abs(orig.In - from) < 0.001 && Math.Abs(orig.Out - to) < 0.001) return;
        if (!EditTime.ValidSpan(start, (to - from) / orig.EffectiveSpeed)) return;
        UpdateOverlayClip(index, orig with { Start = start, In = from, Out = to });
    }

    // --- Detached Audio (A2) ---

    private async void ImportAudioClick(object sender, RoutedEventArgs e)
    {
        if (Busy || !EnsureTools()) return;
        var dialog = new OpenFileDialog
        {
            Title = "导入本地音频",
            Filter = "音频文件|*.wav;*.mp3;*.flac;*.m4a;*.aac;*.ogg;*.opus;*.wma|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        double start = head;
        importing = true;
        RefreshButtons();
        try
        {
            string path = Path.GetFullPath(dialog.FileName);
            await ReadAssetAsync(path, allowAudioOnly: true);
            AddAudio(assets[path].Asset, start);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(FirstLine(error.Message)); }
        finally
        {
            importing = false;
            RefreshButtons();
        }
    }

    private void AddAudio(MediaAsset asset, double start)
    {
        if (asset.AudioStream is null || !EditTime.ValidSpan(start, asset.Duration))
        {
            SetStatus("该文件没有可用音轨或时间范围无效。");
            return;
        }
        var paths = project.Assets.Contains(asset.Path, StringComparer.OrdinalIgnoreCase)
            ? project.Assets : [.. project.Assets, asset.Path];
        var nextAudio = project.AudioTrack.Append(new AudioLayer(asset.Path, start, 0, asset.Duration)).ToArray();
        Commit(project with { Assets = paths, Audio = nextAudio },
            new TimelineSelection(SelectionKind.Audio, nextAudio.Length - 1), head);
        SetStatus($"已添加独立音频 {asset.Name}");
    }

    private void AudioVolumeLiveChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || refreshing || selection.Kind != SelectionKind.Audio) return;
        AudioVolumeValue.Text = $"{AudioVolumeSlider.Value:0}%";
    }

    private void AudioVolumeCommit(object sender, MouseButtonEventArgs e)
    {
        if (!ready || refreshing || Busy || selection.Kind != SelectionKind.Audio) return;
        ApplyAudioInspectorChanges();
    }

    private void AudioTimeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyAudioInspectorChanges(); e.Handled = true; }
    }

    private void ApplyAudioParamsClick(object sender, RoutedEventArgs e) => ApplyAudioInspectorChanges();

    private void ApplyAudioInspectorChanges()
    {
        if (Busy || selection.Kind != SelectionKind.Audio || selection.Index < 0 || selection.Index >= project.AudioTrack.Length) return;
        var orig = project.AudioTrack[selection.Index];
        var asset = assets.GetValueOrDefault(orig.Path)?.Asset;
        if (!EditTime.TryParse(AudioStartBox.Text, out double start) ||
            !EditTime.TryParse(AudioInBox.Text, out double from) ||
            !EditTime.TryParse(AudioOutBox.Text, out double to) ||
            !EditTime.ValidSpan(start, to - from) ||
            to > (asset?.Duration ?? orig.Out) + 0.0006)
        {
            SetStatus("请检查音频起点与素材入出点范围。");
            return;
        }
        double fadeIn = EditTime.TryParse(AudioFadeInBox.Text, out double fi) ? Math.Clamp(fi, 0, 10) : orig.FadeIn;
        double fadeOut = EditTime.TryParse(AudioFadeOutBox.Text, out double fo) ? Math.Clamp(fo, 0, 10) : orig.FadeOut;
        double vol = Math.Round(AudioVolumeSlider.Value / 100.0, 2);
        var updated = orig with { Start = start, In = from, Out = to, Volume = vol, FadeIn = fadeIn, FadeOut = fadeOut };
        if (updated == orig) return;
        var list = (AudioLayer[])project.AudioTrack.Clone();
        list[selection.Index] = updated;
        Commit(project with { Audio = list }, selection, head);
    }

    private void ApplyAudioTimelineEdit(int index, double start, double from, double to)
    {
        if (Busy || index < 0 || index >= project.AudioTrack.Length) return;
        var orig = project.AudioTrack[index];
        if (Math.Abs(orig.Start - start) < 0.001 && Math.Abs(orig.In - from) < 0.001 && Math.Abs(orig.Out - to) < 0.001) return;
        if (!EditTime.ValidSpan(start, (to - from) / orig.EffectiveSpeed)) return;
        var list = (AudioLayer[])project.AudioTrack.Clone();
        list[index] = orig with { Start = start, In = from, Out = to };
        Commit(project with { Audio = list }, new TimelineSelection(SelectionKind.Audio, index), head);
    }

    // --- Track Header Hide / Mute / Lock Toggles ---

    private void TrackFlagClick(object sender, RoutedEventArgs e)
    {
        if (Busy || sender is not ToggleButton { Tag: string tag } btn) return;
        string[] parts = tag.Split(':');
        if (parts.Length != 2) return;
        string trackId = parts[0], prop = parts[1];
        bool isOn = btn.IsChecked == true;
        var cur = project.GetTrack(trackId);
        var next = prop switch
        {
            "Hidden" => cur with { Hidden = isOn },
            "Muted" => cur with { Muted = isOn },
            "Locked" => cur with { Locked = isOn },
            _ => cur
        };
        Commit(project.WithTrack(trackId, next), selection, head);
    }

    private void SyncTrackHeaderButtons()
    {
        V2HideBtn.IsChecked = project.GetTrack("V2").Hidden;
        V2MuteBtn.IsChecked = project.GetTrack("V2").Muted;
        V2LockBtn.IsChecked = project.GetTrack("V2").Locked;
        V1HideBtn.IsChecked = project.GetTrack("V1").Hidden;
        V1LockBtn.IsChecked = project.GetTrack("V1").Locked;
        A1MuteBtn.IsChecked = project.GetTrack("A1").Muted;
        T1HideBtn.IsChecked = project.GetTrack("T1").Hidden;
        T1LockBtn.IsChecked = project.GetTrack("T1").Locked;
        A2MuteBtn.IsChecked = project.GetTrack("A2").Muted;
        A2LockBtn.IsChecked = project.GetTrack("A2").Locked;
    }

    // --- Composition Assembly & On-Demand Baked Preview (for Dissolves) ---

    private static string DisplayValue(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private bool TryBuildComposition(out List<(Clip Clip, MediaAsset Asset)> clips,
        out List<(AudioLayer Layer, MediaAsset Asset)> audio,
        out List<(OverlayClip Clip, MediaAsset Asset)> overlays)
    {
        clips = [];
        audio = [];
        overlays = [];
        foreach (var clip in project.Clips)
        {
            if (!TryGetRenderAsset(clip.Path, clip.Out, out var asset)) return false;
            clips.Add((clip, asset!));
        }
        foreach (var ov in project.OverlayTrack)
        {
            if (!TryGetRenderAsset(ov.Path, ov.Out, out var asset)) return false;
            overlays.Add((ov, asset!));
        }
        foreach (var layer in project.AudioTrack)
        {
            if (!TryGetRenderAsset(layer.Path, layer.Out, out var asset)) return false;
            if (asset!.AudioStream is null)
            {
                ShowError($"素材 {asset.Name} 没有可用音轨。");
                return false;
            }
            audio.Add((layer, asset));
        }
        return true;
    }

    private bool TryGetRenderAsset(string path, double end, out MediaAsset? asset)
    {
        asset = assets.GetValueOrDefault(path)?.Asset;
        if (asset is null || !File.Exists(path))
        {
            ShowError($"找不到素材 {Path.GetFileName(path)}。");
            return false;
        }
        if (end <= asset.Duration + 0.0006) return true;
        ShowError($"素材 {asset.Name} 的时长已改变。请调整片段的出点。");
        return false;
    }

    private async Task<bool> RenderPreviewAsync()
    {
        if (!EnsureTools() || !TryBuildComposition(out var clips, out var audio, out var overlays)) return false;
        Pause();
        StopMedia("正在生成转场合成预览…");
        if (renderedPreview is not null)
        {
            try { File.Delete(renderedPreview); }
            catch (IOException) { }
            renderedPreview = null;
            renderedProject = null;
        }
        using var operation = new CancellationTokenSource();
        previewing = operation;
        string output = Path.Combine(cache, $"preview-{Guid.NewGuid():N}.mp4");
        ExportHeading.Text = "正在生成转场合成预览";
        ExportStatus.Text = "正在准备叠化与音轨…";
        ExportProgress.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        ExportResultActions.Visibility = Visibility.Collapsed;
        CancelExportButton.Visibility = Visibility.Visible;
        CancelExportButton.IsEnabled = true;
        ExportOverlay.Visibility = Visibility.Visible;
        CancelExportButton.Focus();
        RefreshButtons();
        var progress = new Progress<double>(fraction =>
        {
            if (previewing != operation || operation.IsCancellationRequested) return;
            ExportProgress.Value = fraction * 100;
            ExportStatus.Text = $"正在生成  {fraction:P0}";
        });
        try
        {
            await tools.ExportAsync(clips, new ExportOptions(480, 30, project.Format, false),
                output, progress, operation.Token, false, project.TitleTrack, audio, overlays, project.TrackStates);
            renderedProject = project;
            renderedPreview = output;
            SetStatus("转场合成预览已就绪");
            return true;
        }
        catch (OperationCanceledException) { SetStatus("已取消合成预览"); }
        catch (Exception error) { ShowError(FirstLine(error.Message)); }
        finally
        {
            previewing = null;
            ExportOverlay.Visibility = Visibility.Collapsed;
            RefreshButtons();
            PlayButton.Focus();
            if (renderedPreview is null) Seek(head);
        }
        return false;
    }

    private sealed class EditFields : Window
    {
        public bool Removed { get; private set; }

        public EditFields(string title, string message, (string Label, string Value)[] rows,
            Func<string[], string?> validate, bool canRemove = false)
        {
            Title = title;
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
            FontSize = 12.5;
            SetResourceReference(BackgroundProperty, "Surface");
            SetResourceReference(ForegroundProperty, "Text");
            SourceInitialized += (_, _) => WindowColors.Apply(this);

            var root = new StackPanel();
            root.SetResourceReference(Panel.BackgroundProperty, "Surface");

            var header = new Border { Padding = new Thickness(20, 14, 20, 12), BorderThickness = new Thickness(0, 0, 0, 1) };
            header.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
            header.SetResourceReference(Border.BorderBrushProperty, "Line");
            var headerStack = new StackPanel();
            headerStack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
            var desc = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            headerStack.Children.Add(desc);
            header.Child = headerStack;
            root.Children.Add(header);

            var body = new StackPanel { Margin = new Thickness(20, 16, 20, 12) };
            List<TextBox> inputs = [];
            foreach (var field in rows)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                var label = new Label { Content = field.Label, Width = 126, Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center };
                label.SetResourceReference(ForegroundProperty, "TextSecondary");
                DockPanel.SetDock(label, Dock.Left);
                row.Children.Add(label);
                var input = new TextBox { Text = field.Value, MinWidth = 120 };
                label.Target = input;
                System.Windows.Automation.AutomationProperties.SetName(input, field.Label);
                row.Children.Add(input);
                body.Children.Add(row);
                inputs.Add(input);
            }
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 4) };
            error.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x87, 0x87));
            body.Children.Add(error);
            root.Children.Add(body);

            var footer = new Border { Padding = new Thickness(20, 11, 20, 11), BorderThickness = new Thickness(0, 1, 0, 0) };
            footer.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
            footer.SetResourceReference(Border.BorderBrushProperty, "Line");
            var actions = new DockPanel();
            if (canRemove)
            {
                var remove = new Button { Content = "移除此项", Padding = new Thickness(10, 5, 10, 5) };
                remove.SetResourceReference(StyleProperty, "GhostDangerButton");
                remove.Click += (_, _) => { Removed = true; DialogResult = true; };
                DockPanel.SetDock(remove, Dock.Left);
                actions.Children.Add(remove);
            }
            var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            rightButtons.Children.Add(new Button { Content = "取消", IsCancel = true, MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) });
            var apply = new Button { Content = "应用", IsDefault = true, MinWidth = 76 };
            apply.SetResourceReference(StyleProperty, "Primary");
            apply.Click += (_, _) =>
            {
                string? problem = validate(inputs.Select(input => input.Text).ToArray());
                if (problem is null) DialogResult = true;
                else error.Text = problem;
            };
            rightButtons.Children.Add(apply);
            actions.Children.Add(rightButtons);
            footer.Child = actions;
            root.Children.Add(footer);

            Content = root;
            Loaded += (_, _) => { inputs[0].Focus(); inputs[0].SelectAll(); };
        }
    }
}
