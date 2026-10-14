using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sample.Avalonia.Views;

/// <summary>
/// A minimal modal password prompt built from plain Avalonia controls (no extra packages), so it works
/// on the desktop head without pulling in a dialog library. It returns the entered password, or
/// <c>null</c> when the user cancels.
/// </summary>
public sealed class PasswordPromptWindow : Window
{
    private readonly TextBox _input;

    public PasswordPromptWindow(string documentName)
    {
        Title = "Password required";
        Width = 380;
        Height = 190;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _input = new TextBox
        {
            Watermark = "Enter password",
            Margin = new Thickness(0, 8, 0, 14),
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Submit();
            }
        };

        Button ok = new() { Content = "OK", MinWidth = 84 };
        ok.Click += (_, _) => Submit();

        Button cancel = new() { Content = "Cancel", MinWidth = 84 };
        cancel.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock
                {
                    Text = $"\"{documentName}\" is password protected.",
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock { Text = "Enter the document password to open it." },
                _input,
                buttons,
            },
        };

        Opened += (_, _) => _input.Focus();
    }

    private void Submit() => Close(_input.Text);
}
