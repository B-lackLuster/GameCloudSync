using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;
using Microsoft.Win32;
using Localization = GameCloudSync.Core.Services.Localization;

namespace GameCloudSync.App.Views;

public partial class FindSaveDialog : Window
{
    private sealed record GameRow(string Title, string Detail, CatalogGame? Game, SteamInstallation? Steam)
    {
        public string FullText => $"{Title}\n{Detail}";
    }

    private sealed record PathRow(string Title, string Detail, SaveMatch Match)
    {
        public string FullText => $"{Title}\n{Detail}";
    }

    private const int SearchLimit = 40;
    private readonly AppConfig _config;
    private readonly DispatcherTimer _queryTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _scan;
    private GameSaveCatalog? _catalog;
    private IReadOnlyList<SteamInstallation> _installed = [];
    private bool _changingGame;
    private readonly bool _chinese;

    public FindSaveDialog(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        _chinese = config.Language == Localization.Chinese;
        SearchStatus.Text = Localization.T("FindSave.Loading");
        ResultStatus.Text = Localization.T("FindSave.SelectGame");
        _queryTimer.Tick += (_, _) =>
        {
            _queryTimer.Stop();
            ShowGames();
        };
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _queryTimer.Stop();
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    public GameConfig? Result { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
        var cancellationToken = _lifetime.Token;
        try
        {
            var catalogTask = Task.Run(() => GameSaveCatalog.Default, cancellationToken);
            var steamTask = Task.Run(() => SteamInstallations.Find(cancellationToken), cancellationToken);
            await Task.WhenAll(catalogTask, steamTask);
            if (cancellationToken.IsCancellationRequested) return;
            _catalog = catalogTask.Result;
            _installed = steamTask.Result;
            ShowGames();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            if (!cancellationToken.IsCancellationRequested)
                SearchStatus.Text = Localization.T("FindSave.CatalogError");
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_catalog == null) return;
        _queryTimer.Stop();
        GameList.ItemsSource = null;
        _queryTimer.Start();
    }

    private void ShowGames()
    {
        if (_catalog == null) return;
        var query = SearchBox.Text;
        var key = GameSaveCatalog.SearchKey(query);
        var catalogGames = _catalog.Search(query, SearchLimit + 1);
        var hits = catalogGames.ToHashSet();
        var steamRows = (
            from install in _installed
            from game in _catalog.FromSteam(install.AppId).DefaultIfEmpty()
            select new GameRow(game?.DisplayTitle(_chinese) ?? install.Title,
                DescribeInstallation(game, install), game, install)).ToArray();
        var byGame = steamRows.ToLookup(row => row.Game);
        var rankedRows = catalogGames.SelectMany(game => byGame[game].Append(new GameRow(game.DisplayTitle(_chinese),
            Localization.T("FindSave.Catalog", game.Title), game, null)));
        var localNameMatches = steamRows.Where(row => (row.Game == null || !hits.Contains(row.Game))
            && (GameSaveCatalog.SearchKey(row.Steam!.Title).Contains(key, StringComparison.Ordinal)
                || row.Steam.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture) == key));
        var rows = (key.Length == 0 ? steamRows : rankedRows.Concat(localNameMatches)).Take(SearchLimit + 1).ToList();
        GameList.ItemsSource = rows.Take(SearchLimit).ToArray();
        SearchStatus.Text = Localization.T(rows.Count switch
        {
            > SearchLimit => "FindSave.MoreGames",
            0 when key.Length == 0 => "FindSave.EnterName",
            0 => "FindSave.NoGames",
            _ => "FindSave.ChooseSource"
        });
        if (key.Length > 0 && rows.Count == 1)
            GameList.SelectedIndex = 0;
    }

    private string DescribeInstallation(CatalogGame? game, SteamInstallation install)
    {
        var location = Localization.T("FindSave.Steam", install.AppId, install.Directory);
        return _chinese && game?.ChineseTitle is { } chinese && chinese != game.Title
            ? $"{game.Title} · {location}" : location;
    }

    private async void OnGameSelected(object sender, SelectionChangedEventArgs e)
    {
        _changingGame = true;
        InstallPathBox.Clear();
        _changingGame = false;
        await ScanAsync();
    }

    private void OnInstallPathChanged(object sender, TextChangedEventArgs e)
    {
        if (_changingGame || !IsLoaded) return;
        _scan?.Cancel();
        PathList.ItemsSource = null;
        ResultStatus.Text = Localization.T("FindSave.InstallChanged");
    }

    private async void OnBrowseInstallation(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Localization.T("FindSave.InstallDialog") };
        if (dialog.ShowDialog(this) != true) return;
        InstallPathBox.Text = dialog.FolderName;
        await ScanAsync();
    }

    private async void OnRetry(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        _scan?.Cancel();
        PathList.ItemsSource = null;
        var row = GameList.SelectedItem as GameRow;
        InstallPanel.Visibility = row is { Game: not null, Steam: null } ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = row?.Game != null;
        ResultStatus.Text = Localization.T(row == null ? "FindSave.SelectGame" : "FindSave.NoRules");
        if (row?.Game is not { } game || _lifetime.IsCancellationRequested) return;

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _scan = operation;
        var token = operation.Token;
        var folder = InstallPathBox.Text.Trim();
        ResultStatus.Text = Localization.T("FindSave.Scanning");
        var configured = _config.Games.ToArray();
        var cloudRoot = _config.CloudRoot;
        try
        {
            var result = await Task.Run(() =>
            {
                if (folder.Length != 0 && (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)))
                    return null;
                return new SaveLocationFinder(SaveSearchRoots.CurrentUser())
                    .Find(game, folder.Length == 0 ? null : folder, row.Steam, configured, cloudRoot, token);
            }, token);
            if (token.IsCancellationRequested) return;
            if (result == null)
            {
                ResultStatus.Text = Localization.T("FindSave.InvalidInstall");
                return;
            }

            var rows = result.Matches.Select(match => new PathRow(match.Path, Describe(match), match)).ToArray();
            PathList.ItemsSource = rows;
            var usable = rows.Where(item => item.Match.Kind == SaveMatchKind.Folder).ToArray();
            var status = new List<string>
            {
                Localization.T(rows.Length == 0 ? "FindSave.NoPaths"
                    : usable.Length == 0 ? "FindSave.NoUsable"
                    : rows.Length > 1 ? "FindSave.MultiplePaths" : "FindSave.ConfirmPath")
            };
            if (result.NeedsInstallFolder) status.Add(Localization.T("FindSave.NeedsInstall"));
            if (result.HasUnsupportedRules) status.Add(Localization.T("FindSave.Unsupported"));
            ResultStatus.Text = string.Join(" ", status);
            if (rows.Length == 1 && usable.Length == 1 && !result.NeedsInstallFolder && !result.HasUnsupportedRules)
                PathList.SelectedItem = usable[0];
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            if (!token.IsCancellationRequested)
                ResultStatus.Text = Localization.T("FindSave.SearchError");
        }
        finally
        {
            if (ReferenceEquals(_scan, operation)) _scan = null;
        }
    }

    private static string Describe(SaveMatch match)
    {
        var detail = match.Kind switch
        {
            SaveMatchKind.Folder => Localization.T("FindSave.Folder"),
            SaveMatchKind.EmptyFolder => Localization.T("FindSave.Empty"),
            SaveMatchKind.File => Localization.T("FindSave.File"),
            _ => Localization.T("FindSave.Overlap", match.ExistingGame)
        };
        if (match.Source is SaveMatchSource.UbisoftConnect or SaveMatchSource.Goldberg)
            detail = (match.Source == SaveMatchSource.UbisoftConnect ? "Ubisoft Connect" : "Goldberg") + " · " + detail;
        return match.MayBeShared && match.Kind != SaveMatchKind.AlreadyAdded
            ? $"{detail} · {Localization.T("FindSave.Shared")}" : detail;
    }

    private void OnPathSelected(object sender, SelectionChangedEventArgs e) =>
        UseButton.IsEnabled = PathList.SelectedItem is PathRow { Match.Kind: SaveMatchKind.Folder };

    private void OnUse(object sender, RoutedEventArgs e)
    {
        if (PathList.SelectedItem is not PathRow { Match.Kind: SaveMatchKind.Folder } row
            || GameList.SelectedItem is not GameRow game) return;
        ShowAddForm(game, row.Match.Path);
    }

    private void OnCustomAdd(object sender, RoutedEventArgs e) => ShowAddForm(GameList.SelectedItem as GameRow);

    private void ShowAddForm(GameRow? game, string? savePath = null)
    {
        var form = new AddGameDialog(_config, game?.Title, savePath, game?.Steam != null) { Owner = this };
        if (form.ShowDialog() != true) return;
        Result = form.Result;
        DialogResult = true;
    }
}
