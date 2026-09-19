using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Etsy;
using TrMarketplaceHubDesktop.Trendyol;

namespace TrMarketplaceHubDesktop;

public enum MultiStoreMigrationCheckpoint
{
    None,
    BackupCreated,
    SourceBindingsApplied,
    InventoryBalancesApplied,
    ConnectionsApplied,
    ProductBindingsApplied,
    Verified,
    Completed
}

public sealed record MultiStoreMigrationDryRun(
    int Version,
    string DataDirectory,
    string DataFingerprint,
    string ConfirmationToken,
    int SourceBindings,
    int OnlineBalances,
    int PhysicalBalances,
    int Connections,
    int ProductBindings,
    string ProductBindingIdentityHash,
    int Orders,
    int AutomationSettings);

public sealed record MultiStoreMigrationReceipt(
    int Version,
    string ReceiptId,
    MultiStoreMigrationCheckpoint Checkpoint,
    string BackupPath,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    MultiStoreMigrationDryRun Counts);

public sealed record MultiStoreMigrationRollbackReceipt(string ReceiptId, string BackupPath, DateTime RolledBackUtc, string ReceiptPath);

/// <summary>
/// Coordinates the already-atomic Task 1-4 local migrations behind one durable,
/// restart-safe checkpoint. This service has no HTTP dependency and never deletes
/// legacy credential/workspace files.
/// </summary>
public sealed class MultiStoreMigrationService
{
    public const int CurrentVersion = 1;
    public const string BackupFormat = DataBackupService.Format;
    const string StateFileName = "multi-store-migration-v1.json";
    const int MaxStateBytes = 1024 * 1024;
    readonly string directory;
    readonly string statePath;
    readonly Action<MultiStoreMigrationCheckpoint>? afterCheckpoint;
    readonly Action<MarketplaceConnectionMigrationResult>? afterConnectionResult;
    readonly Action<MultiStoreMigrationCheckpoint>? afterStageWrite;
    readonly Action<DataRestoreCheckpoint>? afterRestoreCheckpoint;

    sealed record State(
        int Version,
        string ReceiptId,
        MultiStoreMigrationCheckpoint Checkpoint,
        string InitialFingerprint,
        string CheckpointFingerprint,
        string BackupPath,
        string BackupSha256,
        DateTime StartedUtc,
        DateTime? CompletedUtc,
        MultiStoreMigrationDryRun? Counts,
        Dictionary<string, string> ProtectedLegacyFiles,
        MultiStoreMigrationCheckpoint? StageInProgress = null,
        bool ConnectionImportInProgress = false);

    public MultiStoreMigrationService(
        string? dataDirectory = null,
        Action<MultiStoreMigrationCheckpoint>? afterCheckpoint = null,
        Action<MarketplaceConnectionMigrationResult>? afterConnectionResult = null,
        Action<MultiStoreMigrationCheckpoint>? afterStageWrite = null,
        Action<DataRestoreCheckpoint>? afterRestoreCheckpoint = null)
    {
        directory = Path.GetFullPath(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"));
        statePath = Path.Combine(directory, StateFileName);
        this.afterCheckpoint = afterCheckpoint;
        this.afterConnectionResult = afterConnectionResult;
        this.afterStageWrite = afterStageWrite;
        this.afterRestoreCheckpoint = afterRestoreCheckpoint;
    }

    public MultiStoreMigrationDryRun DryRun()
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Göç veri klasörü bulunamadı.");
        return BuildPlan().Report;
    }

    public MultiStoreMigrationDryRun? StartupReport()
    {
        _ = new DataBackupService(directory).RecoverInterruptedRestore();
        if (!Directory.Exists(directory)) return null;
        var state = ReadState();
        if (state is not null)
        {
            ValidateBackup(state);
            if (state.Checkpoint == MultiStoreMigrationCheckpoint.Completed) return null;
            if (PendingStage(state) is not null)
            {
                try { RestoreApprovedBackup(state); }
                catch (Exception error) when (error is not OutOfMemoryException and not DataRestoreAbruptInterruptionException)
                {
                    throw new InvalidOperationException(
                        "Yarım kalan göç aşaması otomatik geri alınamadı; doğrulanmış yedekten geri dönüş veya onarım gerekli.", error);
                }
                var recovered = BuildPlan();
                return recovered.RequiresMigration ? recovered.Report : null;
            }
            ValidatePendingState(state);
            return state.Counts ?? throw new InvalidDataException("Göç checkpoint onay özeti eksik; yedekten geri dönüş veya onarım gerekli.");
        }
        var plan = BuildPlan();
        return plan.RequiresMigration ? plan.Report : null;
    }

    sealed record PlannedConnection(string Id, string Channel, string Shop, bool Enabled, bool ProfileIdentityVerified, bool RequiresImport);
    sealed record Plan(MultiStoreMigrationDryRun Report, bool RequiresMigration);

    Plan BuildPlan()
    {
        EnsureNoPendingWal();
        var initialFingerprint = Fingerprint();
        var catalogPath = Path.Combine(directory, "catalog.db");
        var products = Count(catalogPath, "CatalogProducts");
        var existingSourceBindings = Count(catalogPath, "ProductSourceBindings");
        var sourceBindings = Math.Max(existingSourceBindings, checked(products * 3));
        var onlineBalances = Math.Max(CountWhere(catalogPath, "InventoryBalances", "LocationId='online'"), products);
        var physicalBalances = CountPhysicalBalances(catalogPath);
        var connections = PlannedConnections(catalogPath);
        var existingBindings = ExistingBindingIdentities(catalogPath);
        var bindingIdentities = existingBindings.ToHashSet();
        bindingIdentities.UnionWith(ProfileBindingIdentities(catalogPath, connections.Where(connection => connection.Enabled && connection.ProfileIdentityVerified)));
        var bindingHash = IdentityHash(bindingIdentities);
        var orders = Count(Path.Combine(directory, "orders.db"), "orders");
        var automation = Count(catalogPath, "AutomationJobs");
        EnsureNoPendingWal();
        var fingerprint = Fingerprint();
        if (!string.Equals(initialFingerprint, fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Yerel veri dry-run sırasında değişti; uygulamayı kapatıp yeniden deneyin.");
        var token = ConfirmationFor(fingerprint);
        var report = new MultiStoreMigrationDryRun(CurrentVersion, directory, fingerprint, token, sourceBindings, onlineBalances, physicalBalances,
            connections.Count, bindingIdentities.Count, bindingHash, orders, automation);
        var requiresMigration = sourceBindings > existingSourceBindings ||
            onlineBalances > CountWhere(catalogPath, "InventoryBalances", "LocationId='online'") ||
            connections.Any(connection => connection.RequiresImport) ||
            bindingIdentities.Count != existingBindings.Count;
        return new(report, requiresMigration);
    }

    public MultiStoreMigrationReceipt Apply(MultiStoreMigrationDryRun report, string confirmationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Version != CurrentVersion || !string.Equals(Path.GetFullPath(report.DataDirectory), directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Göç önizlemesi bu veri klasörü veya sürüm için değil.");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(report.ConfirmationToken), Encoding.UTF8.GetBytes(confirmationToken ?? "")) ||
            !string.Equals(report.ConfirmationToken, ConfirmationFor(report.DataFingerprint), StringComparison.Ordinal))
            throw new InvalidOperationException("Çoklu mağaza göçü için açık yerel onay gerekli.");

        var state = ReadState();
        if (state is not null)
        {
            ValidateBackup(state);
            if (PendingStage(state) is not null)
            {
                try { RestoreApprovedBackup(state); }
                catch (Exception error) when (error is not OutOfMemoryException and not DataRestoreAbruptInterruptionException)
                {
                    throw new InvalidOperationException(
                        "Yarım kalan göç aşaması otomatik geri alınamadı; doğrulanmış yedekten geri dönüş veya onarım gerekli.", error);
                }
                throw new InvalidOperationException(
                    "Yarım kalan göç aşaması doğrulanmış onay yedeğinden geri alındı. Yeni dry-run ve açık onay gerekli.");
            }
            if (state.Counts is null || !Equals(report, state.Counts))
                throw new InvalidOperationException("Göç checkpoint'i yalnız ilk onaylanan dry-run özetiyle sürdürülebilir; yedekten geri dönüş veya onarım gerekli.");
            if (state.Checkpoint == MultiStoreMigrationCheckpoint.Completed) return Receipt(state);
            ValidatePendingState(state);
        }
        if (state is null)
        {
            EnsureNoPendingWal();
            if (!Equals(DryRun(), report))
                throw new InvalidOperationException("Veri göç önizlemesinden sonra değişti; yeni dry-run alın.");
            var backupPath = Path.Combine(Directory.GetParent(directory)?.FullName ?? throw new InvalidOperationException("Veri klasörü üst yolu bulunamadı."),
                Path.GetFileName(directory) + "-multi-store-v1-" + Guid.NewGuid().ToString("N") + ".zip");
            new DataBackupService(directory).Backup(backupPath);
            _ = new DataBackupService(directory).Validate(backupPath);
            state = new(CurrentVersion, Guid.NewGuid().ToString("N"), MultiStoreMigrationCheckpoint.BackupCreated,
                report.DataFingerprint, report.DataFingerprint, backupPath, FileHash(backupPath), DateTime.UtcNow, null, report, ProtectedLegacyFiles());
            WriteState(state);
            Reached(state.Checkpoint);
        }

        if (state.Checkpoint < MultiStoreMigrationCheckpoint.SourceBindingsApplied)
        {
            state = BeginStage(state, MultiStoreMigrationCheckpoint.SourceBindingsApplied);
            _ = new ProductSourceBindingStore(directory).MigrateFromCatalog();
            afterStageWrite?.Invoke(MultiStoreMigrationCheckpoint.SourceBindingsApplied);
            state = Advance(state, MultiStoreMigrationCheckpoint.SourceBindingsApplied);
        }
        if (state.Checkpoint < MultiStoreMigrationCheckpoint.InventoryBalancesApplied)
        {
            state = BeginStage(state, MultiStoreMigrationCheckpoint.InventoryBalancesApplied);
            _ = new InventoryLocationStore(directory);
            afterStageWrite?.Invoke(MultiStoreMigrationCheckpoint.InventoryBalancesApplied);
            state = Advance(state, MultiStoreMigrationCheckpoint.InventoryBalancesApplied);
        }
        if (state.Checkpoint < MultiStoreMigrationCheckpoint.ConnectionsApplied)
        {
            state = BeginStage(state, MultiStoreMigrationCheckpoint.ConnectionsApplied);
            var migration = new MarketplaceConnectionMigration(directory);
            foreach (var channel in new[] { "etsy", "trendyol" })
            {
                MarketplaceConnectionMigrationResult outcome;
                try
                {
                    outcome = migration.ImportLegacyChannel(channel);
                    if (outcome.State == MarketplaceConnectionMigrationState.Failed)
                        throw new InvalidOperationException("Eski mağaza bağlantılarından biri doğrulanamadı; göç tamamlanmadı ve eski dosyalar korundu.");
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    throw RestoreApprovedBackupAfterConnectionFailure(state, error);
                }
                // Kept outside the recovery catch: a test fault here models the
                // process disappearing after a durable account sub-step write.
                afterConnectionResult?.Invoke(outcome);
                afterStageWrite?.Invoke(MultiStoreMigrationCheckpoint.ConnectionsApplied);
            }
            state = Advance(state, MultiStoreMigrationCheckpoint.ConnectionsApplied);
        }
        if (state.Checkpoint < MultiStoreMigrationCheckpoint.ProductBindingsApplied)
        {
            state = BeginStage(state, MultiStoreMigrationCheckpoint.ProductBindingsApplied);
            var migrated = new ProductChannelBindingStore(directory).MigrateVerifiedProfiles(_ =>
                afterStageWrite?.Invoke(MultiStoreMigrationCheckpoint.ProductBindingsApplied));
            if (migrated == 0) afterStageWrite?.Invoke(MultiStoreMigrationCheckpoint.ProductBindingsApplied);
            state = Advance(state, MultiStoreMigrationCheckpoint.ProductBindingsApplied);
        }
        if (state.Checkpoint < MultiStoreMigrationCheckpoint.Verified)
        {
            VerifyProtectedLegacyFiles(state.ProtectedLegacyFiles);
            SqliteConnection.ClearAllPools();
            var finalCounts = DryRun();
            var approved = state.Counts ?? throw new InvalidDataException("Göç checkpoint onay özeti eksik; yedekten geri dönüş veya onarım gerekli.");
            var appliedBindingIdentities = ExistingBindingIdentities(Path.Combine(directory, "catalog.db"));
            if (finalCounts.SourceBindings < approved.SourceBindings || finalCounts.OnlineBalances < approved.OnlineBalances ||
                finalCounts.PhysicalBalances < approved.PhysicalBalances ||
                finalCounts.Connections < approved.Connections || appliedBindingIdentities.Count != approved.ProductBindings ||
                !string.Equals(IdentityHash(appliedBindingIdentities), approved.ProductBindingIdentityHash, StringComparison.Ordinal) ||
                finalCounts.Orders != approved.Orders || finalCounts.AutomationSettings != approved.AutomationSettings)
                throw new InvalidOperationException("Göç doğrulaması beklenen yerel veri sayılarını karşılamadı; yedekten geri dönüş kullanılabilir.");
            state = Advance(state, MultiStoreMigrationCheckpoint.Verified);
        }
        if (state.Checkpoint < MultiStoreMigrationCheckpoint.Completed)
        {
            // Recheck the durable WAL and exact post-Verified snapshot at the last
            // possible boundary. A callback or another local process may have
            // changed data after the Verified checkpoint was persisted.
            ValidateBackup(state);
            ValidatePendingState(state);
            state = state with { Checkpoint = MultiStoreMigrationCheckpoint.Completed, CompletedUtc = DateTime.UtcNow };
            WriteState(state);
            Reached(state.Checkpoint);
        }
        return Receipt(state);
    }

    public MultiStoreMigrationRollbackReceipt Rollback(MultiStoreMigrationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var state = ReadState() ?? throw new InvalidOperationException("Geri alınabilir göç checkpoint'i bulunamadı.");
        if (state.Version != CurrentVersion || state.ReceiptId != receipt.ReceiptId || state.BackupPath != receipt.BackupPath)
            throw new InvalidOperationException("Göç makbuzu checkpoint ile eşleşmiyor.");
        if (!File.Exists(state.BackupPath) || FileHash(state.BackupPath) != state.BackupSha256)
            throw new InvalidOperationException("Göç geri dönüş yedeği doğrulanamadı.");
        _ = new DataBackupService(directory).Validate(state.BackupPath);
        new DataBackupService(directory, afterRestoreCheckpoint).Restore(state.BackupPath);
        var rolledBackUtc = DateTime.UtcNow;
        var receiptPath = state.BackupPath + ".rollback.json";
        AtomicWrite(receiptPath, JsonSerializer.Serialize(new { state.ReceiptId, state.BackupPath, RolledBackUtc = rolledBackUtc }));
        return new(state.ReceiptId, state.BackupPath, rolledBackUtc, receiptPath);
    }

    State Advance(State state, MultiStoreMigrationCheckpoint checkpoint)
    {
        SqliteConnection.ClearAllPools();
        EnsureNoPendingWal();
        state = state with { Checkpoint = checkpoint, CheckpointFingerprint = Fingerprint(), StageInProgress = null, ConnectionImportInProgress = false };
        WriteState(state);
        Reached(checkpoint);
        return state;
    }

    State BeginStage(State state, MultiStoreMigrationCheckpoint stage)
    {
        if (!IsMutatingStage(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        SqliteConnection.ClearAllPools();
        EnsureNoPendingWal();
        state = state with { CheckpointFingerprint = Fingerprint(), StageInProgress = stage, ConnectionImportInProgress = false };
        WriteState(state);
        return state;
    }

    void Reached(MultiStoreMigrationCheckpoint checkpoint) => afterCheckpoint?.Invoke(checkpoint);

    MultiStoreMigrationReceipt Receipt(State state) => new(state.Version, state.ReceiptId, state.Checkpoint, state.BackupPath,
        state.StartedUtc, state.CompletedUtc, state.Counts ?? throw new InvalidDataException("Göç makbuzu onay özeti eksik; yedekten geri dönüş veya onarım gerekli."));

    State? ReadState()
    {
        if (!File.Exists(statePath)) return null;
        var info = new FileInfo(statePath);
        if (info.Length is <= 0 or > MaxStateBytes) throw new InvalidDataException("Göç checkpoint dosyası geçersiz boyutta.");
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(statePath));
            return state is null || state.Version != CurrentVersion || !Enum.IsDefined(state.Checkpoint) ||
                (state.StageInProgress is { } stage && !IsMutatingStage(stage))
                ? throw new InvalidDataException("Göç checkpoint dosyası geçersiz.") : state;
        }
        catch (JsonException error) { throw new InvalidDataException("Göç checkpoint dosyası okunamadı.", error); }
    }

    void ValidateBackup(State state)
    {
        if (state.Version != CurrentVersion || string.IsNullOrWhiteSpace(state.BackupPath) || string.IsNullOrWhiteSpace(state.BackupSha256) ||
            !File.Exists(state.BackupPath) || !string.Equals(FileHash(state.BackupPath), state.BackupSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Göç geri dönüş yedeği eksik veya bozuk; devam etmeden önce yedeği onarın ya da doğrulanmış yedekten geri dönüş yapın.");
        try { _ = new DataBackupService(directory).Validate(state.BackupPath); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Göç geri dönüş yedeği doğrulanamadı; devam etmeden önce yedeği onarın ya da doğrulanmış yedekten geri dönüş yapın.", error);
        }
    }

    void ValidatePendingState(State state)
    {
        if (state.Counts is null || string.IsNullOrWhiteSpace(state.InitialFingerprint) || string.IsNullOrWhiteSpace(state.CheckpointFingerprint))
            throw new InvalidDataException("Göç checkpoint'i eksik; yedekten geri dönüş veya onarım gerekli.");
        SqliteConnection.ClearAllPools();
        EnsureNoPendingWal();
        if (!string.Equals(Fingerprint(), state.CheckpointFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Göç checkpoint'i sonrasında yerel veri değişti; yedekten geri dönüş veya onarım gerekli.");
    }

    static MultiStoreMigrationCheckpoint? PendingStage(State state) => state.StageInProgress ??
        (state.ConnectionImportInProgress ? MultiStoreMigrationCheckpoint.ConnectionsApplied : null);

    static bool IsMutatingStage(MultiStoreMigrationCheckpoint stage) => stage is
        MultiStoreMigrationCheckpoint.SourceBindingsApplied or
        MultiStoreMigrationCheckpoint.InventoryBalancesApplied or
        MultiStoreMigrationCheckpoint.ConnectionsApplied or
        MultiStoreMigrationCheckpoint.ProductBindingsApplied;

    Exception RestoreApprovedBackupAfterConnectionFailure(State state, Exception failure)
    {
        try
        {
            RestoreApprovedBackup(state);
            return new InvalidOperationException(
                "Bağlantı geçişinin bir alt adımı tamamlanamadı; doğrulanmış onay yedeği otomatik geri yüklendi. Yeniden dry-run ve açık onay gerekli.", failure);
        }
        catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException)
        {
            return new InvalidOperationException(
                "Kısmi bağlantı geçişi otomatik geri alınamadı; yazma durduruldu. Doğrulanmış yedekten geri dönüş veya onarım gerekli.",
                new AggregateException(failure, rollbackError));
        }
    }

    void RestoreApprovedBackup(State state)
    {
        ValidateBackup(state);
        SqliteConnection.ClearAllPools();
        new DataBackupService(directory, afterRestoreCheckpoint).Restore(state.BackupPath);
        SqliteConnection.ClearAllPools();
        EnsureNoPendingWal();
        if (!string.Equals(Fingerprint(), state.InitialFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Otomatik geri dönüş ilk onaylanan veri özetiyle eşleşmedi.");
    }

    void WriteState(State state) => AtomicWrite(statePath, JsonSerializer.Serialize(state));

    static void AtomicWrite(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    Dictionary<string, string> ProtectedLegacyFiles()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "credentials.bin", "trendyol.bin" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) result[name] = FileHash(path);
        }
        return result;
    }

    void VerifyProtectedLegacyFiles(IReadOnlyDictionary<string, string> expected)
    {
        foreach (var pair in expected)
        {
            var path = Path.Combine(directory, pair.Key);
            if (!File.Exists(path) || !string.Equals(FileHash(path), pair.Value, StringComparison.Ordinal))
                throw new InvalidOperationException("Eski şifreli bağlantı dosyası değişti; göç tamamlanmadı.");
        }
    }

    List<PlannedConnection> PlannedConnections(string catalogPath)
    {
        var current = new Dictionary<(string Channel, string Shop), (string Id, bool Enabled)>();
        ReadRows(catalogPath, "MarketplaceConnections", "SELECT Id,Channel,ShopId,Enabled FROM MarketplaceConnections", reader =>
            current[(reader.GetString(1).ToLowerInvariant(), reader.GetString(2))] = (reader.GetString(0), reader.GetInt32(3) != 0));
        var legacy = ReadableLegacyConnections();
        var keys = current.Keys.Concat(legacy).Distinct().ToArray();
        var result = new List<PlannedConnection>(keys.Length);
        foreach (var key in keys)
        {
            var exists = current.TryGetValue(key, out var row);
            var id = exists ? row.Id : MarketplaceConnectionMigration.LegacyConnectionId(key.Channel, key.Shop);
            var enabled = !exists || row.Enabled;
            var verified = exists && StoredIdentityIsVerified(catalogPath, id, key.Channel, key.Shop);
            var hasReadableLegacy = legacy.Contains(key);
            result.Add(new(id, key.Channel, key.Shop, enabled, verified || hasReadableLegacy, hasReadableLegacy && !verified));
        }
        return result;
    }

    HashSet<(string Channel, string Shop)> ReadableLegacyConnections()
    {
        var result = new HashSet<(string, string)>();
        try
        {
            var etsy = CredentialStore.Load(directory);
            if (etsy is not null && !string.IsNullOrWhiteSpace(etsy.ShopId)) result.Add(("etsy", etsy.ShopId));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or CryptographicException) { }
        try
        {
            var trendyol = new TrendyolSettingsStore(Path.Combine(directory, "trendyol.bin")).Load();
            if (trendyol is not null && !string.IsNullOrWhiteSpace(trendyol.SupplierId)) result.Add(("trendyol", trendyol.SupplierId));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or CryptographicException) { }
        return result;
    }

    bool StoredIdentityIsVerified(string catalogPath, string connectionId, string channel, string shop)
    {
        var markerMatches = false;
        ReadRows(catalogPath, "MarketplaceCredentialMigrations",
            "SELECT 1 FROM MarketplaceCredentialMigrations WHERE Channel=$channel AND ConnectionId=$connection AND ShopId=$shop", _ => markerMatches = true,
            ("$channel", channel), ("$connection", connectionId), ("$shop", shop));
        if (!markerMatches) return false;
        try
        {
            var vault = new MarketplaceCredentialVault(directory);
            return channel switch
            {
                "etsy" => vault.Load<EtsyCredentials>(connectionId, channel, shop)?.ShopId == shop,
                "trendyol" => vault.Load<TrendyolSettings>(connectionId, channel, shop)?.SupplierId == shop,
                _ => false
            };
        }
        catch (InvalidOperationException) { return false; }
    }

    static HashSet<(string Product, string Connection)> ExistingBindingIdentities(string catalogPath)
    {
        var result = new HashSet<(string, string)>();
        ReadRows(catalogPath, "ProductChannelBindings", "SELECT ProductId,ConnectionId FROM ProductChannelBindings", reader =>
            result.Add((reader.GetString(0), reader.GetString(1))));
        return result;
    }

    static HashSet<(string Product, string Connection)> ProfileBindingIdentities(string catalogPath, IEnumerable<PlannedConnection> connections)
    {
        var products = new HashSet<string>(StringComparer.Ordinal);
        ReadRows(catalogPath, "CatalogProducts", "SELECT Id FROM CatalogProducts WHERE json_valid(Json)=1", reader => products.Add(reader.GetString(0)));
        var byIdentity = connections.ToDictionary(connection => (connection.Channel, connection.Shop));
        var result = new HashSet<(string, string)>();
        ReadRows(catalogPath, "EtsyWorkspace", "SELECT ShopId,Json FROM EtsyWorkspace", reader =>
        {
            var shop = reader.GetString(0);
            if (!byIdentity.TryGetValue(("etsy", shop), out var connection)) return;
            try
            {
                var state = JsonSerializer.Deserialize<EtsyWorkspaceState>(reader.GetString(1));
                if (state is not null) foreach (var profile in state.Profiles.Where(profile => profile.ListingId.HasValue && products.Contains(profile.ProductId) && state.Listings.Count(listing => listing.ListingId == profile.ListingId) == 1))
                    result.Add((profile.ProductId, connection.Id));
            }
            catch (JsonException) { }
        });
        ReadRows(catalogPath, "TrendyolWorkspace", "SELECT SellerId,Json FROM TrendyolWorkspace", reader =>
        {
            var shop = reader.GetString(0);
            if (!byIdentity.TryGetValue(("trendyol", shop), out var connection)) return;
            try
            {
                var state = JsonSerializer.Deserialize<TrendyolWorkspaceState>(reader.GetString(1));
                if (state is not null) foreach (var profile in state.Profiles.Where(profile => products.Contains(profile.ProductId)))
                {
                    var barcode = string.IsNullOrWhiteSpace(profile.IntegrationCode) ? profile.ListingBarcode : profile.IntegrationCode;
                    if (!string.IsNullOrWhiteSpace(barcode) && state.Products.Count(product => product.Barcode == barcode) == 1)
                        result.Add((profile.ProductId, connection.Id));
                }
            }
            catch (JsonException) { }
        });
        return result;
    }

    static string IdentityHash(IEnumerable<(string Product, string Connection)> identities)
    {
        var canonical = string.Join("\n", identities.OrderBy(identity => identity.Product, StringComparer.Ordinal)
            .ThenBy(identity => identity.Connection, StringComparer.Ordinal)
            .Select(identity => identity.Product + "\0" + identity.Connection));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    static int CountPhysicalBalances(string database)
    {
        if (!File.Exists(database) || !TableExists(database, "InventoryBalances") || !TableExists(database, "InventoryLocations")) return 0;
        return ScalarInt(database, "SELECT COUNT(*) FROM InventoryBalances b JOIN InventoryLocations l ON l.Id=b.LocationId WHERE l.Kind=1");
    }

    static int Count(string database, string table) => !File.Exists(database) || !TableExists(database, table) ? 0 : ScalarInt(database, $"SELECT COUNT(*) FROM [{table}]");
    static int CountWhere(string database, string table, string where) => !File.Exists(database) || !TableExists(database, table) ? 0 : ScalarInt(database, $"SELECT COUNT(*) FROM [{table}] WHERE {where}");

    static bool TableExists(string database, string table)
    {
        using var connection = OpenReadOnly(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    static int ScalarInt(string database, string sql)
    {
        using var connection = OpenReadOnly(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static void ReadRows(string database, string requiredTable, string sql, Action<SqliteDataReader> read, params (string Name, object Value)[] parameters)
    {
        if (!File.Exists(database) || !TableExists(database, requiredTable)) return;
        using var connection = OpenReadOnly(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read()) read(reader);
    }

    static SqliteConnection OpenReadOnly(string database)
    {
        var uri = new Uri(Path.GetFullPath(database)).AbsoluteUri + "?immutable=1";
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = uri, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    string Fingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Where(path => !path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) &&
                                    !path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(Path.GetFileName(path), StateFileName, StringComparison.OrdinalIgnoreCase) &&
                                    !Path.GetFileName(path).StartsWith(StateFileName + ".tmp-", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    void EnsureNoPendingWal()
    {
        var pending = Directory.EnumerateFiles(directory, "*-wal", SearchOption.AllDirectories)
            .FirstOrDefault(path => new FileInfo(path).Length > 0);
        if (pending is not null)
            throw new InvalidOperationException("Bekleyen SQLite WAL verisi bulundu; uygulamayı kapatıp veritabanını checkpoint ettikten sonra yeniden deneyin.");
    }

    static string ConfirmationFor(string fingerprint)
    {
        if (fingerprint.Length != 64 || fingerprint.Any(character => !Uri.IsHexDigit(character))) throw new InvalidOperationException("Göç önizleme özeti geçersiz.");
        return "CONFIRM_MULTI_STORE_V1_" + fingerprint[..16];
    }

    static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public static class MultiStoreMigrationStartupGate
{
    public static bool EnsureReady(
        string? dataDirectory,
        Func<MultiStoreMigrationDryRun, string?> requestConfirmation,
        Action<MultiStoreMigrationCheckpoint>? afterCheckpoint = null,
        Action<MarketplaceConnectionMigrationResult>? afterConnectionResult = null,
        Action<MultiStoreMigrationCheckpoint>? afterStageWrite = null,
        Action<DataRestoreCheckpoint>? afterRestoreCheckpoint = null)
    {
        ArgumentNullException.ThrowIfNull(requestConfirmation);
        var migration = new MultiStoreMigrationService(dataDirectory, afterCheckpoint, afterConnectionResult, afterStageWrite, afterRestoreCheckpoint);
        var report = migration.StartupReport();
        if (report is null) return true;
        var confirmation = requestConfirmation(report);
        if (confirmation is null) return false;
        var receipt = migration.Apply(report, confirmation);
        if (receipt.Checkpoint != MultiStoreMigrationCheckpoint.Completed)
            throw new InvalidOperationException("Çoklu mağaza göçü tamamlanmadı; yedekten geri dönüş veya onarım gerekli.");
        return true;
    }
}
