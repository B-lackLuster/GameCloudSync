using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GameCloudSync.App.Services;

/// <summary>
/// The entrance of a page that was switched to: it fades in while it slides up into place, like the pages of
/// Windows' own apps. Nothing moves when animation effects are turned off in Windows.
/// </summary>
public static class PageTransition
{
    private const double Offset = 20;
    private static readonly Duration FadeDuration = TimeSpan.FromMilliseconds(250);
    private static readonly Duration SlideDuration = TimeSpan.FromMilliseconds(400);

    public static void Play(UIElement element)
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;
        var slide = new TranslateTransform(0, Offset);
        element.RenderTransform = slide;
        var fadeIn = new DoubleAnimation(0, 1, FadeDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var slideIn = new DoubleAnimation(Offset, 0, SlideDuration) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } };
        // The page is handed back as it was: text under an animated opacity is drawn without ClearType.
        slideIn.Completed += (_, _) =>
        {
            // A later switch back to this page has started an entrance of its own.
            if (element.RenderTransform != slide)
                return;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.ClearValue(UIElement.RenderTransformProperty);
        };
        element.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        slide.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }
}
