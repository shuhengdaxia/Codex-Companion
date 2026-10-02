using System;
using System.IO;
using System.Threading;

internal static class TestProgram
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length != 1 && !(args.Length == 2 && (args[1] == "--advertisements" || args[1] == "--ui" || args[1] == "--gallery" || args[1] == "--restart" || args[1] == "--update" || args[1] == "--relay"))) return 2;
            string directory = Path.GetFullPath(args[0]);
            if (Directory.Exists(directory)) throw new InvalidOperationException("测试目录必须是新的独立目录。");
            Directory.CreateDirectory(directory);
            if (args.Length == 2)
            {
                if (args[1] == "--gallery") return GalleryTests.Run(Path.Combine(directory, "gallery"), false);
                if (args[1] == "--restart") return RestartTests.Run();
                if (args[1] == "--update") { AutoUpdaterTests.Run(Path.Combine(directory, "update")); return 0; }
                if (args[1] == "--relay") { Regression.RunRelayContract(); return 0; }
                if (args[1] == "--ui")
                {
                    DesktopUiTests.Run(Path.Combine(directory, "ui"));
                    return 0;
                }
                AdvertisementTests.Run(Path.Combine(directory, "advertisements"));
                return 0;
            }
            int regression = Regression.Run(directory);
            AutoUpdaterTests.Run(Path.Combine(directory, "update"));
            int restart = RestartTests.Run();
            // RestartTests creates WinForms controls. Keep synchronous gallery
            // regression waits off the UI synchronization context.
            SynchronizationContext.SetSynchronizationContext(null);
            int gallery = GalleryTests.Run(Path.Combine(directory, "gallery"), false);
            AdvertisementTests.Run(Path.Combine(directory, "advertisements"));
            DesktopUiTests.Run(Path.Combine(directory, "ui"));
            return regression == 0 && restart == 0 && gallery == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
