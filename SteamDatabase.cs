using System.Collections.ObjectModel;
using SteamKit2;
using SteamKit2.Authentication;

namespace ManifestHub;

public class SteamDatabase {
    private readonly SteamClient _steamClient;
    private readonly CallbackManager _manager;
    private readonly SteamUser? _steamUser;
    private readonly SteamApps? _steamApps;

    private Task? _callbackDaemon;

    private TaskCompletionSource<bool>? _connectTaskCompletion;
    private TaskCompletionSource<bool>? _loginTaskCompletion;
    public TaskCompletionSource<ReadOnlyCollection<SteamApps.LicenseListCallback.License>>? Licenses;

    private CancellationTokenSource? _callbackCancellationTokenSource;


    public SteamDatabase() {
        _steamClient = new SteamClient();
        _manager = new CallbackManager(_steamClient);

        _steamUser = _steamClient.GetHandler<SteamUser>();
        _steamApps = _steamClient.GetHandler<SteamApps>();

        _manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _manager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);
    }

    public async Task<bool> ConnectAsync() {
        _connectTaskCompletion = new TaskCompletionSource<bool>();
        _callbackCancellationTokenSource = new CancellationTokenSource();
        Licenses = new TaskCompletionSource<ReadOnlyCollection<SteamApps.LicenseListCallback.License>>();

        Console.WriteLine("Connecting to Steam...");
        _steamClient.Connect();

        _callbackDaemon = Task.Run(() => RunCallbacks(_callbackCancellationTokenSource.Token));

        var loginStatus = await _connectTaskCompletion.Task;

        if (!loginStatus) Console.WriteLine("Failed to connect to Steam.");

        return loginStatus;
    }

    public async Task<bool> LoginAsync(string username, string password, string? previouslyStoredGuardData = null) {
        _loginTaskCompletion = new TaskCompletionSource<bool>();

        Console.WriteLine("Connected to Steam! Logging in '{0}'...", username);

        const bool shouldRememberPassword = true;

        // Begin authenticating via credentials
        var authSession = await _steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails {
            Username = username,
            Password = password,
            IsPersistentSession = shouldRememberPassword,

            // See NewGuardData comment below
            GuardData = previouslyStoredGuardData,

            Authenticator = new UserConsoleAuthenticator()
        });

        // Starting polling Steam for authentication response
        var pollResponse = await authSession.PollingWaitForResultAsync();

        if (pollResponse.NewGuardData != null) {
            // When using certain two factor methods (such as email 2fa), guard data may be provided by Steam
            // for use in future authentication sessions to avoid triggering 2FA again (this works similarly to the old sentry file system).
            // Do note that this guard data is also a JWT token and has an expiration date.
            previouslyStoredGuardData = pollResponse.NewGuardData;

            // Print Hex representation of the guard data
            Console.WriteLine("NewGuardData: " + pollResponse.NewGuardData);
            Console.WriteLine("NewGuardData Length: " + pollResponse.NewGuardData.Length);
        }

        // Logon to Steam with the access token we have received
        // Note that we are using RefreshToken for logging on here
        _steamUser?.LogOn(new SteamUser.LogOnDetails {
            Username = pollResponse.AccountName,
            AccessToken = pollResponse.RefreshToken,
            ShouldRememberPassword =
                shouldRememberPassword, // If you set IsPersistentSession to true, this also must be set to true for it to work correctly
        });

        return await _loginTaskCompletion.Task;
    }

    public async void Disconnect() {
        _steamClient.Disconnect();
        Console.WriteLine("Disconnected from Steam");

        if (_callbackCancellationTokenSource != null)
            await _callbackCancellationTokenSource.CancelAsync();
        if (_callbackDaemon != null)
            await _callbackDaemon;

        // Reset all tasks
        _connectTaskCompletion?.TrySetResult(false);
        _loginTaskCompletion?.TrySetResult(false);
        Licenses?.TrySetResult(
            new ReadOnlyCollection<SteamApps.LicenseListCallback.License>(
                new List<SteamApps.LicenseListCallback.License>()));
    }

    private void OnConnected(SteamClient.ConnectedCallback callback) {
        Console.WriteLine("Connected to Steam, logging in...");
        _connectTaskCompletion?.TrySetResult(true);
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback) {
        Console.WriteLine("Disconnected from Steam");
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback) {
        if (callback.Result == EResult.OK) {
            Console.WriteLine("Successfully logged on to Steam.");
            _loginTaskCompletion?.TrySetResult(true);
        } else {
            Console.WriteLine($"Failed to log on to Steam: {callback.Result}");
            _loginTaskCompletion?.TrySetResult(false);
        }
    }

    private void OnLicenseList(SteamApps.LicenseListCallback callback) {
        if (callback.Result == EResult.OK) {
            Console.WriteLine($"Received {callback.LicenseList.Count} licenses.");
            Licenses?.TrySetResult(callback.LicenseList);
        } else {
            Console.WriteLine("Failed to get licenses.");
        }
    }

    private void RunCallbacks(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            _manager.RunWaitCallbacks(TimeSpan.FromSeconds(0.1));
        }
    }
}
