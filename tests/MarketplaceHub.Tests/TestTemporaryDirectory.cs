using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace MarketplaceHub.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "marketplace-tests-" + Guid.NewGuid().ToString("N"));

    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4) { Thread.Sleep(25); }
            catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(25); }
        }
    }
}
