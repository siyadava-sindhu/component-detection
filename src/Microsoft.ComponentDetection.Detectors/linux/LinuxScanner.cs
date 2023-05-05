namespace Microsoft.ComponentDetection.Detectors.Linux;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ComponentDetection.Common.Telemetry.Records;
using Microsoft.ComponentDetection.Contracts;
using Microsoft.ComponentDetection.Contracts.BcdeModels;
using Microsoft.ComponentDetection.Contracts.TypedComponent;
using Microsoft.ComponentDetection.Detectors.Linux.Contracts;
using Microsoft.Extensions.Logging;

public class LinuxScanner : ILinuxScanner
{
    private const string ScannerImage = "governancecontainerregistry.azurecr.io/syft:v0.74.0@sha256:5b186241c12047572d573116e6ff9305c83b2bb178d2e4ca556165e7f918c3dd";

    private static readonly IList<string> CmdParameters = new List<string>
    {
        "--quiet", "--scope", "all-layers", "--output", "json",
    };

    private static readonly IEnumerable<string> AllowedArtifactTypes = new[] { "apk", "deb", "rpm" };

    private static readonly SemaphoreSlim DockerSemaphore = new(2);

    private static readonly int SemaphoreTimeout = Convert.ToInt32(TimeSpan.FromHours(1).TotalMilliseconds);

    private readonly IDockerService dockerService;
    private readonly ILogger<LinuxScanner> logger;

    public LinuxScanner(IDockerService dockerService, ILogger<LinuxScanner> logger)
    {
        this.dockerService = dockerService;
        this.logger = logger;
    }

    public async Task<IEnumerable<LayerMappedLinuxComponents>> ScanLinuxAsync(string imageHash, IEnumerable<DockerLayer> dockerLayers, int baseImageLayerCount, CancellationToken cancellationToken = default)
    {
        using var record = new LinuxScannerTelemetryRecord
        {
            ImageToScan = imageHash,
            ScannerVersion = ScannerImage,
        };

        var acquired = false;
        var stdout = string.Empty;
        var stderr = string.Empty;

        using var syftTelemetryRecord = new LinuxScannerSyftTelemetryRecord();

        try
        {
            acquired = await DockerSemaphore.WaitAsync(SemaphoreTimeout, cancellationToken);
            if (acquired)
            {
                try
                {
                    var command = new List<string> { imageHash }.Concat(CmdParameters).ToList();
                    (stdout, stderr) = await this.dockerService.CreateAndRunContainerAsync(ScannerImage, command, cancellationToken);
                }
                catch (Exception e)
                {
                    syftTelemetryRecord.Exception = JsonSerializer.Serialize(e);
                    this.logger.LogError(e, "Failed to run syft");
                    throw;
                }
            }
            else
            {
                record.SemaphoreFailure = true;
                this.logger.LogWarning("Failed to enter the docker semaphore for image {ImageHash}", imageHash);
            }
        }
        finally
        {
            if (acquired)
            {
                DockerSemaphore.Release();
            }
        }

        record.ScanStdErr = stderr;
        record.ScanStdOut = stdout;

        if (string.IsNullOrWhiteSpace(stdout) || !string.IsNullOrWhiteSpace(stderr))
        {
            throw new InvalidOperationException(
                $"Scan failed with exit info: {stdout}{Environment.NewLine}{stderr}");
        }

        var layerDictionary = dockerLayers
            .DistinctBy(layer => layer.DiffId)
            .ToDictionary(
                layer => layer.DiffId,
                _ => new List<LinuxComponent>());

        try
        {
            var syftOutput = JsonSerializer.Deserialize<SyftOutput>(stdout, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            var linuxComponentsWithLayers = syftOutput.Artifacts
                .DistinctBy(artifact => (artifact.Name, artifact.Version))
                .Where(artifact => AllowedArtifactTypes.Contains(artifact.Type))
                .Select(artifact =>
                    (Component: new LinuxComponent(syftOutput.Distro.Id, syftOutput.Distro.VersionId, artifact.Name, artifact.Version), layerIds: artifact.Locations.Select(location => location.LayerId).Distinct()));

            foreach (var (component, layers) in linuxComponentsWithLayers)
            {
                layers.ToList().ForEach(layer => layerDictionary[layer].Add(component));
            }

            var layerMappedLinuxComponents = layerDictionary.Select(kvp =>
            {
                (var layerId, var components) = kvp;
                return new LayerMappedLinuxComponents
                {
                    LinuxComponents = components,
                    DockerLayer = dockerLayers.First(layer => layer.DiffId == layerId),
                };
            });

            syftTelemetryRecord.LinuxComponents = JsonSerializer.Serialize(linuxComponentsWithLayers.Select(linuxComponentWithLayer =>
                new LinuxComponentRecord
                {
                    Name = linuxComponentWithLayer.Component.Name,
                    Version = linuxComponentWithLayer.Component.Version,
                }));

            return layerMappedLinuxComponents;
        }
        catch (Exception e)
        {
            record.FailedDeserializingScannerOutput = e.ToString();
            return null;
        }
    }

    internal sealed class LinuxComponentRecord
    {
        public string Name { get; set; }

        public string Version { get; set; }
    }
}
