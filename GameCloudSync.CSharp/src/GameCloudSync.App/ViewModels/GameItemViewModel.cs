using System.Windows.Media;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Util;
using Localization = GameCloudSync.Core.Services.Localization;

namespace GameCloudSync.App.ViewModels;

public sealed class GameItemViewModel : ObservableObject
{
    private SyncStatus _status = new("…", SyncStatusKind.Unknown);
    private string? _lastFailure;

    public GameItemViewModel(GameConfig game)
    {
        Game = game;
    }

    public GameConfig Game { get; }

    public string Name => Game.Name;
    public string SavePath => Game.Save;
    public string MethodsDisplay => Game.MethodsDisplay;
    public bool IsPaused => Game.Paused;
    public bool IsOneDrive => Game.HasMethod(Core.Constants.MethodOneDrive);
    public bool IsWebDav => Game.HasMethod(Core.Constants.MethodWebDav);

    public SyncStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusTooltip));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(StatusBgBrush));
                OnPropertyChanged(nameof(HasProblem));
                RefreshDetail();
            }
        }
    }

    public string StatusText => Status.Text;

    /// <summary>The full explanation when the badge only has room for a short label.</summary>
    public string StatusTooltip => Status.Detail ?? Status.Text;

    /// <summary>Why the latest backup attempt failed, until an attempt succeeds.</summary>
    public string? LastFailure => _lastFailure;

    public bool HasFailure => !string.IsNullOrEmpty(_lastFailure);

    /// <summary>The game needs the user's attention: it can't be backed up, or its last backup failed.</summary>
    public bool HasProblem => HasFailure || Status.Kind == SyncStatusKind.Error;

    public void SetLastFailure(string? message)
    {
        var normalized = string.IsNullOrEmpty(message) ? null : message;
        if (_lastFailure == normalized)
            return;
        _lastFailure = normalized;
        OnPropertyChanged(nameof(LastFailure));
        OnPropertyChanged(nameof(HasFailure));
        OnPropertyChanged(nameof(HasProblem));
        RefreshDetail();
    }

    /// <summary>Second line of the game's card: the failure, else when the newest backup was made.</summary>
    public string DetailText => HasFailure
        ? Localization.T("Card.BackupFailed", _lastFailure)
        : Status.LastBackup is { } lastBackup
            ? Localization.T("Card.LastBackup", RelativeTime.Format(lastBackup, DateTime.Now))
            : "";

    public bool HasDetail => DetailText.Length > 0;

    public Brush DetailBrush => HasFailure ? Brush(Solid("#F18B91")) : Brush(Solid("#839B9E"));

    public Brush StatusBrush => Status.Kind switch
    {
        SyncStatusKind.Synced => Brush(Solid("#6BE0B4")),
        SyncStatusKind.LocalNew => Brush(Solid("#F1CB8D")),
        SyncStatusKind.CloudNew => Brush(Solid("#9AD7F1")),
        SyncStatusKind.Paused => Brush(Solid("#AFBBBD")),
        SyncStatusKind.Error => Brush(Solid("#F8ADB1")),
        _ => Brush(Solid("#ACBABB"))
    };

    public Brush StatusBgBrush => Status.Kind switch
    {
        SyncStatusKind.Synced => Brush(Solid("#213C33")),
        SyncStatusKind.LocalNew => Brush(Solid("#443B2C")),
        SyncStatusKind.CloudNew => Brush(Solid("#253E47")),
        SyncStatusKind.Paused => Brush(Solid("#303739")),
        SyncStatusKind.Error => Brush(Solid("#4A2A2E")),
        _ => Brush(Solid("#303739"))
    };

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(SavePath));
        OnPropertyChanged(nameof(MethodsDisplay));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsOneDrive));
        OnPropertyChanged(nameof(IsWebDav));
    }

    /// <summary>"5 min ago" goes stale as time passes; call this now and then to bring it up to date.</summary>
    public void RefreshDetail()
    {
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailBrush));
    }

    private static Brush Brush(Brush brush) => brush;

    private static SolidColorBrush Solid(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
