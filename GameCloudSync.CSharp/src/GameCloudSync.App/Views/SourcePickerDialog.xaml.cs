using System.Windows;

namespace GameCloudSync.App.Views;

public partial class SourcePickerDialog : Window
{
    public SourcePickerDialog(List<string>? options)
    {
        InitializeComponent();
        DataContext = this;
        Options = options ??
                  new List<string> { "OneDrive 优先", "WebDAV 优先" };
        PromptText = options is null
            ? "批量下载：请选择优先使用的备份来源"
            : "该游戏有多个备份来源，请选择：";
    }

    public List<string> Options { get; }
    public string PromptText { get; }
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
