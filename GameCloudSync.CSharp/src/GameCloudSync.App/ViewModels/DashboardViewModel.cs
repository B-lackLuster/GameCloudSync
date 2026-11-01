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
    private int _knownProblemCount;

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
    public int ConflictCount => _owner.Games.Count(g => g.Status.Kind == SyncStatusKind.Conflict);
    public int NoSavesCount => _owner.Games.Count(g => g.Status.Kind == SyncStatusKind.NoSaves);
    public int NotConfiguredCount => _owner.Games.Count(g => g.Status.Kind == SyncStatusKind.NotConfigured);
    public int UncheckedCount => _owner.Games.Count(g => g.Status.Kind == SyncStatusKind.Unknown);
    public int LogCount => _owner.Logs.Count;
    public bool HasLogs => LogCount > 0;

    /// <summary>Games that can't be backed up as configured, or whose latest backup failed.</summary>
    public int ProblemCount => _owner.Games.Count(g => g.HasProblem);
    public bool HasProblems => ProblemCount > 0;
    public string ProblemTitle => Localization.T("Shell.ProblemTitle", ProblemCount);

    /// <summary>The first few problems in one line each, so the cause is visible without opening the library.</summary>
    public IEnumerable<string> ProblemLines => _owner.Games
        .Where(g => g.HasProblem)
        .OrderByDescending(g => g.HasFailure)
        .Take(3)
        .Select(g => $"{g.Name}: {(g.HasFailure ? g.LastFailure : g.Status.Detail ?? g.Status.Text)}");

    /// <summary>Raised when the number of problem games changes (the tray icon follows it).</summary>
    public event Action? ProblemCountChanged;
    public bool IsOneDriveConfigured => !string.IsNullOrWhiteSpace(_owner.Config.CloudRoot);
    public bool IsWebDavConfigured => !string.IsNullOrWhiteSpace(_owner.Config.WebDavUrl);
    public string OneDriveState => Localization.T(IsOneDriveConfigured ? "Shell.Configured" : "Common.NotConfigured");
    public string WebDavState => Localization.T(IsWebDavConfigured ? "Shell.Configured" : "Common.NotConfigured");
    public string OneDrivePath => IsOneDriveConfigured ? _owner.Config.CloudRoot : Localization.T("Shell.ConfigureHint");
    public string WebDavAddress => IsWebDavConfigured ? _owner.Config.WebDavUrl : Localization.T("Shell.ConfigureHint");
    public string EngineState => Localization.T(_owner.IsMonitoring ? "Shell.Watching" : "Shell.Idle");
    public string SyncCaption => Localization.T("Shell.SyncCaption", SyncedCount, TotalGames);

    /// <summary>
    /// The games that are neither synced nor waiting for a backup or download, each reason on its own:
    /// "no saves yet" and "not set up" need different things from the user than "not checked yet".
    /// </summary>
    public string PausedCaption => string.Join(" · ", CaptionParts());

    private IEnumerable<string> CaptionParts()
    {
        yield return Localization.T("Shell.PausedN", PausedCount);
        if (ConflictCount > 0)
            yield return Localization.T("Shell.ConflictN", ConflictCount);
        if (NotConfiguredCount > 0)
            yield return Localization.T("Shell.NotConfiguredN", NotConfiguredCount);
        if (NoSavesCount > 0)
            yield return Localization.T("Shell.NoSavesN", NoSavesCount);
        if (UncheckedCount > 0)
            yield return Localization.T("Shell.UncheckedN", UncheckedCount);
    }

    public string LibraryCaption => Localization.T("Shell.LibraryCaption", TotalGames);
    public double SyncPercent => TotalGames == 0 ? 0 : SyncedCount * 100.0 / TotalGames;

    /// <summary>Three games for the overview: those that need something done come first, in library order.</summary>
    public IEnumerable<GameItemViewModel> PreviewGames => _owner.Games.OrderBy(AttentionRank).Take(3);

    public IEnumerable<string> RecentLogs => _owner.Logs.Reverse().Take(3);

    private static int AttentionRank(GameItemViewModel game) => game.HasProblem ? 0 : game.Status.Kind switch
    {
        SyncStatusKind.Conflict => 1,
        SyncStatusKind.CloudNew => 2,
        SyncStatusKind.LocalNew => 3,
        SyncStatusKind.NotConfigured => 4,
        SyncStatusKind.Unknown => 5,
        SyncStatusKind.NoSaves => 6,
        SyncStatusKind.Synced => 7,
        _ => 8
    };

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
        if (e.PropertyName is nameof(GameItemViewModel.Status) or nameof(GameItemViewModel.IsPaused)
            or nameof(GameItemViewModel.LastFailure))
            Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(string.Empty);
        var problems = ProblemCount;
        if (problems == _knownProblemCount)
            return;
        _knownProblemCount = problems;
        ProblemCountChanged?.Invoke();
    }
}
