namespace BwPicker;

static class AppIcon
{
    public static Icon Window { get; } = Load(new Size(32, 32));
    public static Icon Tray { get; } = Load(SystemInformation.SmallIconSize);

    static Icon Load(Size size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("BwPicker.AppIcon.ico")
            ?? throw new InvalidOperationException("The application icon is missing.");
        using var icon = new Icon(stream, size);
        return (Icon)icon.Clone();
    }
}
