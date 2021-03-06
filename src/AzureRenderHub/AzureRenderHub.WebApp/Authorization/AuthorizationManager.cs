﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebApp.Arm;
using WebApp.Config;
using WebApp.Models.Environments;
using TaskTupleAwaiter;
using WebApp.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Web.Client;

namespace WebApp.Authorization
{
    public class AuthorizationManager: NeedsAccessToken
    {
        private const string OwnerRole = "Owner";
        private const string ContributorRole = "Contributor";
        private const string VirtualMachineContributorRole = "Virtual Machine Contributor";
        private const string ReaderRole = "Reader";

        private static string[] AdminRoles = new[] { OwnerRole, ContributorRole };
        private static string[] PoolManagerRoles = new[] { OwnerRole, ContributorRole, VirtualMachineContributorRole };
        private static string[] ReaderRoles = new[] { OwnerRole, ContributorRole, ReaderRole };

        private static EnvironmentRoleAssignments EnvironmentReaderRoles = new EnvironmentRoleAssignments
        {
            EnvironmentResourceGroupRole = ReaderRole,
            BatchRole = ReaderRole,
            StorageRole = ReaderRole,
            KeyVaultRole = ReaderRole,
            ApplicationInsightsRole = ReaderRole,
            VNetRole = ReaderRole,
        };

        private static EnvironmentRoleAssignments EnvironmentPoolManagerRoles = new EnvironmentRoleAssignments
        {
            EnvironmentResourceGroupRole = ReaderRole,
            BatchRole = ContributorRole,
            StorageRole = ReaderRole,
            KeyVaultRole = ReaderRole,
            ApplicationInsightsRole = ReaderRole,
            VNetRole = VirtualMachineContributorRole,
        };

        private static EnvironmentRoleAssignments EnvironmentOwnerRoles = new EnvironmentRoleAssignments
        {
            EnvironmentResourceGroupRole = OwnerRole,
            BatchRole = OwnerRole,
            StorageRole = OwnerRole,
            KeyVaultRole = OwnerRole,
            ApplicationInsightsRole = OwnerRole,
            VNetRole = OwnerRole,
        };

        private readonly IAzureResourceProvider _azureResourceProvider;
        private readonly IGraphProvider _graphProvider;
        private readonly IConfiguration _configuration;

        public AuthorizationManager(
            IHttpContextAccessor contextAccessor,
            ITokenAcquisition tokenAcquisition,
            IConfiguration configuration,
            IAzureResourceProvider azureResourceProvider,
            IGraphProvider graphProvider) : base(contextAccessor, tokenAcquisition)
        {
            _azureResourceProvider = azureResourceProvider;
            _configuration = configuration;
            _graphProvider = graphProvider;
        }

        public async Task<List<UserPermission>> ListClassicAdministrators(RenderingEnvironment environment)
        {
            var classicAdmins = await _azureResourceProvider.ListClassicAdministrators(environment.SubscriptionId);

            var permissions = new List<UserPermission>();

            foreach (var admin in classicAdmins)
            {
                permissions.Add(new UserPermission
                {
                    Name = admin.EmailAddress, // The 'Name' property doesn't actually contain a name
                    Email = admin.EmailAddress,
                    ObjectId = admin.Id,
                    Role = admin.Role,
                });
            }

            return permissions;
        }

        // Gets all the user role assignemtns for the environment and collapses them to "Portal" roles
        // that we can display for each user.
        public async Task<List<UserPermission>> ListUserPermissions(RenderingEnvironment environment)
        {
            var environmentPermissions = await GetResourcePermissions(environment);
            var userObjectIds = environmentPermissions.GetUserObjectIds();
            var finalPermissions = new List<UserPermission>();
            foreach (var objectId in userObjectIds)
            {
                var userEnvironmentPermissions = environmentPermissions.ToUserEnvironmentPermissions(objectId);
                var userPermission = userEnvironmentPermissions.GetFirstUserPermission();

                if (userEnvironmentPermissions.IsOwner())
                {
                    finalPermissions.Add(new UserPermission
                    {
                        ObjectId = objectId,
                        Email = userPermission.Email,
                        Name = userPermission.Name,
                        Role = PortalRole.Owner.ToString(),
                        GraphResolutionFailure = userPermission.GraphResolutionFailure,
                    });
                }
                else if (userEnvironmentPermissions.IsPoolManager())
                {
                    finalPermissions.Add(new UserPermission
                    {
                        ObjectId = objectId,
                        Email = userPermission.Email,
                        Name = userPermission.Name,
                        Role = PortalRole.PoolManager.ToString(),
                        GraphResolutionFailure = userPermission.GraphResolutionFailure,
                    });
                }
                else if (userEnvironmentPermissions.IsReader())
                {
                    finalPermissions.Add(new UserPermission
                    {
                        ObjectId = objectId,
                        Email = userPermission.Email,
                        Name = userPermission.Name,
                        Role = PortalRole.Reader.ToString(),
                        GraphResolutionFailure = userPermission.GraphResolutionFailure,
                    });
                }
            }

            return finalPermissions;
        }

        private async Task<EnvironmentPermissions> GetResourcePermissions(RenderingEnvironment environment)
        {
            var (resourceGroupPermissions,
                batchPermissions,
                storagePermissions,
                keyVaultPermissions,
                appInsightsPermissions,
                vnetPermissions) = await (
                _azureResourceProvider.GetUserPermissions(environment.SubscriptionId, environment.ResourceGroupResourceId),
                _azureResourceProvider.GetUserPermissions(environment.SubscriptionId, environment.BatchAccount.ResourceId),
                _azureResourceProvider.GetUserPermissions(environment.SubscriptionId, environment.StorageAccount.ResourceId),
                _azureResourceProvider.GetUserPermissions(environment.SubscriptionId, environment.KeyVault.ResourceId),
                _azureResourceProvider.GetUserPermissions(environment.SubscriptionId, environment.ApplicationInsightsAccount.ResourceId),
                _azureResourceProvider.GetUserPermissions(environment.SubscriptionId, environment.Subnet.VnetResourceId));

            return new EnvironmentPermissions
            {
                // The RG query will return all relative resources too so we need to filter those out
                EnvironmentResourceGroup = resourceGroupPermissions.Where(
                    p => p.Scope == $"/subscriptions/{environment.SubscriptionId}" || 
                    p.Scope == environment.ResourceGroupResourceId).ToList(),
                Batch = batchPermissions,
                Storage = storagePermissions,
                KeyVault = keyVaultPermissions,
                ApplicationInsights = appInsightsPermissions,
                VNet = vnetPermissions,
            };
        }

        public async Task AssignRoleToUser(RenderingEnvironment environment, string userEmailAddress, PortalRole userRole)
        {
            var graphUser = await _graphProvider.GetUser(GetUser(), userEmailAddress);
            if (graphUser == null)
            {
                throw new Exception($"No user with email address {userEmailAddress} found in Graph.");
            }
            await AssignRoleToUser(environment, Guid.Parse(graphUser.Id), userRole);
        }

        private EnvironmentRoleAssignments GetEnvironmentRolesForPortalRole(PortalRole userRole)
        {
            EnvironmentRoleAssignments roleAssignments = null;
            switch (userRole)
            {
                case PortalRole.Reader:
                    roleAssignments = EnvironmentReaderRoles;
                    break;
                case PortalRole.PoolManager:
                    roleAssignments = EnvironmentPoolManagerRoles;
                    break;
                case PortalRole.Owner:
                    roleAssignments = EnvironmentOwnerRoles;
                    break;
            }

            if (roleAssignments == null)
            {
                throw new Exception($"No role assignments configured for role {userRole}");
            }

            return roleAssignments;
        }

        public async Task AssignRoleToUser(RenderingEnvironment environment, Guid objectId, PortalRole userRole)
        {
            EnvironmentRoleAssignments roleAssignments = GetEnvironmentRolesForPortalRole(userRole);
            await AssignRolesToUser(objectId, environment, roleAssignments);
        }

        private async Task AssignRolesToUser(Guid objectId, RenderingEnvironment environment, EnvironmentRoleAssignments roleAssignments)
        {
            var identity = new Identity.Identity { ObjectId = objectId };

            // Assign RG permissions
            // We want to give the correct permissions to the environment RG, 
            // but we also need to give Reader permissions to the other RGs so
            // we can query cost information.

            // ResourceId => RoleName
            var resourceIdsToRoles = environment.ExtractResourceGroupNames().ToDictionary(
                    rgName => $"/subscriptions/{environment.SubscriptionId}/resourceGroups/{rgName}",
                    rgName => rgName == environment.ResourceGroupName ? roleAssignments.EnvironmentResourceGroupRole : "Reader");

            // Add the explicit resource roles
            resourceIdsToRoles[environment.BatchAccount.ResourceId] = roleAssignments.BatchRole;
            resourceIdsToRoles[environment.StorageAccount.ResourceId] = roleAssignments.StorageRole;
            resourceIdsToRoles[environment.KeyVault.ResourceId] = roleAssignments.KeyVaultRole;
            resourceIdsToRoles[environment.ApplicationInsightsAccount.ResourceId] = roleAssignments.ApplicationInsightsRole;
            resourceIdsToRoles[environment.Subnet.VnetResourceId] = roleAssignments.VNetRole;

            await Task.WhenAll(resourceIdsToRoles.Select(
                kvp => _azureResourceProvider.AssignRoleToIdentityAsync(
                    environment.SubscriptionId,
                    kvp.Key, // ResourceId/scope
                    kvp.Value, // Role
                    identity)));
        }

        class EnvironmentRoleAssignments
        {
            public string EnvironmentResourceGroupRole { get; set; }

            public string BatchRole { get; set; }

            public string StorageRole { get; set; }

            public string KeyVaultRole { get; set; }

            public string ApplicationInsightsRole { get; set; }

            public string VNetRole { get; set; }
        }

        class EnvironmentPermissions
        {
            public List<UserPermission> EnvironmentResourceGroup { get; set; } = new List<UserPermission>();

            public List<UserPermission> Batch { get; set; } = new List<UserPermission>();

            public List<UserPermission> Storage { get; set; } = new List<UserPermission>();

            public List<UserPermission> KeyVault { get; set; } = new List<UserPermission>();

            public List<UserPermission> ApplicationInsights { get; set; } = new List<UserPermission>();

            public List<UserPermission> VNet { get; set; } = new List<UserPermission>();

            public IEnumerable<string> GetUserObjectIds()
            {
                return EnvironmentResourceGroup
                    .Concat(Batch)
                    .Concat(Storage)
                    .Concat(KeyVault)
                    .Concat(ApplicationInsights)
                    .Concat(VNet)
                    .Select(p => p.ObjectId)
                    .ToHashSet();
            }

            // Returns the environment permissions for a given user
            public UserEnvironmentPermissions ToUserEnvironmentPermissions(string objectId)
            {
                return new UserEnvironmentPermissions
                {
                    EnvironmentResourceGroup = EnvironmentResourceGroup.Where(p => p.ObjectId == objectId).ToList(),
                    Batch = Batch.Where(p => p.ObjectId == objectId).ToList(),
                    Storage = Storage.Where(p => p.ObjectId == objectId).ToList(),
                    KeyVault = KeyVault.Where(p => p.ObjectId == objectId).ToList(),
                    ApplicationInsights = ApplicationInsights.Where(p => p.ObjectId == objectId).ToList(),
                    VNet = VNet.Where(p => p.ObjectId == objectId).ToList(),
                };
            }
        }

        class UserEnvironmentPermissions : EnvironmentPermissions
        {
            public UserPermission GetFirstUserPermission()
            {
                return EnvironmentResourceGroup
                    .Concat(Batch)
                    .Concat(Storage)
                    .Concat(KeyVault)
                    .Concat(ApplicationInsights)
                    .Concat(VNet)
                    .FirstOrDefault();
            }

            public bool IsOwner()
            {
                return EnvironmentResourceGroup.Any(p => p.Role == OwnerRole) &&
                    Batch.Any(p => p.Role == OwnerRole) &&
                    Storage.Any(p => p.Role == OwnerRole) &&
                    KeyVault.Any(p => p.Role == OwnerRole) &&
                    ApplicationInsights.Any(p => p.Role == OwnerRole) &&
                    VNet.Any(p => p.Role == OwnerRole);
            }

            public bool IsPoolManager()
            {
                return EnvironmentResourceGroup.Any(p => ReaderRoles.Contains(p.Role)) &&
                    Batch.Any(p => AdminRoles.Contains(p.Role)) &&
                    Storage.Any(p => ReaderRoles.Contains(p.Role)) &&
                    KeyVault.Any(p => ReaderRoles.Contains(p.Role)) &&
                    ApplicationInsights.Any(p => ReaderRoles.Contains(p.Role)) &&
                    VNet.Any(p => PoolManagerRoles.Contains(p.Role));
            }

            public bool IsReader()
            {
                return EnvironmentResourceGroup.Any(p => ReaderRoles.Contains(p.Role)) &&
                    Batch.Any(p => ReaderRoles.Contains(p.Role)) &&
                    Storage.Any(p => ReaderRoles.Contains(p.Role)) &&
                    KeyVault.Any(p => ReaderRoles.Contains(p.Role)) &&
                    ApplicationInsights.Any(p => ReaderRoles.Contains(p.Role)) &&
                    VNet.Any(p => ReaderRoles.Contains(p.Role));
            }
        }
    }
}
