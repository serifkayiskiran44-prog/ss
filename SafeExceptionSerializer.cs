namespace TrMarketplaceHubDesktop;

// #784: a global error handler or job-failure log needs enough structure to actually debug from (type,
// message, stack trace, full inner-exception chain) without ever leaking a secret, PII, or a local file
// path. Sanitizes each field independently (not the whole joined report) so AuditStore.Sanitize's per-call
// length cap never truncates a legitimately long stack trace.
public static class SafeExceptionSerializer
{
    public static string Serialize(Exception exception, int maxDepth = 5)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var lines = new List<string>();
        var current = (Exception?)exception;
        for (var depth = 0; current is not null && depth < maxDepth; depth++, current = current.InnerException)
        {
            if (depth > 0) lines.Add($"---- Inner exception {depth} ----");
            lines.Add($"{current.GetType().FullName}: {AuditStore.Sanitize(current.Message)}");
            if (!string.IsNullOrWhiteSpace(current.StackTrace)) lines.Add(AuditStore.Sanitize(current.StackTrace));
        }
        return string.Join(Environment.NewLine, lines);
    }
}
