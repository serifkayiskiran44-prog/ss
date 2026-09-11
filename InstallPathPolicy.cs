namespace TrMarketplaceHubDesktop;

/// <summary>Validates installer-selected locations without touching user data.</summary>
public static class InstallPathPolicy
{
    public static string Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Kurulum klasörü zorunlu.", nameof(path));
        var full = Path.GetFullPath(path.Trim());
        if (full.Length > 240 || full.IndexOfAny(Path.GetInvalidPathChars()) >= 0) throw new ArgumentException("Kurulum klasörü geçersiz.", nameof(path));
        if (full.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) ||
            full.Equals(Environment.GetFolderPath(Environment.SpecialFolder.System), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Windows sistem klasörüne kurulum yapılamaz.");
        return full.TrimEnd(Path.DirectorySeparatorChar);
    }
}
