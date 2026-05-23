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
        ModelBox.Text = AppConfig.NormalizeModel(config.Model);
        TargetLanguageBox.SelectedValue = AppConfig.NormalizeTargetLanguage(config.TargetLanguage);
        LastUpdatedText.Text = AppInfo.LastUpdated;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = ApiKeyBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(apiKey) && !AppConfig.HasGeminiApiKey(apiKey))
        {
            MessageBox.Show(this, "Enter a Gemini API key that starts with AIza.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config.ApiKey = apiKey;
        _config.Model = AppConfig.NormalizeModel(ModelBox.Text);
        _config.TargetLanguage = AppConfig.NormalizeTargetLanguage(TargetLanguageBox.SelectedValue as string);

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
