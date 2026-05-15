using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace Peek;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        ApiKeyBox.Password = config.ApiKey;
        PopulateModels(config.Model);
        FromLanguageBox.Text = string.IsNullOrWhiteSpace(config.FromLanguage) ? "Chinese" : config.FromLanguage;
        ToLanguageBox.Text = string.IsNullOrWhiteSpace(config.ToLanguage) ? "English" : config.ToLanguage;
        StartupBox.IsChecked = StartupService.IsEnabled();
    }

    private void PopulateModels(string selectedModel)
    {
        ModelBox.Items.Clear();

        foreach (var option in AppConfig.ModelOptions)
        {
            var item = new ComboBoxItem
            {
                Content = option.Name,
                Tag = option.Id
            };

            ModelBox.Items.Add(item);
            if (string.Equals(option.Id, selectedModel, StringComparison.OrdinalIgnoreCase))
            {
                ModelBox.SelectedItem = item;
            }
        }

        ModelBox.SelectedItem ??= ModelBox.Items[0];
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _config.ApiKey = ApiKeyBox.Password.Trim();
        _config.Model = (ModelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? AppConfig.DefaultModel;
        _config.FromLanguage = string.IsNullOrWhiteSpace(FromLanguageBox.Text)
            ? "Chinese"
            : FromLanguageBox.Text.Trim();
        _config.ToLanguage = string.IsNullOrWhiteSpace(ToLanguageBox.Text)
            ? "English"
            : ToLanguageBox.Text.Trim();
        StartupService.SetEnabled(StartupBox.IsChecked == true);

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
        OpenFile(AppLogger.LogPath);
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
