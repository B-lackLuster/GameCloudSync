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
        SyncStatusKind.Synced => Brush(Solid("#6BE0B4")),
        SyncStatusKind.LocalNew => Brush(Solid("#F1CB8D")),
        SyncStatusKind.CloudNew => Brush(Solid("#9AD7F1")),
        SyncStatusKind.Paused => Brush(Solid("#AFBBBD")),
        _ => Brush(Solid("#ACBABB"))
    };

    public Brush StatusBgBrush => Status.Kind switch
    {
        SyncStatusKind.Synced => Brush(Solid("#213C33")),
        SyncStatusKind.LocalNew => Brush(Solid("#443B2C")),
        SyncStatusKind.CloudNew => Brush(Solid("#253E47")),
        SyncStatusKind.Paused => Brush(Solid("#303739")),
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

    private static Brush Brush(Brush brush) => brush;

    private static SolidColorBrush Solid(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
