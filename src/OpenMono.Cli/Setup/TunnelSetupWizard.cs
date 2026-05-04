using System.Diagnostics;
using System.Runtime.InteropServices;
using Spectre.Console;
using OpenMono.Utils;

namespace OpenMono.Setup;

public static class TunnelSetupWizard
{
    private const string FrpVersion = "0.61.0";

    public static async Task<int> RunAsync(string dataDirectory, CancellationToken ct = default)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold blue]OpenMono.ai[/] [dim]— Inference Tunnel Setup[/]");
        AnsiConsole.MarkupLine("[dim]─────────────────────────────────────────[/]");
        AnsiConsole.WriteLine();

        var config = RelayConfigStore.Load(dataDirectory);

        if (config is not null && !string.IsNullOrEmpty(config.RelayToken))
        {
            AnsiConsole.MarkupLine($"  [green]✓[/] Found existing relay config for [cyan]{Markup.Escape(config.Email)}[/]");
            AnsiConsole.WriteLine();
        }
        else
        {
            config = await RunOtpFlowAsync(dataDirectory, ct);
            if (config is null) return 1;
        }

        var installed = await InstallFrpcAsync(config, ct);
        if (!installed) return 1;

        PrintBanner(config);
        return 0;
    }

    private static async Task<RelayConfig?> RunOtpFlowAsync(string dataDirectory, CancellationToken ct)
    {
        var email = AnsiConsole.Prompt(
            new TextPrompt<string>("  Enter your email address: ")
                .Validate(e => e.Contains('@') && e.Contains('.')
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Enter a valid email address.[/]")));

        AnsiConsole.WriteLine();
        using var api = new RelayApiClient();

        OtpRequestResult requestResult = OtpRequestResult.Error;
        await AnsiConsole.Status().StartAsync("Sending verification code...", async ctx =>
        {
            requestResult = await api.RequestOtpAsync(email, ct);
        });

        switch (requestResult)
        {
            case OtpRequestResult.RateLimited:
                AnsiConsole.MarkupLine("  [red]✗[/] Too many requests. Try again in a few minutes.");
                return null;
            case OtpRequestResult.NetworkError:
                AnsiConsole.MarkupLine("  [red]✗[/] Could not reach openmonoagent.ai. Check your connection.");
                return null;
            case OtpRequestResult.Error:
                AnsiConsole.MarkupLine("  [red]✗[/] Failed to send code. Try again.");
                return null;
        }

        AnsiConsole.MarkupLine($"  [green]✓[/] Code sent to [cyan]{Markup.Escape(email)}[/]");
        AnsiConsole.WriteLine();

        var otp = AnsiConsole.Prompt(
            new TextPrompt<string>("  Enter the code from your email: ")
                .Validate(o => o.Length >= 4
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Enter the full code.[/]")));

        AnsiConsole.WriteLine();

        RelayConfig? verifiedConfig = null;
        OtpVerifyError verifyError = OtpVerifyError.InvalidCode;
        await AnsiConsole.Status().StartAsync("Verifying...", async ctx =>
        {
            (verifiedConfig, verifyError) = await api.VerifyOtpAsync(email, otp, ct);
        });

        if (verifyError != OtpVerifyError.None)
        {
            var msg = verifyError switch
            {
                OtpVerifyError.MaxAttempts => "Too many incorrect attempts. Run the command again to get a new code.",
                OtpVerifyError.NetworkError => "Could not reach openmonoagent.ai. Check your connection.",
                OtpVerifyError.InvalidResponse => "Unexpected response from server. Contact support.",
                _ => "Invalid or expired code. Run the command again to get a new one.",
            };
            AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(msg)}");
            return null;
        }

        RelayConfigStore.Save(dataDirectory, verifiedConfig!);
        AnsiConsole.MarkupLine("  [green]✓[/] Verified. Relay credentials saved.");
        AnsiConsole.WriteLine();

        return verifiedConfig;
    }

    private static async Task<bool> InstallFrpcAsync(RelayConfig config, CancellationToken ct)
    {
        var (os, arch) = GetPlatform();
        if (os is null)
        {
            AnsiConsole.MarkupLine("  [red]✗[/] Unsupported platform. Supported: linux/amd64, linux/arm64, darwin/amd64, darwin/arm64.");
            return false;
        }

        var frpcBin = GetFrpcBinPath();
        var configPath = GetFrpcConfigPath();

        // Download frpc if not already installed
        if (!File.Exists(frpcBin))
        {
            AnsiConsole.MarkupLine($"  Downloading frpc v{FrpVersion}...");
            var downloaded = await DownloadFrpcAsync(os, arch, frpcBin, ct);
            if (!downloaded) return false;
            AnsiConsole.MarkupLine($"  [green]✓[/] Installed {frpcBin}");
        }
        else
        {
            AnsiConsole.MarkupLine($"  [dim]frpc already installed at {frpcBin}[/]");
        }

        // Write frpc.toml
        var toml = BuildFrpcToml(config);
        var wrote = await WriteFrpcConfigAsync(configPath, toml, ct);
        if (!wrote) return false;
        AnsiConsole.MarkupLine($"  [green]✓[/] Wrote {configPath}");

        // Daemon
        if (OperatingSystem.IsLinux())
            return await SetupSystemdAsync(frpcBin, configPath, ct);
        if (OperatingSystem.IsMacOS())
            return await SetupLaunchdAsync(frpcBin, configPath, ct);

        AnsiConsole.MarkupLine($"  [yellow]⚠[/] Daemon setup not supported on this platform. Start frpc manually:");
        AnsiConsole.MarkupLine($"  [dim]{frpcBin} -c {configPath}[/]");
        return true;
    }

    private static async Task<bool> DownloadFrpcAsync(string os, string arch, string frpcBin, CancellationToken ct)
    {
        var url = $"https://github.com/fatedier/frp/releases/download/v{FrpVersion}/frp_{FrpVersion}_{os}_{arch}.tar.gz";
        var tmp = Path.Combine(Path.GetTempPath(), $"frp_{FrpVersion}.tar.gz");
        var extractDir = Path.Combine(Path.GetTempPath(), $"frp_{FrpVersion}_extract");

        try
        {
            var (dlExit, _, dlErr) = await ProcessRunner.RunAsync(
                $"curl -fsSL \"{url}\" -o \"{tmp}\"", timeoutMs: 120_000, ct: ct);
            if (dlExit != 0)
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] Download failed: {Markup.Escape(dlErr)}");
                return false;
            }

            Directory.CreateDirectory(extractDir);
            var (tarExit, _, tarErr) = await ProcessRunner.RunAsync(
                $"tar xz -C \"{extractDir}\" --strip-components=1 -f \"{tmp}\"", ct: ct);
            if (tarExit != 0)
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] Extract failed: {Markup.Escape(tarErr)}");
                return false;
            }

            var frpcSrc = Path.Combine(extractDir, "frpc");
            Directory.CreateDirectory(Path.GetDirectoryName(frpcBin)!);

            int installExit;
            if (OperatingSystem.IsLinux())
                (installExit, _, _) = await RunPrivilegedAsync($"install -m 0755 \"{frpcSrc}\" \"{frpcBin}\"", ct);
            else
            {
                File.Copy(frpcSrc, frpcBin, overwrite: true);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(frpcBin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                installExit = 0;
            }

            return installExit == 0;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
            try { Directory.Delete(extractDir, recursive: true); } catch { }
        }
    }

    private static async Task<bool> WriteFrpcConfigAsync(string configPath, string toml, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            var tmp = Path.Combine(Path.GetTempPath(), "frpc.toml");
            await File.WriteAllTextAsync(tmp, toml, ct);
            var (mkdirExit, _, _) = await RunPrivilegedAsync($"mkdir -p \"{Path.GetDirectoryName(configPath)}\"", ct);
            var (cpExit, _, cpErr) = await RunPrivilegedAsync($"install -m 0600 \"{tmp}\" \"{configPath}\"", ct);
            File.Delete(tmp);
            if (cpExit != 0)
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] Could not write {configPath}: {Markup.Escape(cpErr)}");
                return false;
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            await File.WriteAllTextAsync(configPath, toml, ct);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return true;
    }

    private static async Task<bool> SetupSystemdAsync(string frpcBin, string configPath, CancellationToken ct)
    {
        var unit = $"""
            [Unit]
            Description=frp client (OpenMono.ai inference-box side)
            After=network.target docker.service
            Wants=docker.service

            [Service]
            Type=simple
            ExecStart={frpcBin} -c {configPath}
            Restart=on-failure
            RestartSec=10s

            [Install]
            WantedBy=multi-user.target
            """;

        var unitTmp = Path.Combine(Path.GetTempPath(), "frpc.service");
        await File.WriteAllTextAsync(unitTmp, unit, ct);

        await RunPrivilegedAsync($"install -m 0644 \"{unitTmp}\" /etc/systemd/system/frpc.service", ct);
        File.Delete(unitTmp);

        await RunPrivilegedAsync("systemctl daemon-reload", ct);
        var (enableExit, _, enableErr) = await RunPrivilegedAsync("systemctl enable --now frpc", ct);
        if (enableExit != 0)
        {
            AnsiConsole.MarkupLine($"  [red]✗[/] systemctl failed: {Markup.Escape(enableErr)}");
            AnsiConsole.MarkupLine("  [dim]Check: sudo journalctl -u frpc[/]");
            return false;
        }

        await Task.Delay(2000, ct);
        var (activeExit, _, _) = await RunPrivilegedAsync("systemctl is-active --quiet frpc", ct);
        if (activeExit != 0)
        {
            AnsiConsole.MarkupLine("  [red]✗[/] frpc failed to start. Check: sudo journalctl -u frpc");
            return false;
        }

        AnsiConsole.MarkupLine("  [green]✓[/] frpc running via systemd");
        return true;
    }

    private static async Task<bool> SetupLaunchdAsync(string frpcBin, string configPath, CancellationToken ct)
    {
        const string label = "ai.openmonoagent.frpc";
        var plistDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents");
        var plistPath = Path.Combine(plistDir, $"{label}.plist");

        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{label}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{frpcBin}</string>
                    <string>-c</string>
                    <string>{configPath}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <true/>
                <key>StandardOutPath</key>
                <string>{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openmono", "frpc.log")}</string>
                <key>StandardErrorPath</key>
                <string>{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openmono", "frpc.log")}</string>
            </dict>
            </plist>
            """;

        Directory.CreateDirectory(plistDir);
        await File.WriteAllTextAsync(plistPath, plist, ct);

        // Unload existing agent if running then reload
        await ProcessRunner.RunAsync($"launchctl unload \"{plistPath}\" 2>/dev/null || true", ct: ct);
        var (loadExit, _, loadErr) = await ProcessRunner.RunAsync($"launchctl load \"{plistPath}\"", ct: ct);
        if (loadExit != 0)
        {
            AnsiConsole.MarkupLine($"  [red]✗[/] launchctl failed: {Markup.Escape(loadErr)}");
            return false;
        }

        AnsiConsole.MarkupLine("  [green]✓[/] frpc running via launchd");
        return true;
    }

    private static string BuildFrpcToml(RelayConfig config) => $"""
        # frp client — OpenMono.ai inference-box side
        # Generated by openmono agent tunnel --inference on {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}

        serverAddr = "{config.FrpsAddress}"
        serverPort = {config.FrpsPort}

        metadatas.token = "{config.RelayToken}"

        transport.tls.enable = true

        log.to    = "console"
        log.level = "info"

        [[proxies]]
        name              = "{config.ProxyPrefix}llama"
        type              = "tcp"
        localIP           = "127.0.0.1"
        localPort         = 7474
        remotePort        = {config.RemotePort}
        metadatas.token   = "{config.RelayToken}"
        """;

    private static string GetFrpcBinPath()
    {
        if (OperatingSystem.IsLinux())
            return "/usr/local/bin/frpc";

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openmono", "bin", "frpc");
    }

    private static string GetFrpcConfigPath()
    {
        if (OperatingSystem.IsLinux())
            return "/etc/frp/frpc.toml";

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "frp", "frpc.toml");
    }

    private static (string? Os, string Arch) GetPlatform()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };

        if (arch is null) return (null, "");

        if (OperatingSystem.IsLinux()) return ("linux", arch);
        if (OperatingSystem.IsMacOS()) return ("darwin", arch);
        return (null, arch);
    }

    // Runs a command without redirecting stdio so sudo can prompt for password.
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPrivilegedAsync(
        string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { "-c", $"sudo {command}" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, stdout.TrimEnd(), stderr.TrimEnd());
    }

    private static void PrintBanner(RelayConfig config)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/]");
        AnsiConsole.MarkupLine($"  [green]✓[/] frp tunnel connected to [cyan]{Markup.Escape(config.FrpsAddress)}:{config.FrpsPort}[/]");
        AnsiConsole.MarkupLine($"  [green]✓[/] Public inference endpoint: [cyan]http://{Markup.Escape(config.FrpsAddress)}:{config.RemotePort}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [bold]On the agent box, run:[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  [dim]openmono --endpoint http://{Markup.Escape(config.FrpsAddress)}:{config.RemotePort}[/]");
        AnsiConsole.WriteLine();
        if (OperatingSystem.IsLinux())
        {
            AnsiConsole.MarkupLine("  [dim]To check tunnel status:  sudo systemctl status frpc[/]");
            AnsiConsole.MarkupLine("  [dim]To tail tunnel logs:      sudo journalctl -u frpc -f[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"  [dim]To tail tunnel logs:  tail -f ~/.openmono/frpc.log[/]");
        }
        AnsiConsole.MarkupLine("[dim]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/]");
        AnsiConsole.WriteLine();
    }
}
