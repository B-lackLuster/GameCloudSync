using System.Windows;
using GameCloudSync.Core.Services;
using Localization = GameCloudSync.Core.Services.Localization;

namespace GameCloudSync.App.Views;

public partial class SourcePickerDialog : Window
{
    public SourcePickerDialog(List<string>? options)
    {
        InitializeComponent();
        DataContext = this;
        Options = options ??
                  new List<string> { Localization.T("Pick.OneDriveFirst"), Localization.T("Pick.WebDavFirst") };
        PromptText = options is null
            ? Localization.T("Pick.BatchPrompt")
            : Localization.T("Pick.SinglePrompt");
        TitleText = Localization.T("Pick.Title");
        CancelText = Localization.T("Common.Cancel");
    }

    public List<string> Options { get; }
    public string PromptText { get; }
    public string TitleText { get; }
    public string CancelText { get; }
    public string? Result { get; private set; }
    public int SelectedIndex { get; private set; } = -1;

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Button)sender;
        Result = (string)button.Content;
        SelectedIndex = Options.IndexOf(Result);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
