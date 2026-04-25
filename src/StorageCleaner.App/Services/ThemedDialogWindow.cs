using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StorageCleaner.App.Services;

internal enum ThemedDialogButtons
{
    Ok,
    YesNo,
    TypedConfirm
}

internal sealed class ThemedDialogWindow : Window
{
    public ThemedDialogWindow(
        string title,
        string message,
        ThemedDialogButtons buttons,
        bool warning,
        string? expectedText = null)
    {
        Title = title;
        Width = 520;
        MinWidth = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        WindowStyle = WindowStyle.SingleBorderWindow;

        var outerBorder = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1)
        };
        outerBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        outerBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var caption = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Text = warning ? $"Warning: {title}" : title,
            TextWrapping = TextWrapping.Wrap
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, warning ? "DangerBrush" : "StrongTextBrush");

        var body = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            Text = message,
            TextWrapping = TextWrapping.Wrap
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var typedPrompt = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        typedPrompt.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");

        var typedInput = new TextBox
        {
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };

        var buttonHost = new StackPanel
        {
            Margin = new Thickness(0, 18, 0, 0),
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        if (buttons == ThemedDialogButtons.YesNo)
        {
            var noButton = CreateButton("No", (_, _) =>
            {
                DialogResult = false;
                Close();
            });

            var yesButton = CreateButton("Yes", (_, _) =>
            {
                DialogResult = true;
                Close();
            });

            buttonHost.Children.Add(noButton);
            buttonHost.Children.Add(yesButton);
        }
        else if (buttons == ThemedDialogButtons.Ok)
        {
            var okButton = CreateButton("OK", (_, _) =>
            {
                DialogResult = true;
                Close();
            });

            buttonHost.Children.Add(okButton);
        }
        else
        {
            var target = string.IsNullOrWhiteSpace(expectedText) ? "DELETE" : expectedText.Trim();
            typedPrompt.Text = $"Type '{target}' to continue.";
            typedPrompt.Visibility = Visibility.Visible;
            typedInput.Visibility = Visibility.Visible;

            var cancelButton = CreateButton("Cancel", (_, _) =>
            {
                DialogResult = false;
                Close();
            });

            var confirmButton = CreateButton("Confirm", (_, _) =>
            {
                DialogResult = true;
                Close();
            });
            confirmButton.IsEnabled = false;

            typedInput.TextChanged += (_, _) =>
            {
                var isMatch = string.Equals(
                    typedInput.Text.Trim(),
                    target,
                    StringComparison.OrdinalIgnoreCase);
                confirmButton.IsEnabled = isMatch;
            };

            buttonHost.Children.Add(cancelButton);
            buttonHost.Children.Add(confirmButton);
        }

        Grid.SetRow(caption, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(typedPrompt, 2);
        Grid.SetRow(typedInput, 3);
        Grid.SetRow(buttonHost, 4);

        layout.Children.Add(caption);
        layout.Children.Add(body);
        layout.Children.Add(typedPrompt);
        layout.Children.Add(typedInput);
        layout.Children.Add(buttonHost);
        outerBorder.Child = layout;

        Content = outerBorder;
    }

    private static Button CreateButton(string text, RoutedEventHandler clickHandler)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 94,
            Margin = new Thickness(8, 0, 0, 0)
        };
        button.Click += clickHandler;
        return button;
    }
}
