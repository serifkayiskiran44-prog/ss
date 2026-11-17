using System.IO;
using System.Security.Cryptography;

namespace TrMarketplaceHubDesktop;

internal static class BoundedCredentialFile
{
    internal static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan);
        return ReadBounded(stream, maximumBytes);
    }

    internal static byte[] ReadBounded(Stream stream, int maximumBytes)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("Credential stream must be readable and seekable.", nameof(stream));
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var observedLength = stream.Length;
        if (observedLength is <= 0 || observedLength > maximumBytes) throw new InvalidDataException("Credential file size is invalid.");
        var buffer = new byte[checked((int)observedLength + 1)];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer.AsSpan(total));
            if (read == 0) break;
            total += read;
        }

        if (total != observedLength || stream.Length != observedLength)
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw new InvalidDataException("Credential file changed while it was being read.");
        }

        if (total == buffer.Length) return buffer;
        var result = buffer.AsSpan(0, total).ToArray();
        CryptographicOperations.ZeroMemory(buffer);
        return result;
    }
}
