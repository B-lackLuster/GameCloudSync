using System.Collections.Specialized;
using System.ComponentModel;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;

namespace GameCloudSync.App.ViewModels;

public enum WorkspacePage { Overview, Library, Activity }

/// <summary>Presentation-only navigation and live summaries of existing application state.</summary>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly HashSet<GameItemViewModel> _observedGames = new();
    private WorkspacePage _page;

    public DashboardViewModel(MainViewModel owner)
    {
        _owner = owner;
        owner.Games.CollectionChanged += OnGamesChanged;
        owner.Logs.CollectionChanged += (_, _) => Refresh();
        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.CloudStatusText) or nameof(MainViewModel.MonitorStateText)
                or nameof(MainViewModel.IsBusy) or nameof(MainViewModel.GameCountText))
                Refresh();
        };
    }

    public WorkspacePage Page
    {
        get => _page;
        set
        {
            if (!SetProperty(ref _page, value)) return;
            Refresh();
        }
    }

    public bool IsOverview => Page == WorkspacePage.Overview;
    public bool IsLibrary => Page == WorkspacePage.Library;
    public bool IsActivity => Page == WorkspacePage.Activity;
    public string PageTitle => Localization.T(Page switch
    {
        WorkspacePage.Library => "Main.LibraryTitle",
        WorkspacePage.Activity => "Shell.Activity",
        _ => "Shell.Overview"
    });
    public string PageSubtitle => Localization.T(Page switch
    {
        WorkspacePage.Library => "Shell.LibraryHint",
        WorkspacePage.Activity => "Shell.ActivityHint",
        _ => "Shell.OverviewHint"
    });
    public int TotalGames => _owner.Games.Count;
    public bool HasGames => TotalGames > 0;
    public int SyncedCount => _owner.Games.Count(g => g.Status.Kind == SyncStatusKind.Synced);
    public int LocalNewCount => _owner.Games.Count(g => g.Status.Kind == SyncStatusKind.LocalNew);
    public int CloudNewCount => _owner.Games.Count(g => g.Status.Kind == SyncStatusKind.CloudNew);
    public int PausedCount => _owner.Games.Count(g => g.IsPaused);
    public int UncheckedCount => _owner.Games.Count(g => g.Status.Kind is SyncStatusKind.Unknown or SyncStatusKind.None);
    public int LogCount => _owner.Logs.Count;
    public bool HasLogs => LogCount > 0;
    public bool IsOneDriveConfigured => !string.IsNullOrWhiteSpace(_owner.Config.CloudRoot);
    public bool IsWebDavConfigured => !string.IsNullOrWhiteSpace(_owner.Config.WebDavUrl);
    public string OneDriveState => Localization.T(IsOneDriveConfigured ? "Shell.Configured" : "Common.NotConfigured");
    public string WebDavState => Localization.T(IsWebDavConfigured ? "Shell.Configured" : "Common.NotConfigured");
    public string OneDrivePath => IsOneDriveConfigured ? _owner.Config.CloudRoot : Localization.T("Shell.ConfigureHint");
    public string WebDavAddress => IsWebDavConfigured ? _owner.Config.WebDavUrl : Localization.T("Shell.ConfigureHint");
    public string EngineState => Localization.T(_owner.IsMonitoring ? "Shell.Watching" : "Shell.Idle");
    public string SyncCaption => Localization.T("Shell.SyncCaption", SyncedCount, TotalGames);
    public string PausedCaption => Localization.T("Shell.PausedCaption", PausedCount, UncheckedCount);
    public string LibraryCaption => Localization.T("Shell.LibraryCaption", TotalGames);
    public double SyncPercent => TotalGames == 0 ? 0 : SyncedCount * 100.0 / TotalGames;
    public IEnumerable<GameItemViewModel> PreviewGames => _owner.Games.Take(3);
    public IEnumerable<string> RecentLogs => _owner.Logs.Reverse().Take(3);

    private void OnGamesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var removed in _observedGames.Except(_owner.Games).ToList())
        {
            removed.PropertyChanged -= OnGameChanged;
            _observedGames.Remove(removed);
        }
        foreach (var added in _owner.Games.Except(_observedGames))
        {
            added.PropertyChanged += OnGameChanged;
            _observedGames.Add(added);
        }
        Refresh();
    }

    private void OnGameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameItemViewModel.Status) or nameof(GameItemViewModel.IsPaused))
            Refresh();
    }

    private void Refresh() => OnPropertyChanged(string.Empty);
}
