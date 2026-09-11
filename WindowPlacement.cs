namespace TrMarketplaceHubDesktop;

public readonly record struct WindowRect(double Left, double Top, double Width, double Height);

public static class WindowPlacement
{
    public static WindowRect Restore(WindowRect saved, WindowRect workArea)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0) throw new ArgumentOutOfRangeException(nameof(workArea));
        var width = double.IsFinite(saved.Width) ? Math.Clamp(saved.Width, 480, workArea.Width) : Math.Min(960, workArea.Width);
        var height = double.IsFinite(saved.Height) ? Math.Clamp(saved.Height, 320, workArea.Height) : Math.Min(700, workArea.Height);
        var left = double.IsFinite(saved.Left) ? saved.Left : workArea.Left;
        var top = double.IsFinite(saved.Top) ? saved.Top : workArea.Top;
        return new(Math.Clamp(left, workArea.Left, workArea.Left + workArea.Width - width), Math.Clamp(top, workArea.Top, workArea.Top + workArea.Height - height), width, height);
    }
}
