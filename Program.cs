using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ManifestHub;
using CommandLine;
using Newtonsoft.Json;
using SteamKit2;
using SteamKit2.Authentication;
using System.Security.Cryptography;
using System.Text;

var result = Parser.Default.ParseArguments<Options>(args)
    .WithNotParsed(errors => {
        foreach (var error in errors) {
            Console.WriteLine(error);
        }

        Environment.Exit(1);
    });

var gdb = new GitDatabase(".", result.Value.Token ?? throw new NullReferenceException(),
    result.Value.Key ?? throw new NullReferenceException());

var semaphore = new SemaphoreSlim(result.Value.ConcurrentAccount);
var tasks = new ConcurrentBag<Task>();
var writeTasks = new ConcurrentBag<Task>();

switch (result.Value.Mode) {
    case "download":

        var index = 0;
        var total = gdb.GetAccounts().Count();

        foreach (var accountInfo in gdb.GetAccounts(true)) {
            await semaphore.WaitAsync();

            Console.WriteLine($"[{index++}/{total}]Dispatching {accountInfo.AccountName}...");
            tasks.Add(Task.Run(async () => {
                var downloader = new ManifestDownloader(accountInfo);
                try {
                    await downloader.Connect().ConfigureAwait(false);
                    var info = await downloader.GetAccountInfo();
                    await gdb.WriteAccount(info);
                    await downloader.DownloadAllManifestsAsync(result.Value.ConcurrentManifest, gdb, writeTasks)
                        .ConfigureAwait(false);
                }
                catch (AuthenticationException e) when (e.Result is
                                                            EResult.InvalidPassword
                                                            or EResult.AccountLogonDeniedVerifiedEmailRequired
                                                            or EResult.AccountLoginDeniedNeedTwoFactor) {
                    await gdb.RemoveAccount(accountInfo);
                    Console.WriteLine($"{e.Result} for {downloader.Username}. Removed.");
                }
                catch (Exception e) {
                    Console.WriteLine(e.Message);
                }
                finally {
                    _ = downloader.Disconnect();
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        Console.WriteLine("Waiting for write tasks...");
        await Task.WhenAll(writeTasks);
        Console.WriteLine("Start tag pruning...");
        await gdb.PruneExpiredTags();

        Console.WriteLine("Writing summary...");
        var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (summaryPath != null) {
            var summaryFile = File.OpenWrite(summaryPath);
            summaryFile.Write(System.Text.Encoding.UTF8.GetBytes(gdb.ReportTrackingStatus()));
            summaryFile.Close();
            Console.WriteLine("Summary written.");
        } else {
            Console.WriteLine("Cannot find GITHUB_STEP_SUMMARY");
        }

        Console.WriteLine("Done.");

        break;
    case "account":
        var raw = File.ReadAllText(result.Value.Account ?? throw new NullReferenceException());

        // Detect if the account file is encrypted
        try {
            var dictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(raw);
            var encryptedAccount = dictionary?["payload"];
            var rsa = new RSACryptoServiceProvider();
            var rsaPrivateKey = Environment.GetEnvironmentVariable("RSA_PRIVATE_KEY");

            // Load the RSA private key in PEM format
            rsa.ImportFromPem(rsaPrivateKey);

            // Decrypt the encrypted account information
            var decryptedBytes = rsa.Decrypt(Convert.FromBase64String(encryptedAccount!), true);
            raw = Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (Exception e) {
            Console.WriteLine(e.Message);
        }

        KeyValuePair<string, List<string?>>[] account;

        try {
            var accountJson = JsonConvert.DeserializeObject<Dictionary<string, List<string?>>>(raw);
            account = accountJson!.ToArray();
        }
        catch (Exception) {
            account = [];
            Console.WriteLine("Invalid account file.");
            Environment.Exit(1);
        }

        for (var i = result.Value.Index; i < account.Length; i += result.Value.Number) {
            var infoPrev = gdb.GetAccount(account[i].Key);

            ManifestDownloader downloader;
            if (infoPrev != null) {
                infoPrev.AccountPassword = account[i].Value.FirstOrDefault();
                downloader = new ManifestDownloader(infoPrev);
            } else {
                downloader = new ManifestDownloader(new AccountInfoCallback(
                    account[i].Key,
                    account[i].Value.FirstOrDefault()
                ));
            }

            await semaphore.WaitAsync();
            Console.WriteLine($"Dispatching {account[i].Key}...");
            tasks.Add(Task.Run(async () => {
                try {
                    await downloader.Connect().ConfigureAwait(false);
                    var info = await downloader.GetAccountInfo();
                    if (infoPrev == null || info.RefreshToken != infoPrev.RefreshToken)
                        await gdb.WriteAccount(info);
                }
                catch (Exception e) {
                    Console.WriteLine(e.Message);
                }
                finally {
                    await downloader.Disconnect();
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);

        break;
    default:
        Console.WriteLine("Invalid mode of operation.");
        Environment.Exit(1);
        break;
}

namespace ManifestHub {
    // ReSharper disable once ClassNeverInstantiated.Global
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    internal class Options {
        [Value(0, MetaName = "Mode", Default = "download", HelpText = "Mode of operation.")]
        public string? Mode { get; set; }

        [Option('a', "account", Required = false, HelpText = "Account file.")]
        public string? Account { get; set; }

        [Option('t', "token", Required = true, HelpText = "GitHub Access Token.")]
        public string? Token { get; set; }

        [Option('c', "concurrent-account", Required = false, HelpText = "Concurrent account.", Default = 4)]
        public int ConcurrentAccount { get; set; }

        [Option('p', "concurrent-manifest", Required = false, HelpText = "Concurrent manifest.", Default = 16)]
        public int ConcurrentManifest { get; set; }

        [Option('i', "index", Required = false, HelpText = "Index of instance.", Default = 0)]
        public int Index { get; set; }

        [Option('n', "number", Required = false, HelpText = "Number of instances.", Default = 1)]
        public int Number { get; set; }

        [Option('k', "key", Required = false, HelpText = "Encryption key.")]
        public string? Key { get; set; }
    }
}
