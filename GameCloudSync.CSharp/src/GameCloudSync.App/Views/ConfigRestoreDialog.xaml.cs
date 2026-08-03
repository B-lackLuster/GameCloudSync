using System.Windows;
using GameCloudSync.App.ViewModels;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;
using GameCloudSync.Core.Util;
using Localization = GameCloudSync.Core.Services.Localization;

namespace GameCloudSync.App.Views;

public partial class ConfigRestoreDialog : Window
{
    private readonly MainViewModel _vm;
    private List<ConfigSourceItem> _items = new();

    public ConfigRestoreDialog(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        Loaded += async (_, _) => await LoadAsync();
    }

    public (string Source, string Filename, IReadOnlyList<GameConfig> Games)? Result { get; private set; }

    private async Task LoadAsync()
    {
        var summaries = await _vm.ListConfigSourcesAsync();
        _items = summaries.Select(s => new ConfigSourceItem(
            s.Source,
            Localization.T("CfgRestore.Summary", s.Filename, s.Games.Count),
            s.Status,
            s)).ToList();
        SourceList.ItemsSource = _items;
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not ConfigSourceItem item)
        {
            MessageBox.Show(this, Localization.T("CfgRestore.SelectFirst"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Result = (item.CloudSummary.Source, item.CloudSummary.Filename, item.CloudSummary.Games);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

public sealed class ConfigSourceItem
{
    public ConfigSourceItem(string source, string summary, string status, CloudConfigSummary cloudSummary)
    {
        Source = source;
        Summary = summary;
        Status = status;
        CloudSummary = cloudSummary;
    }

    public string Source { get; }
    public string Summary { get; }
    public string Status { get; }
    public CloudConfigSummary CloudSummary { get; }
}
