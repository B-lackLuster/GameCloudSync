using System.Windows;
using System.Windows.Controls;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Util;

namespace GameCloudSync.App.Views;

/// <summary>Checks a game name as it is typed and says what is wrong with it under the name box.</summary>
internal static class GameNameFeedback
{
    /// <param name="self">The game being edited, which may keep its own name.</param>
    public static void Show(TextBlock target, string typed, IEnumerable<GameConfig> games, GameConfig? self = null)
    {
        var name = typed.Trim();
        // An empty box is where every name starts, not a mistake; saving still refuses it.
        var error = name.Length == 0 ? GameNameError.None : GameNames.Validate(name, games, self);
        target.Text = error == GameNameError.None ? "" : GameNames.Describe(error, name);
        target.Visibility = target.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
