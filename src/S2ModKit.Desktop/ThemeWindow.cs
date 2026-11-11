using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace S2ModKit.Desktop;

/// <summary>
/// The theme editor: a ready-made theme to start from, and every colour of the app beside a colour picker. Changes show at once in
/// every window and are kept when the window closes.
/// </summary>
internal sealed class ThemeWindow : Window
{
    private readonly ComboBox themes = new() { MinWidth = 180 };
    private readonly StackPanel roles = new() { Spacing = 2 };
    private readonly ColorPicker picker = new();
    private readonly TextBlock roleName = new() { FontSize = 15, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock roleUse = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Muted };
    private readonly Button systemAccent = new() { Content = "Use the system accent" };
    private readonly TextBlock changed = new() { FontSize = 13, Foreground = AppTheme.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, Button> roleButtons = [];
    private ThemeRole role = AppTheme.Roles[0];
    private bool showing;

    public ThemeWindow()
    {
        Title = "Theme";
        Width = 700;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        themes.ItemsSource = AppTheme.Themes.Select(theme => theme.Name).ToArray();
        themes.SelectedItem = AppTheme.Themes.Any(theme => theme.Name == AppTheme.Name) ? AppTheme.Name : AppTheme.DefaultName;
        themes.SelectionChanged += (_, _) =>
        {
            if (!showing && themes.SelectedItem is string name)
            {
                AppTheme.Use(name);
                AppTheme.Save();
                ShowRole(role);
            }
        };

        foreach (var item in AppTheme.Roles)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = AppTheme.Outline,
                Background = AppTheme.Brush(item.Key),
            };
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                Padding = new Thickness(8, 5),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { swatch, new TextBlock { Text = item.Name, VerticalAlignment = VerticalAlignment.Center } } },
            };
            button.Click += (_, _) => ShowRole(item);
            roleButtons[item.Key] = button;
            roles.Children.Add(button);
        }

        picker.ColorChanged += color =>
        {
            if (!showing)
            {
                AppTheme.Set(role.Key, color);
                ShowChanged();
            }
        };
        systemAccent.Click += (_, _) =>
        {
            AppTheme.Set(AppTheme.Accent, null);
            ShowRole(role);
        };
        var undo = new Button { Content = "Undo my changes" };
        undo.Click += (_, _) =>
        {
            AppTheme.Use(AppTheme.Name);
            AppTheme.Save();
            ShowRole(role);
        };
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        Closed += (_, _) => AppTheme.Save();

        var right = new StackPanel { Spacing = 8, Children = { roleName, roleUse, picker, systemAccent } };
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*"), ColumnSpacing = 18 };
        columns.Children.Add(new Border { BorderBrush = AppTheme.Outline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4), Child = roles });
        Grid.SetColumn(right, 1);
        columns.Children.Add(right);
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { new TextBlock { Text = "Start from", VerticalAlignment = VerticalAlignment.Center }, themes, changed },
                },
                columns,
                new TextBlock
                {
                    Text = "Changes show at once and are kept. Picking a theme above starts again from its colours.",
                    Foreground = AppTheme.Muted,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                },
                new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { undo, WithColumn(close, 1) } },
            },
        };
        ShowRole(role);
    }

    private static Control WithColumn(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private void ShowRole(ThemeRole shown)
    {
        role = shown;
        showing = true;
        try
        {
            themes.SelectedItem = AppTheme.Name;
            roleName.Text = shown.Name;
            roleUse.Text = shown.Use;
            picker.Show(AppTheme.Current(shown.Key));
            systemAccent.IsVisible = shown.Key == AppTheme.Accent;
            foreach (var (key, button) in roleButtons)
            {
                button.Background = key == shown.Key ? AppTheme.Header : Brushes.Transparent;
            }

            ShowChanged();
        }
        finally
        {
            showing = false;
        }
    }

    private void ShowChanged() => changed.Text = AppTheme.Changed ? "(with your changes)" : string.Empty;
}
