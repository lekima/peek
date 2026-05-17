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
        TargetLanguageBox.Text = string.IsNullOrWhiteSpace(config.TargetLanguage) ? "English" : config.TargetLanguage;
        InitializeTargetGameBox(config.TargetGame);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = ApiKeyBox.Password.Trim();
        var targetLanguage = string.IsNullOrWhiteSpace(TargetLanguageBox.Text)
            ? "English"
            : TargetLanguageBox.Text.Trim();

        _config.ApiKey = apiKey;
        _config.TargetLanguage = targetLanguage;
        _config.TargetGame = GetSelectedTargetGame();

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

    private void InitializeTargetGameBox(TargetGame selectedGame)
    {
        TargetGameBox.Items.Add(CreateTargetGameItem(TargetGame.None));
        TargetGameBox.Items.Add(CreateTargetGameItem(TargetGame.RocoKingdomWorld));
        TargetGameBox.Items.Add(CreateTargetGameItem(TargetGame.HonorOfKingsWorld));
        TargetGameBox.Items.Add(CreateTargetGameItem(TargetGame.HonorOfKings));

        foreach (ComboBoxItem item in TargetGameBox.Items)
        {
            if (item.Tag is TargetGame game && game == selectedGame)
            {
                TargetGameBox.SelectedItem = item;
                return;
            }
        }

        TargetGameBox.SelectedIndex = 0;
    }

    private static ComboBoxItem CreateTargetGameItem(TargetGame game) =>
        new()
        {
            Content = TargetGames.GetDisplayName(game),
            Tag = game
        };

    private TargetGame GetSelectedTargetGame() =>
        TargetGameBox.SelectedItem is ComboBoxItem { Tag: TargetGame game }
            ? game
            : TargetGame.None;
}
