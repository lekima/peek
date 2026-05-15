using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace Peek;

internal sealed partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(config);

        _config = config;
        ApiKeyBox.Password = config.ApiKey;
        TextFormatButton.IsChecked = config.ResultFormat != ResultFormat.Image;
        ImageFormatButton.IsChecked = config.ResultFormat == ResultFormat.Image;
        FromLanguageBox.Text = string.IsNullOrWhiteSpace(config.FromLanguage) ? "Chinese" : config.FromLanguage;
        ToLanguageBox.Text = string.IsNullOrWhiteSpace(config.ToLanguage) ? "English" : config.ToLanguage;
        StartupBox.IsChecked = StartupService.IsEnabled();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = ApiKeyBox.Password.Trim();
        var resultFormat = ImageFormatButton.IsChecked == true ? ResultFormat.Image : ResultFormat.Text;
        var fromLanguage = string.IsNullOrWhiteSpace(FromLanguageBox.Text)
            ? "Chinese"
            : FromLanguageBox.Text.Trim();
        var toLanguage = string.IsNullOrWhiteSpace(ToLanguageBox.Text)
            ? "English"
            : ToLanguageBox.Text.Trim();
        Exception? startupException = null;

        try
        {
            StartupService.SetEnabled(StartupBox.IsChecked == true);
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or InvalidOperationException)
        {
            startupException = ex;
        }

        _config.ApiKey = apiKey;
        _config.ResultFormat = resultFormat;
        _config.FromLanguage = fromLanguage;
        _config.ToLanguage = toLanguage;

        if (startupException is not null)
        {
            MessageBox.Show(
                this,
                $"Startup setting was not saved. Other settings will still be saved.\n\n{startupException.Message}",
                "Startup setting",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("Log opened from settings.");
        try
        {
            OpenFile(AppLogger.LogPath);
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Log", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void OpenFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, string.Empty);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}
