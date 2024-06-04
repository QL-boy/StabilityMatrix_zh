﻿using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Extensions;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Helper.Cache;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Progress;
using StabilityMatrix.Core.Processes;
using StabilityMatrix.Core.Python;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Core.Models.Packages;

[Singleton(typeof(BasePackage))]
public class Sdfx(
    IGithubApiCache githubApi,
    ISettingsManager settingsManager,
    IDownloadService downloadService,
    IPrerequisiteHelper prerequisiteHelper
) : BaseGitPackage(githubApi, settingsManager, downloadService, prerequisiteHelper)
{
    public override string Name => "sdfx";
    public override string DisplayName { get; set; } = "SDFX";
    public override string Author => "sdfxai";
    public override string Blurb =>
        "The ultimate no-code platform to build and share AI apps with beautiful UI.";
    public override string LicenseType => "AGPL-3.0";
    public override string LicenseUrl => "https://github.com/sdfxai/sdfx/blob/main/LICENSE";
    public override string LaunchCommand => "setup.py";
    public override Uri PreviewImageUri =>
        new("https://github.com/sdfxai/sdfx/raw/main/docs/static/screen-sdfx.png");
    public override string OutputFolderName => Path.Combine("data", "media", "output");

    public override IEnumerable<TorchVersion> AvailableTorchVersions =>
        [TorchVersion.Cpu, TorchVersion.Cuda, TorchVersion.DirectMl, TorchVersion.Rocm, TorchVersion.Mps];

    public override PackageDifficulty InstallerSortOrder => PackageDifficulty.Expert;
    public override SharedFolderMethod RecommendedSharedFolderMethod => SharedFolderMethod.Configuration;
    public override List<LaunchOptionDefinition> LaunchOptions => [];
    public override Dictionary<SharedFolderType, IReadOnlyList<string>> SharedFolders =>
        new()
        {
            [SharedFolderType.StableDiffusion] = new[] { "data/models/checkpoints" },
            [SharedFolderType.Diffusers] = new[] { "data/models/diffusers" },
            [SharedFolderType.Lora] = new[] { "data/models/loras" },
            [SharedFolderType.CLIP] = new[] { "data/models/clip" },
            [SharedFolderType.InvokeClipVision] = new[] { "data/models/clip_vision" },
            [SharedFolderType.TextualInversion] = new[] { "data/models/embeddings" },
            [SharedFolderType.VAE] = new[] { "data/models/vae" },
            [SharedFolderType.ApproxVAE] = new[] { "data/models/vae_approx" },
            [SharedFolderType.ControlNet] = new[] { "data/models/controlnet/ControlNet" },
            [SharedFolderType.GLIGEN] = new[] { "data/models/gligen" },
            [SharedFolderType.ESRGAN] = new[] { "data/models/upscale_models" },
            [SharedFolderType.Hypernetwork] = new[] { "data/models/hypernetworks" },
            [SharedFolderType.IpAdapter] = new[] { "data/models/ipadapter/base" },
            [SharedFolderType.InvokeIpAdapters15] = new[] { "data/models/ipadapter/sd15" },
            [SharedFolderType.InvokeIpAdaptersXl] = new[] { "data/models/ipadapter/sdxl" },
            [SharedFolderType.T2IAdapter] = new[] { "data/models/controlnet/T2IAdapter" },
            [SharedFolderType.PromptExpansion] = new[] { "data/models/prompt_expansion" }
        };
    public override Dictionary<SharedOutputType, IReadOnlyList<string>>? SharedOutputFolders =>
        new() { [SharedOutputType.Text2Img] = new[] { "data/media/output" } };
    public override string MainBranch => "main";
    public override bool ShouldIgnoreReleases => true;

    public override IEnumerable<PackagePrerequisite> Prerequisites =>
        [
            PackagePrerequisite.Python310,
            PackagePrerequisite.VcRedist,
            PackagePrerequisite.Git,
            PackagePrerequisite.Node
        ];

    public override async Task InstallPackage(
        string installLocation,
        TorchVersion torchVersion,
        SharedFolderMethod selectedSharedFolderMethod,
        DownloadPackageVersionOptions versionOptions,
        IProgress<ProgressReport>? progress = null,
        Action<ProcessOutput>? onConsoleOutput = null
    )
    {
        progress?.Report(new ProgressReport(-1, "Setting up venv", isIndeterminate: true));
        // Setup venv
        await using var venvRunner = new PyVenvRunner(Path.Combine(installLocation, "venv"));
        venvRunner.WorkingDirectory = installLocation;
        venvRunner.EnvironmentVariables = GetEnvVars(venvRunner, installLocation);

        await venvRunner.Setup(true, onConsoleOutput).ConfigureAwait(false);

        progress?.Report(
            new ProgressReport(-1f, "Installing Package Requirements...", isIndeterminate: true)
        );

        var gpuArg = torchVersion switch
        {
            TorchVersion.Cuda => "--nvidia",
            TorchVersion.Rocm => "--amd",
            TorchVersion.DirectMl => "--directml",
            TorchVersion.Cpu => "--cpu",
            TorchVersion.Mps => "--mac",
            _ => throw new ArgumentOutOfRangeException(nameof(torchVersion), torchVersion, null)
        };

        await venvRunner
            .CustomInstall(["setup.py", "--install", gpuArg], onConsoleOutput)
            .ConfigureAwait(false);

        progress?.Report(new ProgressReport(1, "Installed Package Requirements", isIndeterminate: false));
    }

    public override async Task RunPackage(
        string installedPackagePath,
        string command,
        string arguments,
        Action<ProcessOutput>? onConsoleOutput
    )
    {
        var venvRunner = await SetupVenv(installedPackagePath).ConfigureAwait(false);
        venvRunner.EnvironmentVariables = GetEnvVars(venvRunner, installedPackagePath);

        void HandleConsoleOutput(ProcessOutput s)
        {
            onConsoleOutput?.Invoke(s);

            if (!s.Text.Contains("Running on", StringComparison.OrdinalIgnoreCase))
                return;

            var regex = new Regex(@"(https?:\/\/)([^:\s]+):(\d+)");
            var match = regex.Match(s.Text);
            if (!match.Success)
                return;

            WebUrl = match.Value;
            OnStartupComplete(WebUrl);
        }

        var args = $"\"{Path.Combine(installedPackagePath, command)}\" --run {arguments}";

        venvRunner.RunDetached(args.TrimEnd(), HandleConsoleOutput, OnExit);
    }

    private Dictionary<string, string> GetEnvVars(PyVenvRunner venvRunner, DirectoryPath installPath)
    {
        var env = new Dictionary<string, string>();
        env.Update(venvRunner.EnvironmentVariables ?? SettingsManager.Settings.EnvironmentVariables);
        env["VIRTUAL_ENV"] = venvRunner.RootPath;

        var pathBuilder = new EnvPathBuilder();

        if (env.TryGetValue("PATH", out var value))
        {
            pathBuilder.AddPath(value);
        }

        if (Compat.IsWindows)
        {
            pathBuilder.AddPath(Environment.GetFolderPath(Environment.SpecialFolder.System));
        }
        else
        {
            var existingPath = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(existingPath))
            {
                pathBuilder.AddPath(existingPath);
            }
            pathBuilder.AddPath(Path.Combine(SettingsManager.LibraryDir, "Assets", "nodejs", "bin"));
        }

        pathBuilder
            .AddPath(Path.Combine(SettingsManager.LibraryDir, "Assets", "nodejs"))
            .AddPath(Path.Combine(installPath, "src", "node_modules", ".bin"));

        env["PATH"] = pathBuilder.ToString();

        return env;
    }

    public override Task SetupModelFolders(
        DirectoryPath installDirectory,
        SharedFolderMethod sharedFolderMethod
    ) =>
        sharedFolderMethod switch
        {
            SharedFolderMethod.Symlink
                => base.SetupModelFolders(installDirectory, SharedFolderMethod.Symlink),
            SharedFolderMethod.Configuration => SetupModelFoldersConfig(installDirectory),
            SharedFolderMethod.None => Task.CompletedTask,
            _ => throw new ArgumentOutOfRangeException(nameof(sharedFolderMethod), sharedFolderMethod, null)
        };

    public override Task RemoveModelFolderLinks(
        DirectoryPath installDirectory,
        SharedFolderMethod sharedFolderMethod
    )
    {
        return sharedFolderMethod switch
        {
            SharedFolderMethod.Symlink => base.RemoveModelFolderLinks(installDirectory, sharedFolderMethod),
            SharedFolderMethod.Configuration => RemoveConfigSection(installDirectory),
            SharedFolderMethod.None => Task.CompletedTask,
            _ => throw new ArgumentOutOfRangeException(nameof(sharedFolderMethod), sharedFolderMethod, null)
        };
    }

    private async Task SetupModelFoldersConfig(DirectoryPath installDirectory)
    {
        var configPath = Path.Combine(installDirectory, "sdfx.config.json");

        if (File.Exists(configPath))
        {
            var configText = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
            var config = JsonSerializer.Deserialize<JsonObject>(configText) ?? new JsonObject();
            var modelsDir = settingsManager.ModelsDirectory;

            var paths = config["paths"] as JsonObject ?? new JsonObject();

            if (!paths.ContainsKey("models"))
            {
                paths["models"] = new JsonObject();
            }

            paths["models"]["checkpoints"] = new JsonArray(Path.Combine(modelsDir, "StableDiffusion"));
            paths["models"]["vae"] = new JsonArray(Path.Combine(modelsDir, "VAE"));
            paths["models"]["loras"] = new JsonArray(
                Path.Combine(modelsDir, "Lora"),
                Path.Combine(modelsDir, "LyCORIS")
            );
            paths["models"]["upscale_models"] = new JsonArray(
                Path.Combine(modelsDir, "ESRGAN"),
                Path.Combine(modelsDir, "RealESRGAN"),
                Path.Combine(modelsDir, "SwinIR")
            );
            paths["models"]["embeddings"] = new JsonArray(Path.Combine(modelsDir, "TextualInversion"));
            paths["models"]["hypernetworks"] = new JsonArray(Path.Combine(modelsDir, "Hypernetwork"));
            paths["models"]["controlnet"] = new JsonArray(
                Path.Combine(modelsDir, "ControlNet"),
                Path.Combine(modelsDir, "T2IAdapter")
            );
            paths["models"]["clip"] = new JsonArray(Path.Combine(modelsDir, "CLIP"));
            paths["models"]["clip_vision"] = new JsonArray(Path.Combine(modelsDir, "InvokeClipVision"));
            paths["models"]["diffusers"] = new JsonArray(Path.Combine(modelsDir, "Diffusers"));
            paths["models"]["gligen"] = new JsonArray(Path.Combine(modelsDir, "GLIGEN"));
            paths["models"]["vae_approx"] = new JsonArray(Path.Combine(modelsDir, "ApproxVAE"));
            paths["models"]["ipadapter"] = new JsonArray(
                Path.Combine(modelsDir, "IpAdapter"),
                Path.Combine(modelsDir, "InvokeIpAdapters15"),
                Path.Combine(modelsDir, "InvokeIpAdaptersXl")
            );

            await File.WriteAllTextAsync(configPath, config.ToString()).ConfigureAwait(false);
        }
    }

    private async Task RemoveConfigSection(DirectoryPath installDirectory)
    {
        var configPath = Path.Combine(installDirectory, "sdfx.config.json");

        if (File.Exists(configPath))
        {
            var configText = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
            var config = JsonSerializer.Deserialize<JsonObject>(configText) ?? new JsonObject();

            var paths = config["paths"] as JsonObject ?? new JsonObject();

            if (!paths.ContainsKey("models"))
            {
                paths["models"] = new JsonObject();
            }

            paths["models"]["checkpoints"] = new JsonArray(Path.Combine("data", "models", "checkpoints"));
            paths["models"]["clip"] = new JsonArray(Path.Combine("data", "models", "clip"));
            paths["models"]["clip_vision"] = new JsonArray(Path.Combine("data", "models", "clip_vision"));
            paths["models"]["controlnet"] = new JsonArray(Path.Combine("data", "models", "controlnet"));
            paths["models"]["diffusers"] = new JsonArray(Path.Combine("data", "models", "diffusers"));
            paths["models"]["embeddings"] = new JsonArray(Path.Combine("data", "models", "embeddings"));
            paths["models"]["gligen"] = new JsonArray(Path.Combine("data", "models", "gligen"));
            paths["models"]["ipadapter"] = new JsonArray(Path.Combine("data", "models", "ipadapter"));
            paths["models"]["hypernetworks"] = new JsonArray(Path.Combine("data", "models", "hypernetworks"));
            paths["models"]["loras"] = new JsonArray(Path.Combine("data", "models", "loras"));
            paths["models"]["upscale_models"] = new JsonArray(
                Path.Combine("data", "models", "upscale_models")
            );
            paths["models"]["vae"] = new JsonArray(Path.Combine("data", "models", "vae"));
            paths["models"]["vae_approx"] = new JsonArray(Path.Combine("data", "models", "vae_approx"));

            await File.WriteAllTextAsync(configPath, config.ToString()).ConfigureAwait(false);
        }
    }
}
