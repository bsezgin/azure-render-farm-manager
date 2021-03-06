﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Management.Compute;
using Microsoft.Azure.Management.Network;
using Microsoft.Azure.Management.ResourceManager;
using Microsoft.Azure.Management.ResourceManager.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Rest.Azure;
using WebApp.Arm;
using WebApp.BackgroundHosts.Deployment;
using WebApp.Code.Contract;
using WebApp.Config.Storage;
using WebApp.Identity;
using WebApp.Models.Storage.Create;
using WebApp.Operations;
using WebApp.Providers.Templates;

namespace WebApp.Config.Coordinators
{
    public class AssetRepoCoordinator : IAssetRepoCoordinator
    {
        private readonly IConfigRepository<AssetRepository> _configCoordinator;
        private readonly ITemplateProvider _templateProvider;
        private readonly IIdentityProvider _identityProvider;
        private readonly IDeploymentQueue _deploymentQueue;
        private readonly ILogger _logger;

        public AssetRepoCoordinator(
            IConfigRepository<AssetRepository> configCoordinator,
            ITemplateProvider templateProvider,
            IIdentityProvider identityProvider,
            IDeploymentQueue deploymentQueue,
            ILogger<AssetRepoCoordinator> logger)
        {
            _configCoordinator = configCoordinator;
            _templateProvider = templateProvider;
            _identityProvider = identityProvider;
            _deploymentQueue = deploymentQueue;
            _logger = logger;
        }

        public async Task<List<string>> ListRepositories()
        {
            return await _configCoordinator.List();
        }

        public async Task<AssetRepository> GetRepository(string repoName)
        {
            return await _configCoordinator.Get(repoName);
        }

        public AssetRepository CreateRepository(AddAssetRepoBaseModel model)
        {
            switch (model.RepositoryType)
            {
                case AssetRepositoryType.AvereCluster:
                    return new AvereCluster { Name = model.RepositoryName, InProgress = true };

                case AssetRepositoryType.NfsFileServer:
                    return new NfsFileServer { Name = model.RepositoryName, InProgress = true };

                default:
                    throw new NotSupportedException("Unknown type of repository selected");
            }
        }

        public async Task UpdateRepository(AssetRepository repository, string originalName = null)
        {
            await _configCoordinator.Update(repository, repository.Name, originalName);
        }

        public async Task<bool> RemoveRepository(AssetRepository repository)
        {
            return await _configCoordinator.Remove(repository.Name);
        }

        //
        // Deployment operations
        //
        public async Task BeginRepositoryDeploymentAsync(AssetRepository repository, IManagementClientProvider managementClientProvider, IAzureResourceProvider azureResourceProvider)
        {
            using (var client = await managementClientProvider.CreateResourceManagementClient(repository.SubscriptionId))
            {
                await client.ResourceGroups.CreateOrUpdateAsync(
                    repository.ResourceGroupName,
                    new ResourceGroup(
                        repository.Subnet.Location, // The subnet location pins us to a region
                        tags: AzureResourceProvider.GetEnvironmentTags(repository.EnvironmentName)));

                await azureResourceProvider.AssignManagementIdentityAsync(
                    repository.SubscriptionId,
                    repository.ResourceGroupResourceId,
                    AzureResourceProvider.ContributorRole,
                    _identityProvider.GetPortalManagedServiceIdentity());

                repository.DeploymentName = "FileServerDeployment";
                await UpdateRepository(repository);

                await DeployFileServer(repository as NfsFileServer, managementClientProvider);
            }
        }

        public async Task<ProvisioningState> UpdateRepositoryFromDeploymentAsync(
            AssetRepository repository,
            IManagementClientProvider managementClientProvider)
        {
            var deployment = await GetDeploymentAsync(repository, managementClientProvider);
            if (deployment == null)
            {
                return ProvisioningState.Failed;
            }

            var fileServer = repository as NfsFileServer;
            if (fileServer == null)
            {
                return ProvisioningState.Failed;
            }

            string privateIp = null;
            string publicIp = null;

            Enum.TryParse<ProvisioningState>(deployment.Properties.ProvisioningState, out var deploymentState);
            if (deploymentState == ProvisioningState.Succeeded)
            {
                (privateIp, publicIp) = await GetIpAddressesAsync(fileServer, managementClientProvider);
            }

            if (deploymentState == ProvisioningState.Succeeded || deploymentState == ProvisioningState.Failed)
            {
                fileServer.ProvisioningState = deploymentState;
                fileServer.PrivateIp = privateIp;
                fileServer.PublicIp = publicIp;
                await UpdateRepository(fileServer);
            }

            return deploymentState;
        }


        public async Task BeginDeleteRepositoryAsync(AssetRepository repository, IManagementClientProvider managementClientProvider)
        {
            repository.ProvisioningState = ProvisioningState.Deleting;
            await UpdateRepository(repository);
            await _deploymentQueue.Add(new ActiveDeployment
            {
                FileServerName = repository.Name,
                StartTime = DateTime.UtcNow,
                Action = "DeleteVM",
            });
        }

        public async Task DeleteRepositoryResourcesAsync(AssetRepository repository, IManagementClientProvider managementClientProvider)
        {
            var fileServer = repository as NfsFileServer;
            if (fileServer == null)
            {
                return;
            }

            using (var resourceClient = await managementClientProvider.CreateResourceManagementClient(repository.SubscriptionId))
            using (var computeClient = await managementClientProvider.CreateComputeManagementClient(repository.SubscriptionId))
            using (var networkClient = await managementClientProvider.CreateNetworkManagementClient(repository.SubscriptionId))
            {
                try
                {
                    var virtualMachine = await computeClient.VirtualMachines.GetAsync(fileServer.ResourceGroupName, fileServer.VmName);

                    var nicName = virtualMachine.NetworkProfile.NetworkInterfaces[0].Id.Split("/").Last(); ;
                    var avSetName = virtualMachine.AvailabilitySet.Id?.Split("/").Last();
                    var osDisk = virtualMachine.StorageProfile.OsDisk.ManagedDisk.Id.Split("/").Last();
                    var dataDisks = virtualMachine.StorageProfile.DataDisks.Select(dd => dd.ManagedDisk.Id.Split("/").Last()).ToList();

                    string pip = null;
                    string nsg = null;
                    try
                    {
                        var nic = await networkClient.NetworkInterfaces.GetAsync(fileServer.ResourceGroupName, nicName);
                        pip = nic.IpConfigurations[0].PublicIPAddress?.Id.Split("/").Last();
                        nsg = nic.NetworkSecurityGroup?.Id.Split("/").Last();
                    }
                    catch (CloudException ex) when (ResourceNotFound(ex))
                    {
                        // NIC doesn't exist
                    }

                    await IgnoreNotFound(async () =>
                    {
                        await computeClient.VirtualMachines.GetAsync(fileServer.ResourceGroupName, fileServer.VmName);
                        await computeClient.VirtualMachines.DeleteAsync(fileServer.ResourceGroupName, fileServer.VmName);
                    });

                    if (nicName != null)
                    {
                        await IgnoreNotFound(() => networkClient.NetworkInterfaces.DeleteAsync(fileServer.ResourceGroupName, nicName));
                    }

                    var tasks = new List<Task>();

                    if (nsg == "nsg")
                    {
                        tasks.Add(IgnoreNotFound(() => networkClient.NetworkSecurityGroups.DeleteAsync(fileServer.ResourceGroupName, nsg)));
                    }

                    if (pip != null)
                    {
                        tasks.Add(IgnoreNotFound(() => networkClient.PublicIPAddresses.DeleteAsync(fileServer.ResourceGroupName, pip)));
                    }

                    tasks.Add(IgnoreNotFound(() => computeClient.Disks.DeleteAsync(fileServer.ResourceGroupName, osDisk)));

                    tasks.AddRange(dataDisks.Select(
                        dd => IgnoreNotFound(() => computeClient.Disks.DeleteAsync(fileServer.ResourceGroupName, dd))));

                    await Task.WhenAll(tasks);

                    if (avSetName != null)
                    {
                        await IgnoreNotFound(() => computeClient.AvailabilitySets.DeleteAsync(fileServer.ResourceGroupName, avSetName));
                    }
                }
                catch (CloudException ex) when (ResourceNotFound(ex))
                {
                    // VM doesn't exist
                }

                try
                {
                    await resourceClient.ResourceGroups.GetAsync(fileServer.ResourceGroupName);

                    var resources = await resourceClient.Resources.ListByResourceGroupAsync(fileServer.ResourceGroupName);
                    if (resources.Any())
                    {
                        _logger.LogDebug($"Skipping resource group deletion as it contains the following resources: {string.Join(", ", resources.Select(r => r.Id))}");
                    }
                    else
                    {
                        await resourceClient.ResourceGroups.DeleteAsync(fileServer.ResourceGroupName);
                    }
                }
                catch (CloudException ex) when (ResourceNotFound(ex))
                {
                    // RG doesn't exist
                }

                await RemoveRepository(repository);
            }
        }

        private async Task<(string privateIp, string publicIp)> GetIpAddressesAsync(NfsFileServer fileServer, IManagementClientProvider managementClientProvider)
        {
            using (var computeClient = await managementClientProvider.CreateComputeManagementClient(fileServer.SubscriptionId))
            using (var networkClient = await managementClientProvider.CreateNetworkManagementClient(fileServer.SubscriptionId))
            {
                var vm = await computeClient.VirtualMachines.GetAsync(fileServer.ResourceGroupName, fileServer.VmName);
                var networkIfaceName = vm.NetworkProfile.NetworkInterfaces.First().Id.Split("/").Last();
                var net = await networkClient.NetworkInterfaces.GetAsync(fileServer.ResourceGroupName, networkIfaceName);
                var firstConfiguration = net.IpConfigurations.First();

                var privateIp = firstConfiguration.PrivateIPAddress;
                var publicIpId = firstConfiguration.PublicIPAddress?.Id;
                var publicIp =
                    publicIpId != null
                        ? await networkClient.PublicIPAddresses.GetAsync(
                            fileServer.ResourceGroupName,
                            publicIpId.Split("/").Last())
                        : null;

                return (privateIp, publicIp?.IpAddress);
            }
        }

        private async Task<DeploymentExtended> GetDeploymentAsync(AssetRepository assetRepo, IManagementClientProvider managementClientProvider)
        {
            using (var resourceClient = await managementClientProvider.CreateResourceManagementClient(assetRepo.SubscriptionId))
            {
                try
                {
                    return await resourceClient.Deployments.GetAsync(
                        assetRepo.ResourceGroupName,
                        assetRepo.DeploymentName);
                }
                catch (CloudException e)
                {
                    if (ResourceNotFound(e))
                    {
                        return null;
                    }

                    throw;
                }
            }
        }

        private async Task DeployFileServer(NfsFileServer repository, IManagementClientProvider managementClientProvider)
        {
            try
            {
                using (var client = await managementClientProvider.CreateResourceManagementClient(repository.SubscriptionId))
                {
                    await client.ResourceGroups.CreateOrUpdateAsync(repository.ResourceGroupName,
                        new ResourceGroup { Location = repository.Subnet.Location });

                    var templateParams = GetTemplateParameters(repository);

                    var properties = new Deployment
                    {
                        Properties = new DeploymentProperties
                        {
                            Template = await _templateProvider.GetTemplate("linux-file-server.json"),
                            Parameters = _templateProvider.GetParameters(templateParams),
                            Mode = DeploymentMode.Incremental
                        }
                    };

                    // Start the ARM deployment
                    await client.Deployments.BeginCreateOrUpdateAsync(
                        repository.ResourceGroupName,
                        repository.DeploymentName,
                        properties);

                    // Queue a request for the background host to monitor the deployment
                    // and update the state and IP address when it's done.
                    await _deploymentQueue.Add(new ActiveDeployment
                    {
                        FileServerName = repository.Name,
                        StartTime = DateTime.UtcNow,
                    });

                    repository.ProvisioningState = ProvisioningState.Running;
                    repository.InProgress = false;

                    await UpdateRepository(repository);
                }
            }
            catch (CloudException ex)
            {

                _logger.LogError(ex, $"Failed to deploy NFS server: {ex.Message}.");
                throw;
            }
        }

        private Dictionary<string, object> GetTemplateParameters(NfsFileServer repository)
        {
            var fileShare = repository.FileShares.FirstOrDefault();
            return new Dictionary<string, object>
            {
                {"environmentTag", repository.EnvironmentName ?? "Global"},
                {"vmName", repository.VmName},
                {"adminUserName", repository.Username},
                {"adminPassword", repository.Password},
                {"vmSize", repository.VmSize},
                {"subnetResourceId", repository.Subnet.ResourceId},
                {"sharesToExport", fileShare?.Name ?? ""},
                {"subnetAddressPrefix", string.Join(",", repository.AllowedNetworks)},
            };
        }

        private static async Task IgnoreNotFound(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (CloudException e)
            {
                if (!ResourceNotFound(e))
                {
                    throw;
                }
            }
        }

        private static bool ResourceNotFound(CloudException ce)
        {
            return ce.Response.StatusCode == HttpStatusCode.NotFound;
        }
    }
}
