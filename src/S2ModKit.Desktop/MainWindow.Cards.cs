using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace S2ModKit.Desktop;

/// <summary>The mod list as picture cards (the user's picture, else the mod's GameBanana picture), and pictures per mod.</summary>
internal sealed partial class MainWindow
{
    private const double CardWidth = 166;
    private const double CardHeight = 192;                                 // every card the same size: picture, two lines, two lines of marks
    private readonly ToggleButton cardsView = new() { Content = "Cards" };
    private static readonly SemaphoreSlim PictureLoads = new(4);            // a few pictures at a time (one for the app's life)
    private DockPanel? sidePanel;

    private ModPictures Pictures => new(Path.Combine(settings.Data, "mod-pictures"));

    private bool Cards => cardsView.IsChecked == true;

    /// <summary>Switches between the text list and picture cards (remembered beside the window layout).</summary>
    private void SetCards(bool on, bool remember = true)
    {
        cardsView.IsChecked = on;
        list.ItemsPanel = new FuncTemplate<Panel?>(() => on ? new WrapPanel { ItemSpacing = 0, LineSpacing = 0 } : new VirtualizingStackPanel());
        var layout = UiLayout.Load();
        if (sidePanel is not null)
        {
            sidePanel.Width = on ? layout.CardsWidth : layout.ListWidth;
        }

        if (remember)
        {
            layout.ModCards = on;
            layout.Save();
        }
        var source = list.ItemsSource;                                      // the template reads the switch: draw the rows again
        list.ItemsSource = null;
        list.ItemsSource = source;
    }

    /// <summary>A card: the picture (or the mod's first letters on a plain tile), name, heroes and state, and its marks.</summary>
    private Border ModCard(ModEntry mod, DoctorFinding? finding, bool update)
    {
        var initials = new TextBlock
        {
            Text = string.Concat(mod.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(word => char.ToUpperInvariant(word[0]))),
            FontSize = 28,
            FontWeight = FontWeight.SemiBold,
            Foreground = Muted,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var image = new Image { Stretch = Stretch.UniformToFill };
        var picture = new Border
        {
            Height = 100,
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            ClipToBounds = true,
            Background = Panel,
            Child = new Grid { Children = { initials, image } },
        };
        _ = LoadPictureAsync(mod, image, initials);
        var text = ModItem(mod, null, false, finding, update);               // the same name, state and marks as the list
        text.Margin = new Thickness(8, 6, 8, 8);
        foreach (var block in text.Children.OfType<TextBlock>())
        {
            block.TextWrapping = TextWrapping.NoWrap;
        }

        return CardFrame(picture, text);
    }

    private static Border CardFrame(Control picture, Control text) => new()
    {
        Width = CardWidth,
        Height = CardHeight,
        Margin = new Thickness(4),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        BorderBrush = AppTheme.Outline,
        ClipToBounds = true,
        Child = new StackPanel { Children = { picture, text } },
    };

    /// <summary>The hero's base game among the cards: the same size as a mod card.</summary>
    private static Border BaseCard(Hero hero)
    {
        var picture = new Border
        {
            Height = 100,
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            Background = Panel,
            Child = new TextBlock { Text = "Base game", Foreground = Muted, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var text = new StackPanel
        {
            Margin = new Thickness(8, 6, 8, 8),
            Children =
            {
                new TextBlock { Text = $"{hero.Name} (base game)", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = "The game's own model", Foreground = Muted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        return CardFrame(picture, text);
    }

    /// <summary>The width a header takes among the cards: the list's width less its scroll bar and margins.</summary>
    private double CardsRowWidth => Math.Max(CardWidth, (sidePanel?.Width ?? UiLayout.MinListWidth) - 40);

    /// <summary>The handle on the mod list's right edge: drag to make the list wider or narrower (remembered per view).</summary>
    private Border ListGrip(Control side, Control root)
    {
        var grip = new Border { Width = 5, Background = AppTheme.Background, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast) };
        ToolTip.SetTip(grip, "Drag to make the mod list wider or narrower");
        (double X, double Width)? dragging = null;
        grip.PointerPressed += (_, e) =>
        {
            dragging = (e.GetPosition(root).X, side.Bounds.Width);
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (dragging is { } start)
            {
                side.Width = Math.Clamp(start.Width + (e.GetPosition(root).X - start.X), UiLayout.MinListWidth, UiLayout.MaxListWidth);
            }
        };
        grip.PointerReleased += (_, e) =>
        {
            if (dragging is null)
            {
                return;
            }

            dragging = null;
            e.Pointer.Capture(null);
            var layout = UiLayout.Load();
            if (Cards)
            {
                layout.CardsWidth = side.Width;
                var source = list.ItemsSource;                              // headers take the new row width
                list.ItemsSource = null;
                list.ItemsSource = source;
            }
            else
            {
                layout.ListWidth = side.Width;
            }

            layout.Save();
        };
        return grip;
    }

    private async Task LoadPictureAsync(ModEntry mod, Image image, TextBlock initials)
    {
        await PictureLoads.WaitAsync();
        try
        {
            byte[]? bytes = null;
            if (Pictures.UserPicture(ModProfiles.IdentityOf(mod)) is { } own)
            {
                bytes = await File.ReadAllBytesAsync(own);
            }
            else if (GameBananaIdOf(mod) is { } id)
            {
                gameBanana ??= new GameBananaClient(Path.Combine(settings.Data, "gamebanana-cache"));
                var url = Pictures.Thumbnail(id);
                if (url is null && (await gameBanana.PageAsync(id)).Images is { Count: > 0 } images && images[0] is { } first)
                {
                    url = first;
                    Pictures.RememberThumbnail(id, url);
                }

                bytes = url is null ? null : await gameBanana.ImageAsync(url);
            }

            if (bytes is not null)
            {
                image.Source = new Bitmap(new MemoryStream(bytes));
                initials.IsVisible = false;
            }
        }
#pragma warning disable CA1031 // a card without a picture keeps its plain tile; the reason goes to the log
        catch (Exception exception)
#pragma warning restore CA1031
        {
            AppLog.Error("Mod picture", exception);
        }
        finally
        {
            PictureLoads.Release();
        }
    }

    /// <summary>More > Set picture...: a picture of the user's own for the card (copied into the data folder).</summary>
    private async Task SetPictureAsync(ModEntry mod)
    {
        var picked = await PickPictureAsync();
        if (picked is null)
        {
            return;
        }

        try
        {
            Pictures.SetUserPicture(ModProfiles.IdentityOf(mod), picked);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            listStatus.Text = "Could not use the picture: " + exception.Message;
            return;
        }

        await LoadAsync(mod.Key);
        listStatus.Text = $"'{mod.Name}' has its picture.";
    }

    private async Task RemovePictureAsync(ModEntry mod)
    {
        Pictures.SetUserPicture(ModProfiles.IdentityOf(mod), null);
        await LoadAsync(mod.Key);
    }

    private async Task<string?> PickPictureAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a picture",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}

/// <summary>Before an install from a file or archive: the mod's name (the file's by default) and an optional picture.</summary>
internal sealed class InstallDetailsDialog : Window
{
    public InstallDetailsDialog(string fileName, Func<Task<string?>> pickPicture)
    {
        Title = "Install a mod";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var name = new TextBox { Text = Path.GetFileNameWithoutExtension(fileName), PlaceholderText = "Name" };
        string? picture = null;
        var preview = new Image { Stretch = Stretch.UniformToFill };
        var previewBox = new Border { Width = 176, Height = 100, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = AppTheme.Main, Child = preview };
        var choose = new Button { Content = "Choose a picture..." };
        var clear = new Button { Content = "No picture", IsVisible = false };
        choose.Click += async (_, _) =>
        {
            if (await pickPicture() is not { } path)
            {
                return;
            }

            try
            {
                preview.Source = new Bitmap(path);
                picture = path;
                clear.IsVisible = true;
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                AppLog.Error("Install picture", exception);
            }
        };
        clear.Click += (_, _) =>
        {
            picture = null;
            preview.Source = null;
            clear.IsVisible = false;
        };
        var install = new Button { Content = "Install", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        install.Click += (_, _) => Close(new InstallDetails(string.IsNullOrWhiteSpace(name.Text) ? Path.GetFileNameWithoutExtension(fileName) : name.Text.Trim(), picture));
        cancel.Click += (_, _) => Close(null);
        Opened += (_, _) => name.Focus();
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = Path.GetFileName(fileName), FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = "Name in S2ModKit", Foreground = AppTheme.Muted },
                name,
                new TextBlock { Text = "Picture for the card view (optional)", Foreground = AppTheme.Muted },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { previewBox, new StackPanel { Spacing = 6, Children = { choose, clear } } } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { install, cancel } },
            },
        };
    }
}

internal sealed record InstallDetails(string Name, string? Picture);
