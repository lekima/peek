using System.Diagnostics;
using System.IO;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace Peek;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        ApiKeyBox.Password = config.ApiKey;
        TextFormatButton.IsChecked = !AppConfig.IsImageEditModel(config.Model);
        ImageFormatButton.IsChecked = AppConfig.IsImageEditModel(config.Model);
        FromLanguageBox.Text = string.IsNullOrWhiteSpace(config.FromLanguage) ? "Chinese" : config.FromLanguage;
        ToLanguageBox.Text = string.IsNullOrWhiteSpace(config.ToLanguage) ? "English" : config.ToLanguage;
        StartupBox.IsChecked = StartupService.IsEnabled();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = ApiKeyBox.Password.Trim();
        var model = ImageFormatButton.IsChecked == true
            ? AppConfig.Gemini31FlashImageModel
            : AppConfig.Gemini31FlashLiteModel;
        var fromLanguage = string.IsNullOrWhiteSpace(FromLanguageBox.Text)
            ? "Chinese"
            : FromLanguageBox.Text.Trim();
        var toLanguage = string.IsNullOrWhiteSpace(ToLanguageBox.Text)
            ? "English"
            : ToLanguageBox.Text.Trim();

        try
        {
            StartupService.SetEnabled(StartupBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Startup setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config.ApiKey = apiKey;
        _config.Model = model;
        _config.FromLanguage = fromLanguage;
        _config.ToLanguage = toLanguage;

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
        catch (Exception ex)
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
