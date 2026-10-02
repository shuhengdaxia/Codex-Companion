using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--skin-host") return SkinBridge.RunHost(args[1]);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--preview")
            {
                string output = Path.GetFullPath(args[1]);
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                using (var form = new MainForm(new AppController(Path.Combine(Path.GetDirectoryName(output), "preview-data"), Path.Combine(Path.GetDirectoryName(output), "preview-codex")), true, new PreviewThemeGalleryService()))
                {
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Shown += delegate { form.BeginInvoke((Action)delegate { form.SavePreview(output); form.Close(); }); };
                    Application.Run(form);
                }
                return 0;
            }
            if (args.Length == 2 && args[0] == "--gallery-preview")
            {
                string output = Path.GetFullPath(args[1]);
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                using (var form = new ThemeGalleryForm(new PreviewThemeGalleryService(), true))
                {
                    form.SelectGalleryTab();
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Shown += delegate { form.BeginInvoke((Action)delegate { form.SavePreview(output); form.Close(); }); };
                    Application.Run(form);
                }
                return 0;
            }
            if (args.Length == 2 && args[0] == "--gallery-preview-live")
            {
                string output = Path.GetFullPath(args[1]);
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                string cache = Path.Combine(Path.GetDirectoryName(output), "gallery-live-cache");
                int exitCode = 1;
                using (var form = new ThemeGalleryForm(new CodexThemeGalleryService(new GalleryCatalog(cache)), true))
                {
                    form.SelectGalleryTab();
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Shown += async delegate
                    {
                        try
                        {
                            Task ready = form.ContentReady;
                            Task timeout = Task.Delay(TimeSpan.FromSeconds(60));
                            if (await Task.WhenAny(ready, timeout) != ready)
                                throw new TimeoutException("等待全量主题目录和首项预览图超过 60 秒。");
                            await ready;
                            form.SavePreview(output);
                            exitCode = 0;
                        }
                        catch (Exception error)
                        {
                            File.WriteAllText(output + ".error.txt", error.Message, new System.Text.UTF8Encoding(false));
                        }
                        finally { form.Close(); }
                    };
                    Application.Run(form);
                }
                return exitCode;
            }
            if (args.Length != 0) return 2;
            Application.Run(new MainForm(new AppController(), false, new CodexThemeGalleryService(new GalleryCatalog())));
            return 0;
        }
        catch (Exception error)
        {
            if (args.Length == 0) MessageBox.Show(error.Message, "Codex Companion", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
