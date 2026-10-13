using ServiceLib.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// 0.5 tray popover (left click on the menu-bar icon): a small window under the icon with the
/// state, a big toggle, the country, live speed, the subscription and Open / Quit. Closes
/// itself when it loses focus or on Esc. Placement and lifetime are driven by TermitusTray.
/// </summary>
public partial class TermitusPopover : Window
{
    public event Action? OpenMainRequested;
    public event Action? QuitRequested;
    public event Action? Hidden;

    public TermitusPopover()
    {
        InitializeComponent();
        if (OperatingSystem.IsMacOS())
        {
            FontFamily = new FontFamily("Helvetica Neue"); // same face as the main window (see TermitusHomeView)
        }
        btnOpen.Click += (_, _) => OpenMainRequested?.Invoke();
        btnQuit.Click += (_, _) => QuitRequested?.Invoke();
        btnRenew.Click += (_, _) =>
        {
            OpenMainRequested?.Invoke();
            (DataContext as TermitusHomeViewModel)?.RenewCmd.Execute().Subscribe(_ => { }, _ => { });
        };
        btnCountry.Click += (_, _) =>
        {
            countryList.IsVisible = !countryList.IsVisible;
            countryChevron.Data = Geometry.Parse(countryList.IsVisible ? "M1 5 L5 1 L9 5" : "M1 1 L5 5 L9 1");
        };
        Deactivated += (_, _) => HideSelf();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                HideSelf();
            }
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && !IsVisible)
            {
                Hidden?.Invoke();
            }
        };
        // Picking a country closes the list again (the choice shows in the row above).
        countryList.AddHandler(Button.ClickEvent, (_, _) =>
        {
            countryList.IsVisible = false;
            countryChevron.Data = Geometry.Parse("M1 1 L5 5 L9 1");
        });
    }

    private void HideSelf()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>Centre the popover under the icon (DIPs, Avalonia screen coordinates, origin top-left).</summary>
    public void PlaceUnder(double centerX, double topY)
    {
        var scale = Screens.Primary?.Scaling ?? 1.0;
        var x = (centerX - Width / 2) * scale;
        var y = (topY - 4) * scale; // the content has a 4 dip top margin
        var area = Screens.ScreenFromPoint(new PixelPoint((int)(centerX * scale), (int)(topY * scale)))?.WorkingArea;
        if (area is { } a)
        {
            x = Math.Clamp(x, a.X, Math.Max(a.X, a.Right - Width * scale));
        }
        Position = new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
    }
}
