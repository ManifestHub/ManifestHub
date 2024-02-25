using System.Diagnostics;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;
using SteamKit2.Discovery;

namespace ManifestHub;

class ManifestDownloader {
    private readonly Client _cdnClient;
    private readonly SteamApps _steamApps;
    private readonly SteamUser _steamUser;
    private readonly SteamClient _steamClient;
    private readonly SteamContent _steamContent;

    private readonly Task _daemonTask;
    private readonly CancellationTokenSource _cancellationTokenSource;

    private readonly string? _password;
    private string? _refreshToken;
    private readonly DateTime? _lastRefresh;
    private string? _newRefreshToken;

    private AccountInfoCallback? _accountInfo;

    private readonly TaskCompletionSource _licenseReady = new();
    private readonly HashSet<SteamApps.LicenseListCallback.License> _licenses = [];
    private readonly TaskCompletionSource _loginReady = new();

    public ManifestDownloader(AccountInfoCallback accountInfo) : this(
        accountInfo.AccountName ?? throw new ArgumentNullException(nameof(accountInfo)),
        accountInfo.AccountPassword,
        accountInfo.RefreshToken) {
        _lastRefresh = accountInfo.LastRefresh;
        _accountInfo = accountInfo;
    }

    public ManifestDownloader(string username, string? password = null, string? refreshToken = null) {
        _steamClient = new SteamClient(SteamConfiguration.Create(
            builder => {
                builder.WithProtocolTypes(ProtocolTypes.All);
                builder.WithServerListProvider(new FileStorageServerListProvider("servers.bin"));
                builder.WithDirectoryFetch(true);
                builder.WithUniverse(EUniverse.Public);
            }
        ));
        _cdnClient = new Client(_steamClient);
        _steamApps = _steamClient.GetHandler<SteamApps>() ?? throw new NullReferenceException();
        _steamUser = _steamClient.GetHandler<SteamUser>() ?? throw new NullReferenceException();
        _steamContent = _steamClient.GetHandler<SteamContent>() ?? throw new NullReferenceException();

        var manager = new CallbackManager(_steamClient);
        _cancellationTokenSource = new CancellationTokenSource();

        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);

        _daemonTask = Task.Run(() => {
            while (!_cancellationTokenSource.Token.IsCancellationRequested) {
                manager.RunWaitCallbacks(TimeSpan.FromSeconds(0.1));
            }
        });

        Username = username;
        _password = password;
        _refreshToken = refreshToken;
    }

    public string Username { get; }

    public Task Connect() {
        _steamClient.Connect();
        return _loginReady.Task;
    }

    public Task Disconnect() {
        _steamClient.Disconnect();
        _cancellationTokenSource.Cancel();
        return _daemonTask;
    }

    private async Task<ManifestInfoCallback> DownloadManifestAsync(uint appid, uint depotId, ulong manifestId,
        Server server, uint maxRetries = 30) {
        var retries = 0;
        var key = null as byte[];
        var requestCode = 0ul;

        const int retryInterval = 10000;

        while (retries < maxRetries) {
            try {
                requestCode = await _steamContent.GetManifestRequestCode(depotId, appid, manifestId)
                    .ConfigureAwait(false);
                break;
            }
            catch (Exception) {
                retries++;
                await Task.Delay(retryInterval);
            }
        }

        if (requestCode == 0) {
            if (retries >= maxRetries)
                throw new Exception(
                    $"Failed to get manifest request code. AppID: {appid}, DepotID: {depotId}, ManifestID: {manifestId}");
            throw new Exception(
                $"Access denied to manifest. AppID: {appid}, DepotID: {depotId}, ManifestID: {manifestId}");
        }

        while (retries < maxRetries) {
            try {
                key = (await _steamApps.GetDepotDecryptionKey(depotId, appid)).DepotKey;
                break;
            }
            catch (Exception) {
                retries++;
                await Task.Delay(retryInterval);
            }
        }

        if (key == null) throw new Exception($"Failed to get depot key. AppID: {appid}, DepotID: {depotId}");


        while (retries < maxRetries) {
            try {
                var manifest = await _cdnClient.DownloadManifestAsync(depotId, manifestId, requestCode, server, key)
                    .ConfigureAwait(false);
                return new ManifestInfoCallback(appid, depotId, manifestId, key, manifest);
            }
            catch (Exception) {
                retries++;
                await Task.Delay(retryInterval);
            }
        }

        throw new Exception(
            $"Failed to download manifest. AppID: {appid}, DepotID: {depotId}, ManifestID: {manifestId}");
    }

    public async Task DownloadAllManifestsAsync(int maxConcurrentDownloads = 16,
        GitDatabase? gdb = null, List<Task>? writeTasks = null) {
        await _licenseReady.Task.ConfigureAwait(false);

        var packagePicsRequest = _licenses
            .Where(license => license.PaymentMethod != EPaymentMethod.Complimentary)
            .Select(license => new SteamApps.PICSRequest {
                ID = license.PackageID,
                AccessToken = license.AccessToken,
            });

        var productInfo =
            await _steamApps.PICSGetProductInfo([], packagePicsRequest).ToTask().ConfigureAwait(false);

        if (!productInfo.Complete || productInfo.Results == null) throw new Exception("Failed to get product info");

        var products = productInfo.Results.SelectMany(result => result.Packages).ToDictionary();
        var appIds = products.SelectMany(product =>
            product.Value.KeyValues["appids"].Children
                .Select(app => app.AsUnsignedInteger())
                .Where(app => app != 0)).Distinct().ToList();

        var appTokens = await _steamApps.PICSGetAccessTokens(appIds, []).ToTask().ConfigureAwait(false);

        var appPicsRequest = appTokens.AppTokens.Select(token => new SteamApps.PICSRequest {
            ID = token.Key,
            AccessToken = token.Value,
        });

        var appInfo = await _steamApps.PICSGetProductInfo(appPicsRequest, []).ToTask().ConfigureAwait(false);
        var servers = (await _steamContent.GetServersForSteamPipe()).ToArray();

        if (!appInfo.Complete || appInfo.Results == null) throw new Exception("Failed to get app info");

        var apps = appInfo.Results.SelectMany(result => result.Apps).ToDictionary();

        var semaphore = new SemaphoreSlim(maxConcurrentDownloads);
        var tasksList = new List<Func<Task<ManifestInfoCallback>>>();
        var downloadTasks = new List<Task>();

        foreach (var app in apps) {
            var depots = app.Value.KeyValues["depots"].Children
                .Where(depot => depot.Name?.All(char.IsDigit) ?? false)
                .Where(depot => depot["manifests"]["public"] != KeyValue.Invalid)
                .SelectMany(depot => new Dictionary<uint, ulong> {
                    { uint.Parse(depot.Name!), depot["manifests"]["public"]["gid"].AsUnsignedLong() }
                })
                .Where(depot => !(gdb?.HasManifest(app.Key, depot.Key, depot.Value) ?? false)).ToDictionary();

            tasksList.AddRange(depots.Select(depot => (Func<Task<ManifestInfoCallback>>)(
                () => DownloadManifestAsync(app.Key, depot.Key, depot.Value, servers[depot.Key % servers.Length])
            )));
        }

        foreach (var task in tasksList) {
            await semaphore.WaitAsync();
            Debug.Assert(gdb != null, nameof(gdb) + " != null");
            Debug.Assert(writeTasks != null, nameof(writeTasks) + " != null");

            downloadTasks.Add(Task.Run(
                    async () => {
                        try {
                            var result = await task();
                            Console.WriteLine(
                                $"[Success]: AppID: {result.AppId}, DepotID: {result.DepotId}, ManifestID: {result.ManifestId}");

                            writeTasks.Add(Task.Run(
                                async () => {
                                    var commit = await gdb.WriteManifest(result);
                                    Console.WriteLine(
                                        $"[Written]: AppID: {result.AppId}, DepotID: {result.DepotId}, ManifestID: {result.ManifestId}, Commit: {commit?.Sha}");
                                }
                            ));
                        }
                        catch (Exception e) {
                            if (!e.Message.Contains("Access denied to manifest") &&
                                !e.Message.Contains("Failed to get depot key"))
                                Console.WriteLine("[Failed]: " + e.Message);
                        }
                        finally {
                            semaphore.Release();
                        }
                    }
                )
            );
        }

        await Task.WhenAll(downloadTasks).ConfigureAwait(false);
    }


    public async Task<AccountInfoCallback> GetAccountInfoAsync() {
        await _loginReady.Task.ConfigureAwait(false);

        _accountInfo ??= new AccountInfoCallback(
            accountName: Username
        );

        _accountInfo.AccountPassword = _password;
        _accountInfo.RefreshToken = _newRefreshToken ?? _refreshToken;
        _accountInfo.LastRefresh = (_newRefreshToken != null ? DateTime.Now : _lastRefresh) ?? DateTime.Now;
        _accountInfo.Index = _steamUser.SteamID?.AsCsgoFriendCode();

        return _accountInfo;
    }


    private async void OnConnected(SteamClient.ConnectedCallback callback) {
        Console.WriteLine($"Connected to Steam! Logging in '{Username}'...");

        if (!string.IsNullOrEmpty(_refreshToken)) {
            _steamUser.LogOn(new SteamUser.LogOnDetails {
                Username = Username,
                AccessToken = _refreshToken,
                ShouldRememberPassword = true,
            });
        } else {
            AuthPollResult? pollResponse;
            try {
                AuthSession authSession = await _steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(
                    new AuthSessionDetails {
                        Username = Username,
                        Password = _password,
                        IsPersistentSession = false,
                        GuardData = null,
                        Authenticator = null
                    }).ConfigureAwait(false);

                pollResponse = await authSession.PollingWaitForResultAsync().ConfigureAwait(false);
            }
            catch (Exception e) {
                _loginReady.TrySetException(e);
                return;
            }


            if (!string.IsNullOrEmpty(pollResponse.RefreshToken)) {
                _newRefreshToken = pollResponse.RefreshToken;

                _steamUser.LogOn(new SteamUser.LogOnDetails {
                    Username = pollResponse.AccountName,
                    AccessToken = pollResponse.RefreshToken,
                    ShouldRememberPassword = true,
                });
            } else {
                Console.WriteLine("Failed to get RefreshToken");
                await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private async void OnLoggedOn(SteamUser.LoggedOnCallback callback) {
        if (callback.Result != EResult.OK) {
            if (!string.IsNullOrEmpty(_refreshToken)) {
                Console.WriteLine(
                    $"[Previous RefreshToken] Unable to logon to Steam: {callback.Result}, Retrying...");
                _refreshToken = null;
                _steamClient.Connect();
            } else {
                _loginReady.TrySetException(new Exception($"Unable to logon to Steam: {callback.Result}"));
            }

            return;
        }

        Console.WriteLine(string.IsNullOrEmpty(_newRefreshToken)
            ? "Logged on using previous RefreshToken"
            : "Logged on using new RefreshToken");

        await _licenseReady.Task.ConfigureAwait(false);
        _loginReady.TrySetResult();
    }

    private async void OnDisconnected(SteamClient.DisconnectedCallback callback) {
        if (!callback.UserInitiated) {
            Console.WriteLine($"[Reconnecting] {Username} disconnected from Steam. Reconnecting in 5 seconds...");
            await Task.Delay(5000);
            _steamClient.Connect();
        } else {
            Console.WriteLine("Disconnected from Steam");
        }
    }

    private void OnLicenseList(SteamApps.LicenseListCallback callback) {
        Console.WriteLine("License list received");
        _licenses.UnionWith(callback.LicenseList);
        _licenseReady.TrySetResult();
    }
}
