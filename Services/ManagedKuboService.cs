using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using P2FK.IO.Options;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace P2FK.IO.Services
{
    public sealed class ManagedKuboService : BackgroundService
    {
        private const int MinimumStartupTimeoutSeconds = 5;
        private readonly IKuboIngressService _kuboIngressService;
        private readonly IpfsIngressOptions _options;
        private readonly IHostEnvironment _hostEnvironment;
        private readonly ILogger<ManagedKuboService> _logger;
        private readonly string _kuboExecutablePath;
        private Process? _kuboProcess;
        private bool _startedManagedProcess;

        public ManagedKuboService(
            IKuboIngressService kuboIngressService,
            IOptions<IpfsIngressOptions> options,
            IHostEnvironment hostEnvironment,
            ILogger<ManagedKuboService> logger)
        {
            _kuboIngressService = kuboIngressService;
            _options = options.Value;
            _hostEnvironment = hostEnvironment;
            _logger = logger;
            _kuboExecutablePath = ResolveExecutablePath();
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            if (!_options.ManageKuboProcess)
            {
                _logger.LogInformation("Managed Kubo startup is disabled");
                await base.StartAsync(cancellationToken);
                return;
            }

            if (await _kuboIngressService.IsHealthyAsync(cancellationToken))
            {
                _logger.LogInformation("Kubo already responds at {KuboApiBaseUrl}; skipping managed startup", _options.KuboApiBaseUrl);
                await base.StartAsync(cancellationToken);
                return;
            }

            Directory.CreateDirectory(_options.RepoPath);

            if (!File.Exists(Path.Combine(_options.RepoPath, "config")))
            {
                await RunKuboCommandAsync(cancellationToken, "init", "--profile", _options.KuboInitProfile);
            }

            await ConfigureKuboAsync(cancellationToken);
            _kuboProcess = StartDaemonProcess();
            _startedManagedProcess = true;
            await WaitForKuboAsync(cancellationToken);

            _logger.LogInformation("Managed Kubo daemon started for repo {RepoPath}", _options.RepoPath);
            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_kuboProcess is null)
                return;

            try
            {
                await _kuboProcess.WaitForExitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Managed Kubo daemon exited unexpectedly with code {_kuboProcess.ExitCode}.");
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_startedManagedProcess && _kuboProcess is { HasExited: false })
                {
                    await TryShutdownKuboAsync(cancellationToken);
                }
            }
            finally
            {
                _kuboProcess?.Dispose();
                await base.StopAsync(cancellationToken);
            }
        }

        private async Task ConfigureKuboAsync(CancellationToken cancellationToken)
        {
            await RunKuboCommandAsync(cancellationToken, "config", "Addresses.API", _options.KuboApiMultiAddress);
            await RunKuboCommandAsync(cancellationToken, "config", "Addresses.Gateway", _options.KuboGatewayMultiAddress);
            await RunKuboCommandAsync(
                cancellationToken,
                "config",
                "--json",
                "Addresses.Swarm",
                JsonSerializer.Serialize(_options.KuboSwarmMultiAddresses));
            await RunKuboCommandAsync(cancellationToken, "config", "--json", "Gateway.NoFetch", "true");
        }

        private Process StartDaemonProcess()
        {
            var process = new Process
            {
                StartInfo = CreateStartInfo("daemon", "--migrate=true"),
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    _logger.LogInformation("kubo: {Message}", args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    _logger.LogWarning("kubo: {Message}", args.Data);
            };

            if (!process.Start())
                throw new InvalidOperationException("Failed to start the managed Kubo daemon.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        private async Task WaitForKuboAsync(CancellationToken cancellationToken)
        {
            TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(MinimumStartupTimeoutSeconds, _options.KuboStartupTimeoutSeconds));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            while (!timeoutCts.IsCancellationRequested)
            {
                if (_kuboProcess is { HasExited: true })
                {
                    throw new InvalidOperationException($"Managed Kubo daemon exited during startup with code {_kuboProcess.ExitCode}.");
                }

                if (await _kuboIngressService.IsHealthyAsync(timeoutCts.Token))
                    return;

                await Task.Delay(TimeSpan.FromSeconds(1), timeoutCts.Token);
            }

            throw new TimeoutException($"Managed Kubo daemon did not become healthy within {timeout.TotalSeconds:0} seconds.");
        }

        private async Task TryShutdownKuboAsync(CancellationToken cancellationToken)
        {
            try
            {
                await RunKuboCommandAsync(cancellationToken, "shutdown");
                await _kuboProcess!.WaitForExitAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Graceful Kubo shutdown failed; forcing process termination");
            }

            if (_kuboProcess is { HasExited: false })
            {
                _kuboProcess.Kill(entireProcessTree: true);
                await _kuboProcess.WaitForExitAsync(cancellationToken);
            }
        }

        private async Task RunKuboCommandAsync(CancellationToken cancellationToken, params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = CreateStartInfo(arguments),
                EnableRaisingEvents = false
            };

            if (!process.Start())
                throw new InvalidOperationException($"Failed to start kubo command '{string.Join(" ", arguments)}'.");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            if (process.ExitCode == 0)
                return;

            var builder = new StringBuilder()
                .Append("kubo ")
                .Append(string.Join(" ", arguments))
                .Append(" failed with exit code ")
                .Append(process.ExitCode);

            if (!string.IsNullOrWhiteSpace(stderr))
                builder.Append(": ").Append(stderr.Trim());
            else if (!string.IsNullOrWhiteSpace(stdout))
                builder.Append(": ").Append(stdout.Trim());

            throw new InvalidOperationException(builder.ToString());
        }

        private ProcessStartInfo CreateStartInfo(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _kuboExecutablePath,
                WorkingDirectory = _hostEnvironment.ContentRootPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment["IPFS_PATH"] = _options.RepoPath;
            return startInfo;
        }

        private string ResolveExecutablePath()
        {
            string executableName = OperatingSystem.IsWindows() ? "kubo.exe" : "kubo";
            foreach (string candidate in GetExecutableCandidates(executableName))
            {
                if (candidate.Equals(executableName, StringComparison.Ordinal))
                    return candidate;

                if (File.Exists(candidate))
                    return candidate;
            }

            throw new FileNotFoundException(
                $"Unable to find the Kubo executable. Set {IpfsIngressOptions.SectionName}:KuboExecutablePath (IpfsIngress:KuboExecutablePath) to the kubo binary location.");
        }

        private IEnumerable<string> GetExecutableCandidates(string executableName)
        {
            if (!string.IsNullOrWhiteSpace(_options.KuboExecutablePath))
            {
                string configuredPath = _options.KuboExecutablePath.Trim();
                if (Path.IsPathRooted(configuredPath))
                {
                    yield return configuredPath;
                }
                else if (configuredPath.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || configuredPath.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                {
                    yield return Path.GetFullPath(Path.Combine(_hostEnvironment.ContentRootPath, configuredPath));
                }
                else
                {
                    yield return configuredPath;
                }
            }

            yield return Path.Combine(AppContext.BaseDirectory, executableName);
            yield return Path.Combine(AppContext.BaseDirectory, "tools", "kubo", executableName);
            yield return Path.Combine(_hostEnvironment.ContentRootPath, "tools", "kubo", executableName);
            if (OperatingSystem.IsWindows())
            {
                yield return Path.Combine(AppContext.BaseDirectory, "ipfs.exe");
                yield return Path.Combine(AppContext.BaseDirectory, "tools", "kubo", "ipfs.exe");
                yield return Path.Combine(_hostEnvironment.ContentRootPath, "tools", "kubo", "ipfs.exe");
                yield return "ipfs.exe";
            }
            yield return executableName;
        }
    }
}
