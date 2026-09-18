using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace REE.Unpacker
{
    public partial class MainWindow : Window
    {
        const String ProjectName = "OWOTS_STM_Release";
        bool _running;
        String _currentPak = "";

        public MainWindow()
        {
            InitializeComponent();
        }

        void BtnRoot_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择游戏根目录" };
            if (dlg.ShowDialog() == true) RootBox.Text = dlg.FolderName;
        }

        void BtnOut_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择输出目录" };
            if (dlg.ShowDialog() == true) OutBox.Text = dlg.FolderName;
        }

        async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;

            String root = RootBox.Text?.Trim();
            String outDir = OutBox.Text?.Trim();
            if (String.IsNullOrEmpty(root) || !Directory.Exists(root)) { SetStatus("游戏根目录无效"); return; }
            if (String.IsNullOrEmpty(outDir)) { SetStatus("请选择输出目录"); return; }

            var paks = Directory.GetFiles(root, "*.pak", SearchOption.TopDirectoryOnly)
                                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            if (paks.Length == 0) { SetStatus("未在根目录找到 .pak 文件"); return; }

            _running = true;
            SetBusy(true);
            LogBox.Clear();
            Bar.Value = 0;
            Directory.CreateDirectory(outDir);
            String dst = outDir.EndsWith("\\") ? outDir : outDir + "\\";

            Utils.LogSink = s => Report(s);
            PakUnpack.Progress = (cur, total, file) =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Bar.Value = total > 0 ? (double)cur * 100 / total : 0;
                    StatusText.Text = _currentPak + " — " + cur + "/" + total + " — " + file;
                }));

            try
            {
                await Task.Run(() =>
                {
                    Report("加载文件名列表: " + ProjectName);
                    PakList.iLoadProject(ProjectName);
                    for (int i = 0; i < paks.Length; i++)
                    {
                        _currentPak = "PAK " + (i + 1) + "/" + paks.Length + ": " + Path.GetFileName(paks[i]);
                        Report("开始: " + _currentPak);
                        try { PakUnpack.iDoIt(paks[i], dst); }
                        catch (Exception ex) { Report("[ERROR] " + Path.GetFileName(paks[i]) + ": " + ex.Message); }
                        Report("完成: " + Path.GetFileName(paks[i]));
                    }
                });
                Bar.Value = 100;
                SetStatus("全部完成 → " + outDir);
            }
            catch (Exception ex) { SetStatus("失败: " + ex.Message); }
            finally
            {
                Utils.LogSink = null;
                PakUnpack.Progress = null;
                SetBusy(false);
                _running = false;
            }
        }

        void Report(String s) => Dispatcher.BeginInvoke(new Action(() =>
        {
            LogBox.AppendText(s + Environment.NewLine);
            LogBox.ScrollToEnd();
        }));

        void SetStatus(String s) => StatusText.Text = s;

        void SetBusy(bool busy)
        {
            BtnStart.IsEnabled = !busy;
            BtnRoot.IsEnabled = !busy;
            BtnOut.IsEnabled = !busy;
            RootBox.IsEnabled = !busy;
            OutBox.IsEnabled = !busy;
        }
    }
}
