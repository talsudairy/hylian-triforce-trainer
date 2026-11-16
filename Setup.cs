using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Tozot;

/// <summary>
/// Per-user install, no administrator rights: the single-file exe copies itself to %LOCALAPPDATA%\Programs\Hylian Triforce, adds
/// Start menu (and optionally desktop) shortcuts and a Settings > Apps entry whose Uninstall runs "--uninstall".
/// The same exe is the installer: run under a name containing "setup" (or with --install) it shows the setup window.
/// Adapted from this repository's Notes installer (src/Notes/Setup.cs).
/// </summary>
public static class Setup
{
    public const string AppName = "Hylian Triforce Trainer & Save Editor";
    const string ExeName = "Hylian Triforce Trainer & Save Editor.exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HylianTriforce";

    // this same program's install under its old name, TOZOT: replaced by the install under the new name
    const string OldName = "TOZOT Trainer & Save Editor";
    const string OldUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TOZOT";
    static string OldInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TOZOT");

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Hylian Triforce");
    static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
    static string Version => typeof(Setup).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static bool IsSetupLaunch(string[] args)
    {
        if (args.Any(a => a.Equals("--install", StringComparison.OrdinalIgnoreCase))) return true;
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        return name.Contains("setup", StringComparison.OrdinalIgnoreCase);
    }

    public static string Install(bool desktopShortcut)
    {
        var exe = Path.Combine(InstallDir, ExeName);
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the setup file.");
        Directory.CreateDirectory(InstallDir);
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Copy(self, exe, overwrite: true); }
            catch (IOException) { throw new IOException($"{AppName} is open. Close it and try again."); }
        }
        CreateShortcut(StartMenuLink, exe);
        if (desktopShortcut) CreateShortcut(DesktopLink, exe);
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "Hylian Triforce");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("DisplayIcon", exe);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        RemoveOldName();
        return exe;
    }

    /// <summary>
    /// Removes the TOZOT-named install of this same program: its shortcuts, its Apps entry, and its folder only when
    /// that folder holds nothing but the old program's own files. Saves and save backups are never touched.
    /// </summary>
    static void RemoveOldName()
    {
        foreach (var dir in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory })
            try { File.Delete(Path.Combine(Environment.GetFolderPath(dir), OldName + ".lnk")); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(OldUninstallKey, false); } catch { }
        try
        {
            var entries = Directory.Exists(OldInstallDir) ? Directory.GetFileSystemEntries(OldInstallDir) : Array.Empty<string>();
            if (entries.Length > 0 && entries.All(f => Path.GetFileName(f).StartsWith(OldName, StringComparison.OrdinalIgnoreCase)))
                Directory.Delete(OldInstallDir, recursive: true);
        }
        catch { }   // the old copy is still open: its folder stays, harmless
    }

    public static void RunUninstall()
    {
        if (MessageBox.Show($"Remove {AppName} from this PC?\n\nYour game, saves and save backups are not touched.",
                $"Uninstall {AppName}", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        try { File.Delete(StartMenuLink); } catch { }
        try { File.Delete(DesktopLink); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        // the program folder goes once this process has exited
        int pid = Environment.ProcessId;
        string wait = $"for /l %i in (1,1,40) do (tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul && timeout /t 1 /nobreak >nul)";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c {wait} & rmdir /s /q \"{InstallDir}\"")
        { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        MessageBox.Show($"{AppName} has been removed.", AppName);
    }

    static void CreateShortcut(string path, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        link.Description = AppName;
        link.IconLocation = target + ",0";
        link.Save();
    }

    public static Window CreateSetupWindow()
    {
        bool installed = File.Exists(Path.Combine(InstallDir, ExeName));
        var w = new Window
        {
            Title = "Install " + AppName, Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Theme.Bg, Foreground = Theme.Text,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 14,
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/TOZOT.ico")),
        };
        var root = new StackPanel { Margin = new Thickness(32, 28, 32, 24) };
        root.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/TOZOT.ico")), Width = 80, Height = 80 });
        root.Children.Add(new TextBlock { Text = AppName, FontSize = 22, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 2), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        root.Children.Add(new TextBlock { Text = "Version " + Version, Foreground = Theme.Dim, HorizontalAlignment = HorizontalAlignment.Center });
        root.Children.Add(new TextBlock
        {
            Text = installed ? "It is already installed. Installing again updates it." : "Installs for you only. No administrator rights are needed.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 16, 0, 16),
        });
        var desktop = new CheckBox { Content = "Add a shortcut to the desktop", IsChecked = true, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Theme.Text };
        root.Children.Add(desktop);
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        root.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = Theme.Button("Cancel"); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 10, 0);
        cancel.Click += (_, _) => w.Close();
        var install = Theme.Button(installed ? "Update" : "Install", accent: true); install.IsDefault = true;
        install.Click += (_, _) =>
        {
            try
            {
                var exe = Install(desktop.IsChecked == true);
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
                w.Close();
            }
            catch (Exception ex) { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(install);
        root.Children.Add(buttons);
        root.Children.Add(new TextBlock { Text = "Installs to " + InstallDir, FontSize = 11, Foreground = Theme.Dim, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 18, 0, 0) });
        w.Content = root;
        return w;
    }
}
