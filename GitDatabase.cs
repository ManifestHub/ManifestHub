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

    private static ConcurrentDictionary<string, SemaphoreSlim> _lockDictionary = new();
    private uint _uniqueId;

    // Generated Regex for account branch names
    [GeneratedRegex("origin/[A-HJ-NP-Z2-9]{5}-[A-HJ-NP-Z2-9]{4}")]
    private static partial Regex AccountBranchPattern();


    public GitDatabase(string repoPath, string token, string aesKey) {
        _repo = new Repository(repoPath);
        _remote = _repo.Network.Remotes["origin"];
        _signature = new Signature("ManifestHub", "manifesthub@localhost", DateTimeOffset.Now);
        _aesKey = aesKey;

        _lockDictionary = new ConcurrentDictionary<string, SemaphoreSlim>();

        var credential = new UsernamePasswordCredentials {
            Username = "x-access-token",
            Password = token
        };

        _pushOptions = new PushOptions {
            CredentialsProvider = (_, _, _) => credential
        };

        Commands.Fetch(_repo, _remote.Name, ["+refs/heads/*:refs/remotes/origin/*"],
            new FetchOptions
                { Prune = true, TagFetchMode = TagFetchMode.All, CredentialsProvider = (_, _, _) => credential }, null);
    }

    public async Task<Commit?> WriteManifest(ManifestInfoCallback manifest) {
        var branchName = manifest.AppId.ToString();

        var uniqueFileName =
            $"{manifest.DepotId}_{manifest.ManifestId}_{Interlocked.Increment(ref _uniqueId)}.manifest";

        var timeStart = DateTime.Now;
        manifest.Manifest.SaveToFile(uniqueFileName);
        Console.WriteLine($"Manifest {manifest.DepotId}_{manifest.ManifestId} saved in {DateTime.Now - timeStart}.");

        var locker = _lockDictionary.GetOrAdd(branchName, new SemaphoreSlim(1));
        await locker.WaitAsync();

        // Skip if manifest already exists
        if (HasManifest(manifest.AppId, manifest.DepotId, manifest.ManifestId)) {
            Console.WriteLine($"Manifest {manifest.DepotId}_{manifest.ManifestId} already exists.");
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

            // Insert manifest
            var manifestBlob = _repo.ObjectDatabase.CreateBlob(uniqueFileName);
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
        // Commands.Fetch(_repo, _remote.Name, ["+refs/tags/*:refs/tags/*"], null, "Fetching tags for manifest check");
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

    public IEnumerable<AccountInfoCallback> GetAccounts() {
        var accounts = _repo.Branches
            .Where(b => AccountBranchPattern().IsMatch(b.FriendlyName))
            .Select(b =>
                JsonConvert.DeserializeObject<AccountInfoCallback>(b["AccountInfo.json"]?.Target.Peel<Blob>()
                    .GetContentText() ?? "{}"))
            .Where(a => a != null)
            .Cast<AccountInfoCallback>()
            .ToList();

        accounts.ForEach(a => a.Decrypt(_aesKey));

        return accounts;
    }

    public AccountInfoCallback? GetAccount(string accountName) {
        return GetAccounts().FirstOrDefault(account => account.AccountName == accountName);
    }
}
