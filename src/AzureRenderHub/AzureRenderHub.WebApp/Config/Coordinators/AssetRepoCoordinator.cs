﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AzureRenderHub.WebApp.Arm.Deploying;
using AzureRenderHub.WebApp.Config.Storage;
using Microsoft.Azure.Management.Compute;
using Microsoft.Azure.Management.Network;
using Microsoft.Azure.Management.ResourceManager;
using Microsoft.Azure.Management.ResourceManager.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Rest.Azure;
using Newtonsoft.Json.Linq;
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
        private readonly IManagementClientProvider _clientProvider;
        private readonly IAzureResourceProvider _azureResourceProvider;
        private readonly ILogger _logger;

        public AssetRepoCoordinator(
            IConfigRepository<AssetRepository> configCoordinator,
            ITemplateProvider templateProvider,
            IIdentityProvider identityProvider,
            IDeploymentQueue deploymentQueue,
            IManagementClientProvider clientProvider,
            IAzureResourceProvider azureResourceProvider,
            ILogger<AssetRepoCoordinator> logger)
        {
            _configCoordinator = configCoordinator;
            _templateProvider = templateProvider;
            _identityProvider = identityProvider;
            _deploymentQueue = deploymentQueue;
            _clientProvider = clientProvider;
            _azureResourceProvider = azureResourceProvider;
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
        public async Task BeginRepositoryDeploymentAsync(AssetRepository repository)
        {
            using (var client = await _clientProvider.CreateResourceManagementClient(repository.SubscriptionId))
            {
                await client.ResourceGroups.CreateOrUpdateAsync(
                    repository.ResourceGroupName,
                    new ResourceGroup(
                        repository.Subnet.Location, // The subnet location pins us to a region
                        tags: AzureResourceProvider.GetEnvironmentTags(repository.EnvironmentName)));

                await _azureResourceProvider.AssignRoleToIdentityAsync(
                    repository.SubscriptionId,
                    repository.ResourceGroupResourceId,
                    AzureResourceProvider.ContributorRole,
                    _identityProvider.GetPortalManagedServiceIdentity());

                repository.Deployment = new AzureRenderHub.WebApp.Arm.Deploying.Deployment
                {
                    DeploymentName = $"{repository.Name}-{Guid.NewGuid()}",
                    SubscriptionId = repository.SubscriptionId,
                    ResourceGroupName = repository.ResourceGroupName,
                };

                await UpdateRepository(repository);

                await DeployRepository(repository);
            }
        }

        public async Task UpdateRepositoryFromDeploymentAsync(AssetRepository repository)
        {
            if (repository.Deployment == null)
            {
                return;
            }

            var deployment = await GetDeploymentAndUpdateState(repository);

            if (repository is NfsFileServer fileServer)
            {
                await UpdateFileServerFromDeploymentAsync(deployment, fileServer);
            }
            else if (repository is AvereCluster avere)
            {
                await UpdateAvereFromDeploymentAsync(deployment, avere);
            }
            else
            {
                throw new NotSupportedException("Unknown type of repository");
            }

            await UpdateRepository(repository);
        }

        private async Task<DeploymentExtended> GetDeploymentAndUpdateState(AssetRepository repository)
        {
            var deployment = await GetDeploymentAsync(repository);
            if (deployment == null)
            {
                repository.Deployment.ProvisioningState = ProvisioningState.Unknown;
                if (repository.State == StorageState.Creating)
                {
                    // Deployment is gone, but storage never finished deploying (that we know).
                    repository.State = StorageState.Failed;
                }
            }
            else
            {
                Enum.TryParse<ProvisioningState>(deployment.Properties.ProvisioningState, out var deploymentState);

                repository.Deployment.ProvisioningState = deploymentState;

                if (deploymentState == ProvisioningState.Succeeded)
                {
                    repository.State = StorageState.Ready;
                }

                if (deploymentState == ProvisioningState.Failed)
                {
                    repository.State = StorageState.Failed;
                }
            }

            return deployment;
        }

        private async Task UpdateFileServerFromDeploymentAsync(
            DeploymentExtended deployment, 
            NfsFileServer fileServer)
        {
            if (fileServer.Deployment.ProvisioningState == ProvisioningState.Succeeded)
            {
                var (privateIp, publicIp) = await GetIpAddressesAsync(fileServer);
                fileServer.PrivateIp = privateIp;
                fileServer.PublicIp = publicIp;
            }

            if (fileServer.Deployment.ProvisioningState == ProvisioningState.Failed)
            {
                fileServer.State = StorageState.Failed;
            }
        }

        private async Task UpdateAvereFromDeploymentAsync(
            DeploymentExtended deployment, 
            AvereCluster avereCluster)
        {
            await Task.CompletedTask;

            if (avereCluster.Deployment.ProvisioningState == ProvisioningState.Succeeded)
            {
                if (deployment.Properties.Outputs != null)
                {
                    var outputs = deployment.Properties.Outputs as JObject;
                    avereCluster.SshConnectionDetails = (string)outputs["ssh_string"]?["value"];
                    avereCluster.ManagementIP = (string)outputs["mgmt_ip"]?["value"];
                    avereCluster.VServerIPRange = (string)outputs["vserver_ips"]?["value"];
                }
            }
        }

        // Called from the controller to initiate deletion
        public async Task BeginDeleteRepositoryAsync(AssetRepository repository)
        {
            repository.State = StorageState.Deleting;
            await UpdateRepository(repository);
            await _deploymentQueue.Add(new ActiveDeployment
            {
                StorageName = repository.Name,
                StartTime = DateTime.UtcNow,
                Action = "DeleteVM",
            });
        }

        // Called from the BackgroundHost to actually delete
        public async Task DeleteRepositoryResourcesAsync(AssetRepository repository, bool deleteResourceGroup)
        {
            if (deleteResourceGroup)
            {
                using (var resourceClient = await _clientProvider.CreateResourceManagementClient(repository.SubscriptionId))
                {
                    await resourceClient.ResourceGroups.DeleteAsync(repository.ResourceGroupName);
                }
            }
            else
            {
                if (repository is NfsFileServer fileServer)
                {
                    await DeleteFileServerAsync(fileServer);
                }
                else if (repository is AvereCluster avereCluster)
                {
                    await DeleteAvereAsync(avereCluster);
                }
                else
                {
                    throw new ArgumentException($"Repository {repository.Name} has unknown type {repository.RepositoryType}");
                }
            }

            await RemoveRepository(repository);
        }

        private async Task DeleteFileServerAsync(NfsFileServer fileServer)
        {
            await DeleteVirtualMachineAsync(fileServer.SubscriptionId, fileServer.ResourceGroupName, fileServer.VmName);
        }

        private async Task DeleteAvereAsync(AvereCluster avereCluster)
        {
            try
            {
                var avereClusterVmNames = await GetAvereVMNames(avereCluster);
                foreach(var vmName in avereClusterVmNames)
                {
                    await DeleteVirtualMachineAsync(
                        avereCluster.SubscriptionId, 
                        avereCluster.ResourceGroupName,
                        vmName);
                }
            }
            catch (CloudException ex) when (ResourceNotFound(ex))
            {
                // RG doesn't exist
            }
        }

        private async Task<List<string>> GetAvereVMNames(AvereCluster avereCluster)
        {
            var controllerName = avereCluster.ControllerName;
            var vfxtPrefix = avereCluster.ClusterName;

            using (var computeClient = await _clientProvider.CreateComputeManagementClient(avereCluster.SubscriptionId))
            {
                var vms = await computeClient.VirtualMachines.ListAsync(avereCluster.ResourceGroupName);
                var vmNames = vms.Select(vm => vm.Name).Where(name => IsAvereVM(avereCluster, name)).ToList();
                while (!string.IsNullOrEmpty(vms.NextPageLink))
                {
                    vms = await computeClient.VirtualMachines.ListAsync(avereCluster.ResourceGroupName);
                    vmNames.AddRange(vms.Select(vm => vm.Name).Where(name => IsAvereVM(avereCluster, name)));
                }
                return vmNames;
            }
        }

        private bool IsAvereVM(AvereCluster avereCluster, string vmName)
        {
            var controllerName = avereCluster.ControllerName;
            var vfxtPrefix = avereCluster.ClusterName.ToLowerInvariant();
            return vmName != null
                && (vmName.Equals(controllerName, StringComparison.InvariantCultureIgnoreCase)
                || vmName.ToLowerInvariant().StartsWith(vfxtPrefix));
        }

        public async Task DeleteVirtualMachineAsync(Guid subscription, string resourceGroupName, string vmName)
        {
            using (var resourceClient = await _clientProvider.CreateResourceManagementClient(subscription))
            using (var computeClient = await _clientProvider.CreateComputeManagementClient(subscription))
            using (var networkClient = await _clientProvider.CreateNetworkManagementClient(subscription))
            {
                try
                {
                    var virtualMachine = await computeClient.VirtualMachines.GetAsync(resourceGroupName, vmName);

                    var nicName = virtualMachine.NetworkProfile.NetworkInterfaces[0].Id.Split("/").Last(); ;
                    var avSetName = virtualMachine.AvailabilitySet.Id?.Split("/").Last();
                    var osDisk = virtualMachine.StorageProfile.OsDisk.ManagedDisk.Id.Split("/").Last();
                    var dataDisks = virtualMachine.StorageProfile.DataDisks.Select(dd => dd.ManagedDisk.Id.Split("/").Last()).ToList();

                    string pip = null;
                    string nsg = null;
                    try
                    {
                        var nic = await networkClient.NetworkInterfaces.GetAsync(resourceGroupName, nicName);
                        pip = nic.IpConfigurations[0].PublicIPAddress?.Id.Split("/").Last();
                        nsg = nic.NetworkSecurityGroup?.Id.Split("/").Last();
                    }
                    catch (CloudException ex) when (ResourceNotFound(ex))
                    {
                        // NIC doesn't exist
                    }

                    await IgnoreNotFound(async () =>
                    {
                        await computeClient.VirtualMachines.GetAsync(resourceGroupName, vmName);
                        await computeClient.VirtualMachines.DeleteAsync(resourceGroupName, vmName);
                    });

                    if (nicName != null)
                    {
                        await IgnoreNotFound(() => networkClient.NetworkInterfaces.DeleteAsync(resourceGroupName, nicName));
                    }

                    var tasks = new List<Task>();

                    if (nsg == "nsg")
                    {
                        tasks.Add(IgnoreNotFound(() => networkClient.NetworkSecurityGroups.DeleteAsync(resourceGroupName, nsg)));
                    }

                    if (pip != null)
                    {
                        tasks.Add(IgnoreNotFound(() => networkClient.PublicIPAddresses.DeleteAsync(resourceGroupName, pip)));
                    }

                    tasks.Add(IgnoreNotFound(() => computeClient.Disks.DeleteAsync(resourceGroupName, osDisk)));

                    tasks.AddRange(dataDisks.Select(
                        dd => IgnoreNotFound(() => computeClient.Disks.DeleteAsync(resourceGroupName, dd))));

                    await Task.WhenAll(tasks);

                    if (avSetName != null)
                    {
                        await IgnoreNotFound(() => computeClient.AvailabilitySets.DeleteAsync(resourceGroupName, avSetName));
                    }
                }
                catch (CloudException ex) when (ResourceNotFound(ex))
                {
                    // VM doesn't exist
                }

                try
                {
                    await resourceClient.ResourceGroups.GetAsync(resourceGroupName);

                    var resources = await resourceClient.Resources.ListByResourceGroupAsync(resourceGroupName);
                    if (resources.Any())
                    {
                        _logger.LogDebug($"Skipping resource group deletion as it contains the following resources: {string.Join(", ", resources.Select(r => r.Id))}");
                    }
                    else
                    {
                        await resourceClient.ResourceGroups.DeleteAsync(resourceGroupName);
                    }
                }
                catch (CloudException ex) when (ResourceNotFound(ex))
                {
                    // RG doesn't exist
                }
            }
        }

        private async Task<(string privateIp, string publicIp)> GetIpAddressesAsync(NfsFileServer fileServer)
        {
            using (var computeClient = await _clientProvider.CreateComputeManagementClient(fileServer.SubscriptionId))
            using (var networkClient = await _clientProvider.CreateNetworkManagementClient(fileServer.SubscriptionId))
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

        private async Task<DeploymentExtended> GetDeploymentAsync(AssetRepository assetRepo)
        {
            if (assetRepo?.Deployment == null || assetRepo.ResourceGroupName == null)
            {
                return null;
            }

            using (var resourceClient = await _clientProvider.CreateResourceManagementClient(assetRepo.SubscriptionId))
            {
                try
                {
                    return await resourceClient.Deployments.GetAsync(
                        assetRepo.ResourceGroupName,
                        assetRepo.Deployment.DeploymentName);
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

        private async Task DeployRepository(AssetRepository repository)
        {
            try
            {
                using (var client = await _clientProvider.CreateResourceManagementClient(repository.SubscriptionId))
                {
                    await client.ResourceGroups.CreateOrUpdateAsync(repository.ResourceGroupName,
                        new ResourceGroup { Location = repository.Subnet.Location });

                    var templateParams = repository.GetTemplateParameters();

                    var properties = new Microsoft.Azure.Management.ResourceManager.Models.Deployment
                    {
                        Properties = new DeploymentProperties
                        {
                            Template = await _templateProvider.GetTemplate(repository.GetTemplateName()),
                            Parameters = _templateProvider.GetParameters(templateParams),
                            Mode = DeploymentMode.Incremental
                        }
                    };

                    // Start the ARM deployment
                    await client.Deployments.BeginCreateOrUpdateAsync(
                        repository.Deployment.ResourceGroupName,
                        repository.Deployment.DeploymentName,
                        properties);

                    // TODO re-enable below for background monitoring.
                    // Queue a request for the background host to monitor the deployment
                    // and update the state and IP address when it's done.
                    //await _deploymentQueue.Add(new ActiveDeployment
                    //{
                    //    FileServerName = repository.Name,
                    //    StartTime = DateTime.UtcNow,
                    //});

                    repository.State = StorageState.Creating;
                    repository.InProgress = false;

                    await UpdateRepository(repository);
                }
            }
            catch (CloudException ex)
            {
                _logger.LogError(ex, $"Failed to deploy storage server: {ex.Message}.");
                throw;
            }
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
