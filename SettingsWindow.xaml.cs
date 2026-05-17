using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
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
        InitializeSearchSourceBox(config.SearchSource);
        InitializeGameSearchPrefixBox(config.GameSearchPrefix);
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
        _config.SearchSource = GetSelectedSearchSource();
        _config.GameSearchPrefix = GetSelectedGameSearchPrefix();

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

    private void InitializeGameSearchPrefixBox(GameSearchPrefix selectedPrefix)
    {
        GameSearchPrefixBox.Items.Add(CreateGameSearchPrefixItem(GameSearchPrefix.None));
        GameSearchPrefixBox.Items.Add(CreateGameSearchPrefixItem(GameSearchPrefix.HonorOfKingsWorld));
        GameSearchPrefixBox.Items.Add(CreateGameSearchPrefixItem(GameSearchPrefix.RocoKingdomWorld));

        foreach (ComboBoxItem item in GameSearchPrefixBox.Items)
        {
            if (item.Tag is GameSearchPrefix prefix && prefix == selectedPrefix)
            {
                GameSearchPrefixBox.SelectedItem = item;
                return;
            }
        }

        GameSearchPrefixBox.SelectedIndex = 0;
    }

    private void InitializeSearchSourceBox(SearchSource selectedSource)
    {
        SearchSourceBox.Items.Add(CreateSearchSourceItem(SearchSource.Bilibili));
        SearchSourceBox.Items.Add(CreateSearchSourceItem(SearchSource.YouTube));
        SearchSourceBox.Items.Add(CreateSearchSourceItem(SearchSource.YouTubeChinese));

        foreach (ComboBoxItem item in SearchSourceBox.Items)
        {
            if (item.Tag is SearchSource source && source == selectedSource)
            {
                SearchSourceBox.SelectedItem = item;
                return;
            }
        }

        SearchSourceBox.SelectedIndex = 0;
    }

    private static ComboBoxItem CreateSearchSourceItem(SearchSource source) =>
        new()
        {
            Content = SearchProfiles.Get(source).DisplayName,
            Tag = source
        };

    private SearchSource GetSelectedSearchSource() =>
        SearchSourceBox.SelectedItem is ComboBoxItem { Tag: SearchSource source }
            ? source
            : SearchSource.Bilibili;

    private static ComboBoxItem CreateGameSearchPrefixItem(GameSearchPrefix prefix) =>
        new()
        {
            Content = GameSearchPrefixes.GetDisplayName(prefix),
            Tag = prefix
        };

    private GameSearchPrefix GetSelectedGameSearchPrefix() =>
        GameSearchPrefixBox.SelectedItem is ComboBoxItem { Tag: GameSearchPrefix prefix }
            ? prefix
            : GameSearchPrefix.None;
}
