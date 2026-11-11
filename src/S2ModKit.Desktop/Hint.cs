using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace S2ModKit.Desktop;

/// <summary>
/// A small info icon beside a label that holds a longer explanation: one card opens on hover (after a short pause) and stays open on
/// click until the pointer leaves, a click elsewhere or Esc. It replaces the long grey notes, which were too small to read and took the
/// panel's room. The card is a plain popup (no tooltip frame around it).
/// </summary>
internal sealed class Hint : Panel
{
    private static readonly IBrush Idle = AppTheme.Outline;
    private static readonly IBrush Active = AppTheme.AccentBrush;
    private static readonly IBrush CardBackground = AppTheme.Background;
    private static readonly IBrush CardBorder = AppTheme.Outline;
    private static readonly IBrush CardText = AppTheme.Text;
    private readonly Popup popup;
    private readonly TextBlock body = new() { FontSize = 13.5, LineHeight = 20, TextWrapping = TextWrapping.Wrap, Foreground = CardText };
    private readonly Border badge = new()
    {
        Width = 17,
        Height = 17,
        CornerRadius = new CornerRadius(8.5),
        Background = Idle,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = "i",
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
        },
    };
    private readonly DispatcherTimer delay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool pinned;
    private bool overCard;

    public Hint(string title, string text)
    {
        Title = title;
        Width = 20;
        Height = 20;
        VerticalAlignment = VerticalAlignment.Center;
        Background = Brushes.Transparent;                                   // the whole square takes the pointer
        Cursor = new Cursor(StandardCursorType.Help);
        Focusable = true;
        AutomationProperties.SetName(this, "About " + title);
        body.Text = text;

        Children.Add(badge);

        var card = new Border
        {
            MaxWidth = 340,
            Padding = new Thickness(14, 12),
            CornerRadius = new CornerRadius(10),
            Background = CardBackground,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 8 24 0 #66000000"),
            Margin = new Thickness(0, 4, 16, 16),                           // room for the shadow
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14, Foreground = CardText }, body },
            },
        };
        card.PointerEntered += (_, _) => overCard = true;
        card.PointerExited += (_, _) =>
        {
            overCard = false;
            CloseSoon();
        };
        popup = new Popup
        {
            Child = card,
            PlacementTarget = this,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = false,                                  // set only for a clicked card: on hover it made the icon lose the pointer, and the card blinked
        };
        popup.Closed += (_, _) =>
        {
            pinned = false;
            Paint(false);
        };
        Children.Add(popup);

        delay.Tick += (_, _) =>
        {
            delay.Stop();
            Open();
        };
        PointerEntered += (_, _) =>
        {
            Paint(true);
            delay.Start();
        };
        PointerExited += (_, _) =>
        {
            delay.Stop();
            CloseSoon();
        };
        PointerPressed += (_, e) =>
        {
            pinned = !popup.IsOpen || !pinned;
            if (pinned)
            {
                popup.IsOpen = false;                                       // reopen as a clicked card: a click elsewhere closes it
                Open();
            }
            else
            {
                popup.IsOpen = false;
            }

            e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                popup.IsOpen = false;
                e.Handled = true;
            }
        };
    }

    public string Title { get; }

    /// <summary>The explanation; it may change with the selected part.</summary>
    public string Text
    {
        get => body.Text ?? string.Empty;
        set => body.Text = value;
    }

    /// <summary>For the window's screenshot mode: opens the card as a click would.</summary>
    public void OpenForShot()
    {
        pinned = true;
        Open();
    }

    private void Open()
    {
        popup.IsLightDismissEnabled = pinned;
        popup.IsOpen = true;
        Paint(true);
        Focus();                                                            // so Esc reaches it
    }

    /// <summary>Closes a hover card once the pointer is on neither the icon nor the card (a pinned card stays).</summary>
    private void CloseSoon() => DispatcherTimer.RunOnce(() =>
    {
        if (!pinned && !overCard && !IsPointerOver)
        {
            popup.IsOpen = false;
            Paint(false);
        }
    }, TimeSpan.FromMilliseconds(150));

    private void Paint(bool active) => badge.Background = active ? Active : Idle;

    /// <summary>A label with its hint beside it.</summary>
    public static StackPanel Beside(Control label, Hint hint) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Children = { label, hint },
    };
}
