using System.Runtime.CompilerServices;

using Aspire.Dashboard.Model;
using Aspire.ResourceService.Proto.V1;

using Docker.DotNet;
using Docker.DotNet.Models;

using Google.Protobuf.WellKnownTypes;

namespace Aspire.ResourceService.Standalone.Server.ResourceProviders;

internal sealed partial class DockerResourceProvider : IResourceProvider
{
    private readonly IDockerClient _dockerClient;
    private readonly SemaphoreSlim _syncRoot = new(1);
    private readonly List<ContainerListResponse> _dockerContainers = [];

    public DockerResourceProvider(IDockerClient dockerClient)
    {
        _dockerClient = dockerClient;
    }

    public async Task<List<Resource>> GetResourcesAsync()
    {
        var containers = await GetContainers().ConfigureAwait(false);

        List<Resource> resources = [];
        foreach (var container in containers)
        {
            var ar = new Resource
            {
                CreatedAt = Timestamp.FromDateTime(container.Created),
                State = container.State,
                DisplayName = container.Names.First(),
                ResourceType = KnownResourceTypes.Container,
                Name = string.Join('|', container.Names),
                Uid = container.ID
            };

            ar.Urls.Add(container.Ports.Where(p => !string.IsNullOrEmpty(p.IP))
                .Select(s => new Url
                {
                    IsInternal = false,
                    Name = $"http://{s.IP}:{s.PublicPort}",
                    FullUrl = $"http://{s.IP}:{s.PublicPort}"
                }));

            resources.Add(ar);
        }

        return resources;
    }

    public async IAsyncEnumerable<string> GerResourceLogs(string resourceName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var containers = await GetContainers().ConfigureAwait(false);

        var container = containers.Single(c => c.Names.Contains(resourceName));

        using var stream = await _dockerClient.Containers
            .GetContainerLogsAsync(container.ID, false, new ContainerLogsParameters()
            {
                ShowStdout = true,
                ShowStderr = true
            }, cancellationToken)
            .ConfigureAwait(false);

        var (output, error) = await stream.ReadOutputToEndAsync(cancellationToken).ConfigureAwait(false);

        var lines = output.Split(Environment.NewLine).Union(error.Split(Environment.NewLine));

        foreach (var line in lines)
        {
            yield return line;
        }

    }

    private async ValueTask<List<ContainerListResponse>> GetContainers()
    {
        if (_dockerContainers.Count != 0)
        {
            return _dockerContainers;
        }

        try
        {
            await _syncRoot.WaitAsync();
            var c = await _dockerClient.Containers
                .ListContainersAsync(new ContainersListParameters())
                .ConfigureAwait(false);

            _dockerContainers.AddRange(c);

            return _dockerContainers;
        }

        finally
        {
            _syncRoot.Release();
        }

    }
}
