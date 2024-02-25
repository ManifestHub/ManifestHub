using System.Diagnostics.CodeAnalysis;
using ManifestHub;
using CommandLine;
using Newtonsoft.Json;

var result = Parser.Default.ParseArguments<Options>(args)
    .WithNotParsed(errors => {
        foreach (var error in errors) {
            Console.WriteLine(error);
        }

        Environment.Exit(1);
    });

var gdb = new GitDatabase(".", result.Value.Token ?? throw new NullReferenceException(), result.Value.Key ?? throw new NullReferenceException());

var semaphore = new SemaphoreSlim(result.Value.ConcurrentAccount);
var tasks = new List<Task>();

switch (result.Value.Mode) {
    case "download":

        var index = 0;
        var total = gdb.GetAccounts().Count();
        var writeTasks = new List<Task>();

        foreach (var downloader in gdb.GetAccounts().Select(account => new ManifestDownloader(account))) {
            await semaphore.WaitAsync();

            Console.WriteLine($"[{index++}/{total}]Dispatching {downloader.Username}...");
            tasks.Add(Task.Run(async () => {
                try {
                    await downloader.Connect().ConfigureAwait(false);
                    var info = await downloader.GetAccountInfoAsync();
                    await gdb.WriteAccount(info);
                    await downloader.DownloadAllManifestsAsync(result.Value.ConcurrentManifest, gdb, writeTasks)
                        .ConfigureAwait(false);
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

        break;
    case "account":
        var raw = File.ReadAllText(result.Value.Account ?? throw new NullReferenceException())
            .Replace("账号", "\n")
            .Replace("密码", "\n");

        List<string> account;

        try {
            var accountJson = JsonConvert.DeserializeObject<Dictionary<string, List<string?>>>(raw);
            account = accountJson!
                .SelectMany(kvp => new List<string> { kvp.Key, kvp.Value.First()! })
                .ToList();
        }
        catch (Exception) {
            account = raw.Split('\n')
                .Select(line => line.Trim())
                .Select(line => line.Trim([':', '：']))
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (account.Count % 2 != 0) {
                Console.WriteLine("Invalid account file.");
                Environment.Exit(1);
            }
        }

        for (var i = 2 * result.Value.Index; i < account.Count; i += 2 * result.Value.Number) {
            var infoPrev = gdb.GetAccount(account[i]);

            ManifestDownloader downloader;
            if (infoPrev != null) {
                infoPrev.AccountPassword = account[i + 1];
                downloader = new ManifestDownloader(infoPrev);
            } else
                downloader = new ManifestDownloader(account[i], account[i + 1]);

            await semaphore.WaitAsync();
            Console.WriteLine($"Dispatching {account[i]}...");
            tasks.Add(Task.Run(async () => {
                try {
                    await downloader.Connect().ConfigureAwait(false);
                    var info = await downloader.GetAccountInfoAsync();
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
