using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using Newtonsoft.Json;

namespace ManifestHub;

public partial class GitDatabase {
    private readonly Repository _repo;
    private readonly Remote _remote;
    private readonly Signature _signature;
    private readonly PushOptions _pushOptions;
    private readonly string _aesKey;

    private readonly ConcurrentDictionary<uint, byte> _trackingApps;
    private readonly ConcurrentDictionary<uint, byte> _trackingDepots;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lockDictionary;

    // Generated Regex for account branch names
    [GeneratedRegex("origin/[A-HJ-NP-Z2-9]{5}-[A-HJ-NP-Z2-9]{4}")]
    private static partial Regex AccountBranchPattern();

    // Generated Regex for manifest tag names
    [GeneratedRegex(@"(\d+)_(\d+)_(\d+)")]
    private static partial Regex ManifestTagPattern();


    public GitDatabase(string repoPath, string token, string aesKey) {
        // Disable strict object creation
        GlobalSettings.SetEnableStrictObjectCreation(false);

        _repo = new Repository(repoPath);
        _remote = _repo.Network.Remotes["origin"];
        _signature = new Signature("ManifestHub", "manifesthub@localhost", DateTimeOffset.Now);
        _aesKey = aesKey;

        _trackingApps = new ConcurrentDictionary<uint, byte>();
        _trackingDepots = new ConcurrentDictionary<uint, byte>();
        _lockDictionary = new ConcurrentDictionary<string, SemaphoreSlim>();

        var credential = new UsernamePasswordCredentials {
            Username = "x-access-token",
            Password = token
        };

        _pushOptions = new PushOptions {
            CredentialsProvider = (_, _, _) => credential
        };
    }

    public async Task<Commit?> WriteManifest(ManifestInfoCallback manifest) {
        var branchName = manifest.AppId.ToString();

        var locker = _lockDictionary.GetOrAdd(branchName, new SemaphoreSlim(1));

        // Wait for lock with status message
        while (!await locker.WaitAsync(5000))
            Console.WriteLine(
                $"Manifest {manifest.DepotId}_{manifest.ManifestId} is waiting for lock on {branchName}...");

        // Skip if manifest already exists
        if (HasManifest(manifest.AppId, manifest.DepotId, manifest.ManifestId)) {
            Console.WriteLine($"Manifest {manifest.DepotId}_{manifest.ManifestId} already exists.");
            locker.Release();
            return null;
        }

        try {
            var branch = _repo.Branches[_remote.Name + "/" + branchName];
            var tree = branch?.Tip.Tree;
            var treeDef = tree != null ? TreeDefinition.From(tree) : new TreeDefinition();

            // Remove old manifest
            if (tree != null)
                foreach (var entry in tree) {
                    try {
                        var fileDepot = uint.Parse(entry.Name.Split('_')[0]);
                        if (fileDepot == manifest.DepotId) treeDef.Remove(entry.Path);
                    }
                    catch (FormatException) {
                        // Ignore non-manifest files
                    }
                }

            // Insert decryption key
            var keyConfig =
                VdfConvert.Deserialize(tree?["Key.vdf"]?.Target.Peel<Blob>().GetContentText() ?? "\"depots\"{}");
            var depots = (VObject)keyConfig.Value;
            depots[manifest.DepotId.ToString()] = new VObject {
                { "DecryptionKey", new VValue(Convert.ToHexString(manifest.DepotKey)) }
            };
            var keyBlob =
                _repo.ObjectDatabase.CreateBlob(
                    new MemoryStream(Encoding.UTF8.GetBytes(VdfConvert.Serialize(keyConfig))));
            treeDef.Add("Key.vdf", keyBlob, Mode.NonExecutableFile);

            // Create a MemoryStream to hold the serialized data
            using var ms = new MemoryStream();
            // Serialize the manifest directly into the MemoryStream
            manifest.Manifest.Serialize(ms);
            ms.Position = 0; // Reset the stream position to the beginning for reading

            // Insert manifest blob into the repository
            var manifestBlob = _repo.ObjectDatabase.CreateBlob(ms);
            treeDef.Add($"{manifest.DepotId}_{manifest.ManifestId}.manifest", manifestBlob, Mode.NonExecutableFile);

            // Skip if no changes
            var newTree = _repo.ObjectDatabase.CreateTree(treeDef);
            if (tree != null && newTree.Id == tree.Id) {
                Console.WriteLine($"Manifest {manifest.DepotId}_{manifest.ManifestId} no changes.");

                try {
                    // Failsafe: push branch tip as tag
                    _repo.Tags.Add($"{manifest.AppId}_{manifest.DepotId}_{manifest.ManifestId}", branch!.Tip);
                    _repo.Network.Push(_remote,
                        $"{branch.Tip.Id.Sha}:refs/tags/{manifest.AppId}_{manifest.DepotId}_{manifest.ManifestId}",
                        _pushOptions);
                }
                catch (LibGit2SharpException) {
                    // Ignore if tag already exists
                }

                return null;
            }

            // Commit
            var newCommit = _repo.ObjectDatabase.CreateCommit(
                _signature,
                _signature,
                $"Update {manifest.DepotId}_{manifest.ManifestId}.manifest",
                newTree,
                branch != null ? new[] { branch.Tip } : [],
                true);

            // Push manifest to remote
            _repo.Network.Push(_remote, $"{newCommit.Id.Sha}:refs/heads/{branchName}", _pushOptions);

            // Add & Push tag
            _repo.Network.Push(_remote,
                $"{newCommit.Id.Sha}:refs/tags/{manifest.AppId}_{manifest.DepotId}_{manifest.ManifestId}",
                _pushOptions);
            _repo.Tags.Add($"{manifest.AppId}_{manifest.DepotId}_{manifest.ManifestId}", newCommit);

            return newCommit;
        }
        finally {
            locker.Release();
        }
    }

    public bool HasManifest(uint appId, uint depotId, ulong manifestId) {
        // Update trackingCounters
        _trackingApps.TryAdd(appId, 0);
        _trackingDepots.TryAdd(depotId, 0);

        return _repo.Tags[$"{appId}_{depotId}_{manifestId}"] != null;
    }

    public async Task WriteAccount(AccountInfoCallback account) {
        var branchName = account.Index ?? throw new ArgumentNullException(nameof(account));

        var locker = _lockDictionary.GetOrAdd(branchName, new SemaphoreSlim(1));

        await locker.WaitAsync();

        try {
            var branch = _repo.Branches[_remote.Name + "/" + branchName];
            var tree = branch?.Tip.Tree;
            var treeDef = tree != null ? TreeDefinition.From(tree) : new TreeDefinition();

            account.Encrypt(_aesKey);

            var accountBlob =
                _repo.ObjectDatabase.CreateBlob(
                    new MemoryStream(
                        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(account, Formatting.Indented))));
            treeDef.Add("AccountInfo.json", accountBlob, Mode.NonExecutableFile);

            var newTree = _repo.ObjectDatabase.CreateTree(treeDef);
            if (tree != null && newTree.Id == tree.Id) return;

            var newCommit = _repo.ObjectDatabase.CreateCommit(
                _signature,
                _signature,
                $"Update AccountInfo.json for {account.AccountName}",
                newTree,
                branch != null ? new[] { branch.Tip } : [],
                true);

            _repo.Network.Push(_remote, $"{newCommit.Id.Sha}:refs/heads/{branchName}", _pushOptions);
        }
        finally {
            locker.Release();
        }
    }

    public async Task RemoveAccount(AccountInfoCallback account) {
        var branchName = account.Index ?? throw new ArgumentNullException(nameof(account));

        var locker = _lockDictionary.GetOrAdd(branchName, new SemaphoreSlim(1));

        await locker.WaitAsync();

        try {
            _repo.Network.Push(_remote, $"+:refs/heads/{branchName}", _pushOptions);
        }
        finally {
            locker.Release();
        }
    }

    public IEnumerable<AccountInfoCallback> GetAccounts(bool randomize = false) {
        var rng = randomize ? new Random() : new Random(0);

        var accounts = _repo.Branches
            .Where(b => AccountBranchPattern().IsMatch(b.FriendlyName))
            .Select(b =>
                JsonConvert.DeserializeObject<AccountInfoCallback>(b["AccountInfo.json"]?.Target.Peel<Blob>()
                    .GetContentText() ?? "{}"))
            .Where(a => a != null)
            .Cast<AccountInfoCallback>()
            .OrderBy(_ => rng.Next())
            .ToList();

        accounts.ForEach(a => a.Decrypt(_aesKey));

        return accounts;
    }

    public AccountInfoCallback? GetAccount(string accountName) {
        return GetAccounts().FirstOrDefault(account => account.AccountName == accountName);
    }

    public string ReportTrackingStatus() {
        // Parse IDs of managed apps from tags matching app branch pattern
        var managedApps = _repo.Tags
            .Where(t => ManifestTagPattern().IsMatch(t.FriendlyName))
            .Select(t => uint.Parse(ManifestTagPattern().Match(t.FriendlyName).Groups[1].Value))
            .ToHashSet(); // Use HashSet for optimized search performance

        // Parse IDs of managed depots from tags matching manifest tag pattern
        var managedDepots = _repo.Tags
            .Where(t => ManifestTagPattern().IsMatch(t.FriendlyName))
            .Select(t => uint.Parse(ManifestTagPattern().Match(t.FriendlyName).Groups[2].Value))
            .ToHashSet();

        // IDs of touched apps and depots
        // var touchedApps = new HashSet<uint>(_trackingApps.Keys);
        var touchedApps = new HashSet<uint>(GetAccounts().SelectMany(a => a.AppIds));
        var touchedDepots = new HashSet<uint>(_trackingDepots.Keys);

        // Calculate status for Apps
        var appsData = CalculateStatus(managedApps, touchedApps);
        // Calculate status for Depots
        var depotsData = CalculateStatus(managedDepots, touchedDepots);

        var markdownBuilder = new StringBuilder();
        markdownBuilder.AppendLine("# Tracking Status Report :rocket:\n");

        // Append status tables for Apps and Depots to the report
        AppendStatusTable(markdownBuilder, "Apps", appsData);
        AppendStatusTable(markdownBuilder, "Depots", depotsData);

        return markdownBuilder.ToString();
    }

    private static void AppendStatusTable(StringBuilder markdownBuilder, string category,
        (int Active, int Orphan, int AccessDenied) data) {
        // Append a status table for a category (Apps or Depots) to the markdown builder
        markdownBuilder.AppendLine($"## {category}\n");
        markdownBuilder.AppendLine("| Status | Count |");
        markdownBuilder.AppendLine("|--------|-------|");
        markdownBuilder.AppendLine($"| Active | {data.Active} |");
        markdownBuilder.AppendLine($"| Orphan | {data.Orphan} |");
        markdownBuilder.AppendLine($"| Access Denied | {data.AccessDenied} |");
        markdownBuilder.AppendLine();
    }

    private static (int Active, int Orphan, int AccessDenied) CalculateStatus(IReadOnlyCollection<uint> managed,
        IReadOnlyCollection<uint> touched) {
        // Calculate active, orphan, and access denied counts
        var active = managed.Intersect(touched).Count(); // Count of items both managed and touched
        var orphan = managed.Except(touched).Count(); // Count of items managed but not touched
        var accessDenied = touched.Except(managed).Count(); // Count of items touched but not managed

        return (active, orphan, accessDenied);
    }

    public Task PruneExpiredTags() {
        var tags = _repo.Tags
            .Select(t => new {
                Tag = t,
                TimeStamp = t.Target.Peel<Commit>().Author.When,
                Match = ManifestTagPattern().Match(t.FriendlyName)
            })
            .Where(t => t.Match.Success)
            .ToList();

        var validTags = new Dictionary<Tuple<uint, uint>, Tuple<ulong, DateTimeOffset>>();

        foreach (var tag in tags) {
            var match = tag.Match;
            var appId = uint.Parse(match.Groups[1].Value);
            var depotId = uint.Parse(match.Groups[2].Value);
            var manifestId = ulong.Parse(match.Groups[3].Value);

            var expiredTagName = null as string;

            if (!validTags.ContainsKey(Tuple.Create(appId, depotId))) {
                validTags.Add(Tuple.Create(appId, depotId), Tuple.Create(manifestId, tag.TimeStamp));
            } else if (validTags[Tuple.Create(appId, depotId)].Item2 < tag.TimeStamp) {
                // Previous tag expired
                expiredTagName = $"{appId}_{depotId}_{validTags[Tuple.Create(appId, depotId)].Item1}";
                // Update valid tag
                validTags[Tuple.Create(appId, depotId)] = Tuple.Create(manifestId, tag.TimeStamp);
            } else {
                // Current tag expired
                expiredTagName = $"{appId}_{depotId}_{manifestId}";
            }

            // Skip if no expired tag
            if (expiredTagName == null) continue;

            // Remove expired tag
            _repo.Tags.Remove(expiredTagName);
            _repo.Network.Push(_remote, $"+:refs/tags/{expiredTagName}", _pushOptions);
            Console.WriteLine($"Pruned expired tag {expiredTagName}.");
        }

        return Task.CompletedTask;
    }
}
