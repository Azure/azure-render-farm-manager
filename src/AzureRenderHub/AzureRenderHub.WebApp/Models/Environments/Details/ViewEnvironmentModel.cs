﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Azure.Management.ResourceManager.Models;
using System;
using System.Collections.Generic;
using WebApp.AppInsights.PoolUsage;
using WebApp.Code.Extensions;
using WebApp.Config;
using WebApp.Models.Environments.Create;
using WebApp.Models.Reporting;

namespace WebApp.Models.Environments.Details
{
    /// <summary>
    /// TODO: can probably break this out into separate decorator classes!!!!.
    /// Would make it look tidier.
    /// </summary>
    public class ViewEnvironmentModel : EnvironmentBaseModel
    {
        public ViewEnvironmentModel()
        {

        }

        public ViewEnvironmentModel(
            RenderingEnvironment environment,
            Microsoft.Azure.Management.Batch.Models.BatchAccount batchAccount = null,
            IList<PoolUsageResult> poolUsageResults = null,
            EnvironmentCost usage = null)
        {
            if (environment != null)
            {
                EnvironmentName = environment.Name;
                SubscriptionId = environment.SubscriptionId;
                RenderManager = environment.RenderManager;
                LocationName = environment.LocationName;
                ResourceGroup = environment.ResourceGroupName;
                KeyVaultName = environment.KeyVault?.Name;
                KeyVaultUrl = environment.KeyVault?.Uri;
                KeyVaultResourceId = environment.KeyVault?.ResourceId;

                if (environment.KeyVaultServicePrincipal != null)
                {
                    KeyVaultServicePrincipalAppId = environment.KeyVaultServicePrincipal.ApplicationId;
                    KeyVaultServicePrincipalObjectId = environment.KeyVaultServicePrincipal.ObjectId;
                    KeyVaultServicePrincipalCertificatePath = environment.KeyVaultServicePrincipal.CertificateKeyVaultName;
                }

                if (environment.BatchAccount != null)
                {
                    BatchAccountName = environment.BatchAccount.Name;
                    BatchAccountResourceId = environment.BatchAccount.ResourceId;
                    BatchAccountLocation = environment.BatchAccount.Location;
                    BatchAccountUrl = environment.BatchAccount.Url;
                }

                if (environment.StorageAccount != null)
                {
                    StorageAccountName = environment.StorageAccount.Name;
                    StorageAccountResourceId = environment.StorageAccount.ResourceId;
                    StorageAccountLocation = environment.StorageAccount.Location;
                }

                if (environment.Subnet != null)
                {
                    SubnetName = environment.Subnet.Name;
                    SubnetVNetName = environment.Subnet.VNetName;
                    SubnetResourceId = environment.Subnet.ResourceId;
                    SubnetPrefix = environment.Subnet.AddressPrefix;
                    SubnetLocation = environment.Subnet.Location;
                }

                if (environment.ApplicationInsightsAccount?.ResourceId != null)
                {
                    AppInsightsName = environment.ApplicationInsightsAccount.Name;
                    AppInsightsResourceId = environment.ApplicationInsightsAccount.ResourceId;
                    AppInsightsLocation = environment.ApplicationInsightsAccount.Location;
                }

                if (environment.AutoScaleConfiguration != null)
                {
                    MaxIdleCpuPercent = environment.AutoScaleConfiguration.MaxIdleCpuPercent;
                    WhitelistedProcesses = environment.AutoScaleConfiguration.SpecificProcesses;
                }

                if (environment.DeletionSettings != null &&
                    !string.IsNullOrEmpty(environment.DeletionSettings.DeleteErrors))
                {
                    DeleteErrors = environment.DeletionSettings.DeleteErrors;
                }

                if (environment.Domain != null)
                {
                    JoinDomain = environment.Domain.JoinDomain;
                    DomainName = environment.Domain.DomainName;
                    DomainJoinUsername = environment.Domain.DomainJoinUsername;
                    DomainJoinPassword = environment.Domain.DomainJoinPassword;
                    DomainWorkerOuPath = environment.Domain.DomainWorkerOuPath;
                }

                if (environment.RenderManagerConfig != null)
                {
                    if (environment.RenderManagerConfig.Deadline != null)
                    {
                        DeadlineEnvironment = new DeadlineEnvironment
                        {
                            WindowsDeadlineRepositoryShare = environment.RenderManagerConfig.Deadline.WindowsRepositoryPath,
                            RepositoryUser = environment.RenderManagerConfig.Deadline.RepositoryUser,
                            RepositoryPassword = environment.RenderManagerConfig.Deadline.RepositoryPassword,
                            InstallDeadlineClient = environment.RenderManagerConfig.Deadline.LicenseServer != null,
                            LicenseMode = environment.RenderManagerConfig.Deadline.LicenseMode,
                            LicenseServer = environment.RenderManagerConfig.Deadline.LicenseServer,
                            DeadlineRegion = environment.RenderManagerConfig.Deadline.DeadlineRegion,
                            DeadlineDatabaseCertificatePassword = environment.RenderManagerConfig.Deadline.DeadlineDatabaseCertificate?.Password,
                            RunAsService = environment.RenderManagerConfig.Deadline.RunAsService,
                            ServiceUser = environment.RenderManagerConfig.Deadline.ServiceUser,
                            ServicePassword = environment.RenderManagerConfig.Deadline.ServicePassword,
                            UseDeadlineDatabaseCertificate = environment.RenderManagerConfig.Deadline.DeadlineDatabaseCertificate?.FileName != null,
                            DeadlineDatabaseCertificateFileName = environment.RenderManagerConfig.Deadline.DeadlineDatabaseCertificate?.FileName,
                            ExcludeFromLimitGroups = environment.RenderManagerConfig.Deadline.ExcludeFromLimitGroups,
                        };
                    }

                    if (environment.RenderManagerConfig.Qube != null)
                    {
                        QubeEnvironment = new QubeEnvironment
                        {
                            QubeSupervisor = environment.RenderManagerConfig.Qube.SupervisorIp,
                        };
                    }

                    if (environment.RenderManagerConfig.Tractor != null)
                    {
                        TractorEnvironment = new TractorEnvironment
                        {
                            EngineIpOrHostnameAndPort = environment.RenderManagerConfig.Tractor.EngineIpOrHostnameAndPort,
                        };
                    }

                    if (environment.RenderManagerConfig.OpenCue != null)
                    {
                        OpenCueEnvironment = new OpenCueEnvironment
                        {
                            CuebotHostnameOrIp = environment.RenderManagerConfig.OpenCue.CuebotHostnameOrIp,
                            Facility = environment.RenderManagerConfig.OpenCue.Facility,
                        };
                    }

                    if (environment.RenderManagerConfig.BYOS != null)
                    {
                        BYOSEnvironment = new BYOSEnvironment
                        {
                            SchedulerHostnameOrIp = environment.RenderManagerConfig.BYOS.SchedulerHostnameOrIp,
                        };
                    }
                }
            }

            if (batchAccount != null)
            {
                BatchDedicatedCoreQuota = batchAccount.DedicatedCoreQuota;
                BatchLowPriorityCoreQuota = batchAccount.LowPriorityCoreQuota;
                BatchPoolQuota = batchAccount.PoolQuota;
            }

            PoolUsageResults = poolUsageResults;
            EnvironmentCost = usage;
        }

        // Details

        public Guid? SubscriptionId { get; set; }

        public string LocationName { get; set; }

        public string ResourceGroup { get; set; }

        public string KeyVaultName { get; set; }

        public string KeyVaultResourceId { get; set; }

        public string KeyVaultUrl{ get; set; }

        // Identity

        public Guid ManagementServicePrincipalAppId { get; set; }

        public Guid ManagementServicePrincipalObjectId { get; set; }

        public string ManagementServicePrincipalKey { get; set; }

        public Guid KeyVaultServicePrincipalAppId { get; set; }

        public Guid KeyVaultServicePrincipalObjectId { get; set; }

        public string KeyVaultServicePrincipalCertificatePath { get; set; }

        // Resources

        public string BatchAccountName { get; set; }

        public string BatchAccountUrl { get; set; }

        public string BatchAccountResourceId { get; set; }

        public string BatchAccountLocation { get; set; }

        public string StorageAccountName { get; set; }

        public string StorageAccountResourceId { get; set; }

        public string StorageAccountLocation { get; set; }

        public string SubnetName { get; set; }

        public string SubnetVNetName { get; set; }

        public string SubnetResourceId { get; set; }

        public string SubnetLocation { get; set; }

        public string SubnetPrefix { get; set; }

        public string AppInsightsName { get; set; }

        public string AppInsightsResourceId { get; set; }

        public string AppInsightsLocation { get; set; }

        // Manager config
        public DeadlineEnvironment DeadlineEnvironment { get; set; }

        public QubeEnvironment QubeEnvironment { get; set; }

        public TractorEnvironment TractorEnvironment { get; set; }

        public OpenCueEnvironment OpenCueEnvironment { get; set; }

        public BYOSEnvironment BYOSEnvironment { get; set; }

        // Domain
        public bool JoinDomain { get; set; }

        public string DomainName { get; set; }

        public string DomainWorkerOuPath { get; set; }

        public string DomainJoinUsername { get; set; }

        public string DomainJoinPassword { get; set; }

        // Env Config
        public int MaxIdleCpuPercent { get; set; }

        public List<string> WhitelistedProcesses { get; set; }

        // Deletion Errors
        public string DeleteErrors { get; set; }

        // Quotas
        public int BatchDedicatedCoreQuota { get; set; }

        public int BatchLowPriorityCoreQuota { get; set; }

        public int BatchPoolQuota { get; set; }

        public IList<PoolUsageResult> PoolUsageResults { get; set; }

        public EnvironmentCost EnvironmentCost { get; set; }

        // Links
        private const string PortalPrefix = "https://portal.azure.com/#resource";

        public string ResourceGroupPortalLink => $"{PortalPrefix}/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}";

        public string BatchAccountPortalLink => $"{PortalPrefix}{BatchAccountResourceId}";

        public string KeyVaultPortalLink => $"{PortalPrefix}{KeyVaultResourceId}";

        public string StorageAccountPortalLink => $"{PortalPrefix}{StorageAccountResourceId}";

        public string AppInsightsComponentPortalLink => $"{PortalPrefix}{AppInsightsResourceId}";

        public string SubnetPortalLink => $"{PortalPrefix}{SubnetResourceId}";
    }
}
