using System.ComponentModel;
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

        Title = AppMetadata.DisplayNameWithVersion;
        _config = config;
        ApiKeyBox.Password = config.ApiKey;
        TargetLanguageBox.Text = AppConfig.NormalizeTargetLanguage(config.TargetLanguage);
        TargetGameBox.Text = AppConfig.NormalizeTargetGame(config.TargetGame);
        DiagnosticsBox.IsChecked = config.DiagnosticsEnabled;
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
        _config.TargetLanguage = AppConfig.NormalizeTargetLanguage(TargetLanguageBox.Text);
        _config.TargetGame = AppConfig.NormalizeTargetGame(TargetGameBox.Text);
        _config.DiagnosticsEnabled = DiagnosticsBox.IsChecked == true;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ClearData_Click(object sender, RoutedEventArgs e)
    {
        AppDataMaintenance.ClearSensitiveData();
        MessageBox.Show(
            this,
            "Troubleshooting data has been cleared.",
            "Settings",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

}
