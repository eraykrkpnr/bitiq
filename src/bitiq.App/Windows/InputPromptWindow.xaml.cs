using System.Windows;

namespace Bitiq.App.Windows;

public partial class InputPromptWindow : Window
{
    public string ResponseText => InputBox.Text.Trim();

    public InputPromptWindow(string title, string prompt, string initialText = "")
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Text = prompt;
        InputBox.Text = initialText;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResponseText))
            return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
