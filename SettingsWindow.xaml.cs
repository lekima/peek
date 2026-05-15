using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        PopulateModels(config.Model);
        FromLanguageBox.Text = string.IsNullOrWhiteSpace(config.FromLanguage) ? "Chinese" : config.FromLanguage;
        ToLanguageBox.Text = string.IsNullOrWhiteSpace(config.ToLanguage) ? "Vietnamese" : config.ToLanguage;
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
        var apiKey = ApiKeyBox.Password.Trim();
        var model = (ModelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? AppConfig.DefaultModel;
        var fromLanguage = string.IsNullOrWhiteSpace(FromLanguageBox.Text)
            ? "Chinese"
            : FromLanguageBox.Text.Trim();
        var toLanguage = string.IsNullOrWhiteSpace(ToLanguageBox.Text)
            ? "Vietnamese"
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
