using System.Windows.Media;
using GameCloudSync.Core.Models;

namespace GameCloudSync.App.ViewModels;

public sealed class GameItemViewModel : ObservableObject
{
    private SyncStatus _status = new("…", SyncStatusKind.Unknown);

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
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(StatusBgBrush));
            }
        }
    }

    public string StatusText => Status.Text;

    public Brush StatusBrush => Status.Kind switch
    {
        SyncStatusKind.Synced => Brush(Solid("#10B981")),
        SyncStatusKind.LocalNew => Brush(Solid("#EF4444")),
        SyncStatusKind.CloudNew => Brush(Solid("#4A6CF7")),
        SyncStatusKind.Paused => Brush(Solid("#94A3B8")),
        _ => Brush(Solid("#64748B"))
    };

    public Brush StatusBgBrush => Status.Kind switch
    {
        SyncStatusKind.Synced => Brush(Solid("#D1FAE5")),
        SyncStatusKind.LocalNew => Brush(Solid("#FEE2E2")),
        SyncStatusKind.CloudNew => Brush(Solid("#EBF0FF")),
        SyncStatusKind.Paused => Brush(Solid("#F1F5F9")),
        _ => Brush(Solid("#F1F5F9"))
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

    private static Brush Brush(Brush brush) => brush;

    private static SolidColorBrush Solid(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
