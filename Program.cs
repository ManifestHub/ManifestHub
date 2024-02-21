using System.Diagnostics.CodeAnalysis;
using ManifestHub;
using CommandLine;

var result = Parser.Default.ParseArguments<Options>(args)
    .WithNotParsed(errors => {
        foreach (var error in errors) {
            Console.WriteLine(error);
        }

        Environment.Exit(1);
    });

var gdb = new GitDatabase(".", result.Value.Token ?? throw new NullReferenceException());

var semaphore = new SemaphoreSlim(result.Value.ConcurrentAccount);
var tasks = new List<Task>();

switch (result.Value.Mode) {
    case "download":

        var index = 0;
        var total = gdb.GetAccounts().Count();

        foreach (var downloader in gdb.GetAccounts().Select(account => new ManifestDownloader(account))) {
            await semaphore.WaitAsync();

            Console.WriteLine($"[{index++}/{total}]Dispatching {downloader.Username}...");
            tasks.Add(Task.Run(async () => {
                try {
                    await downloader.Connect().ConfigureAwait(false);
                    var info = await downloader.GetAccountInfoAsync();
                    gdb.WriteAccount(info);
                    await downloader.DownloadAllManifestsAsync(result.Value.ConcurrentManifest, gdb)
                        .ConfigureAwait(false);
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
    case "account":
        var raw = File.ReadAllText(result.Value.Account ?? throw new NullReferenceException())
            .Replace("账号", "\n")
            .Replace("密码", "\n");

        var account = raw.Split('\n')
            .Select(line => line.Trim())
            .Select(line => line.Trim([':', '：']))
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (account.Length % 2 != 0) {
            Console.WriteLine("Invalid account file.");
            Environment.Exit(1);
        }

        for (var i = 0; i < account.Length; i += 2) {
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
                        gdb.WriteAccount(info);
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
    }
}
