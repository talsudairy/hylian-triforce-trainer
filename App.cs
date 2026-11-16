using System.Windows;

namespace Tozot;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--selftest-")) return SelfTest.Run(args);
        if (args.Length > 0 && args[0] == "--install-quiet")   // no window: install, replace the TOZOT-named install, no desktop shortcut
        {
            string result;
            try { result = "installed " + Setup.Install(desktopShortcut: args.Contains("--desktop")); }
            catch (Exception ex) { result = "FAILED " + ex.Message; }
            if (args.Length > 1 && !args[^1].StartsWith("--")) System.IO.File.WriteAllText(args[^1], result);
            return result.StartsWith("installed") ? 0 : 1;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Glass.xaml") });
        if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Setup.RunUninstall();
            return 0;
        }
        if (args.Length > 1 && args[0] == "--shots")   // README pictures, see MainWindow.TakeShots
        {
            var mw = new MainWindow { Left = 40, Top = 0 };
            mw.ContentRendered += async (_, _) => { try { await mw.TakeShots(args[1], args.Length > 2 ? args[2] : null); } catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(args[1], "error.txt"), ex.ToString()); mw.Close(); } };
            return app.Run(mw);
        }
        Window w = Setup.IsSetupLaunch(args) ? Setup.CreateSetupWindow() : new MainWindow();
        return app.Run(w);
    }
}
