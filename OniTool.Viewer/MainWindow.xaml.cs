﻿using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using OniTool.Core;

namespace OniTool.App;

public partial class MainWindow : Window
{
    const string Host = "oniapp.assets";
    string AssetDir => AppContext.BaseDirectory;

    ParsedMesh? _mesh;
    string? _meshPath;
    int _glbCounter;
    readonly MediaPlayer _player = new();
    string? _lastWav;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitWeb();
    }

    async Task InitWeb()
    {
        await Web.EnsureCoreWebView2Async();
        Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            Host, AssetDir, CoreWebView2HostResourceAccessKind.Allow);
        SetStatus(AudioConverter.HasVgmstream ? "就绪" : "就绪 (未找到 vgmstream，音频不可用)");
    }

    void SetStatus(string s) => Status.Text = s;

    // ---------------- directory tree ----------------

    void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择解包目录 (natives)" };
        if (dlg.ShowDialog() == true) LoadRoot(dlg.FolderName);
    }

    void LoadRoot(string root)
    {
        RootLabel.Text = root;
        Tree.Items.Clear();
        Tree.Items.Add(MakeDirNode(root));
    }

    TreeViewItem MakeDirNode(string path)
    {
        var item = new TreeViewItem
        {
            Header = "📁 " + Path.GetFileName(path.TrimEnd('\\', '/')),
            Tag = path,
            Foreground = Brushes.Gainsboro,
        };
        item.Items.Add("__dummy__");
        item.Expanded += DirExpanded;
        return item;
    }

    TreeViewItem MakeFileNode(string path)
    {
        return new TreeViewItem
        {
            Header = "📄 " + Path.GetFileName(path),
            Tag = path,
            Foreground = Brushes.Gainsboro,
        };
    }

    void DirExpanded(object sender, RoutedEventArgs e)
    {
        var item = (TreeViewItem)sender;
        if (item.Items.Count != 1 || item.Items[0] is not string) return; // already populated
        item.Items.Clear();
        var dir = (string)item.Tag;
        var filter = Filter.Text?.Trim() ?? "";
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x))
                item.Items.Add(MakeDirNode(d));
            foreach (var f in Directory.EnumerateFiles(dir).OrderBy(x => x))
            {
                if (filter.Length > 0 && !Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                item.Items.Add(MakeFileNode(f));
            }
        }
        catch (Exception ex) { SetStatus("读取目录失败: " + ex.Message); }
    }

    void Filter_TextChanged(object sender, TextChangedEventArgs e) { /* applied on next expand */ }

    // ---------------- selection dispatch ----------------

    async void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem item || item.Tag is not string path) return;
        if (Directory.Exists(path)) return;
        var name = Path.GetFileName(path);

        try
        {
            if (name.Contains(".mesh.", StringComparison.OrdinalIgnoreCase))
                await LoadMesh(path);
            else if (name.Contains(".motlist.", StringComparison.OrdinalIgnoreCase)
                     || name.Contains(".mot.", StringComparison.OrdinalIgnoreCase))
                await LoadMotlist(path);
            else if (AudioConverter.IsAudio(path))
                await LoadAudio(path);
        }
        catch (Exception ex) { SetStatus("失败: " + ex.Message); }
    }

    // ---------------- model / animation ----------------

    async Task LoadMesh(string path)
    {
        SetStatus("解析模型…");
        var mesh = await Task.Run(() => MeshReader.Read(path));
        _mesh = mesh; _meshPath = path;
        var glb = await Task.Run(() => ExportGlb(mesh, null));
        NavigateGlb(glb);
        int bones = mesh.Skeleton?.Bones.Count ?? 0;
        SetStatus($"模型: {Path.GetFileName(path)} · LOD0 · 骨骼 {bones} · 已选模型可叠加动作");
    }

    async Task LoadMotlist(string path)
    {
        if (_mesh == null) { SetStatus("请先在左侧选择一个 .mesh 模型，再选择动作"); return; }
        SetStatus("解析动作…");
        var ml = await Task.Run(() => MotReader.Read(path));
        var mesh = _mesh;
        var glb = await Task.Run(() => ExportGlb(mesh, ml.Clips));
        NavigateGlb(glb);
        SetStatus($"动作: {Path.GetFileName(path)} · 片段 {ml.Clips.Count} · 模型 {Path.GetFileName(_meshPath!)}");
    }

    string ExportGlb(ParsedMesh mesh, IReadOnlyList<MotClip>? anims)
    {
        int n = System.Threading.Interlocked.Increment(ref _glbCounter);
        var outPath = Path.Combine(AssetDir, $"preview_{n}.glb");
        // clean older previews
        foreach (var old in Directory.EnumerateFiles(AssetDir, "preview_*.glb"))
            if (old != outPath) try { File.Delete(old); } catch { }
        GlbExporter.ExportGlb(mesh, outPath, 0, anims);
        return Path.GetFileName(outPath);
    }

    void NavigateGlb(string glbFileName)
    {
        Web.CoreWebView2.Navigate($"https://{Host}/viewer.html?src={glbFileName}");
    }

    // ---------------- audio ----------------

    async Task LoadAudio(string path)
    {
        AudioName.Text = "音频: " + Path.GetFileName(path);
        if (!AudioConverter.HasVgmstream) { SetStatus("vgmstream 不可用"); return; }
        SetStatus("转换音频…");
        var wav = Path.Combine(Path.GetTempPath(), "OniTool", "preview.wav");
        _lastWav = await Task.Run(() => AudioConverter.ToWav(path, wav));
        SetStatus("音频就绪，点击播放");
    }

    void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_lastWav == null || !File.Exists(_lastWav)) { SetStatus("无可播放音频"); return; }
        _player.Open(new Uri(_lastWav));
        _player.Play();
        SetStatus("播放中…");
    }

    void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        SetStatus("已停止");
    }

    void BtnExportAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_lastWav == null || !File.Exists(_lastWav)) { SetStatus("先选择一个音频文件"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "WAV 音频|*.wav",
            FileName = Path.GetFileNameWithoutExtension(AudioName.Text.Replace("音频: ", "")) + ".wav",
        };
        if (dlg.ShowDialog() == true)
        {
            File.Copy(_lastWav, dlg.FileName, true);
            SetStatus("已导出: " + dlg.FileName);
        }
    }
}
