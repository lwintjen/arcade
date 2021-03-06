// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using Microsoft.DotNet.Build.Tasks.Feed.Model;
using Microsoft.DotNet.Maestro.Client;
using Microsoft.DotNet.Maestro.Client.Models;
using Microsoft.DotNet.VersionTools.BuildManifest.Model;
using NuGet.Packaging.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.DotNet.Build.Tasks.Feed
{
    /// <summary>
    /// The intended use of this task is to push artifacts described in
    /// a build manifest to a static package feed.
    /// </summary>
    public abstract class PublishArtifactsInManifestBase : Microsoft.Build.Utilities.Task
    {
        /// <summary>
        /// Full path to the folder containing blob assets.
        /// </summary>
        [Required]
        public string BlobAssetsBasePath { get; set; }

        /// <summary>
        /// Full path to the folder containing package assets.
        /// </summary>
        [Required]
        public string PackageAssetsBasePath { get; set; }

        /// <summary>
        /// ID of the build (in BAR/Maestro) that produced the artifacts being published.
        /// This might change in the future as we'll probably fetch this ID from the manifest itself.
        /// </summary>
        [Required]
        public int BARBuildId { get; set; }

        /// <summary>
        /// Access point to the Maestro API to be used for accessing BAR.
        /// </summary>
        [Required]
        public string MaestroApiEndpoint { get; set; }

        /// <summary>
        /// Authentication token to be used when interacting with Maestro API.
        /// </summary>
        [Required]
        public string BuildAssetRegistryToken { get; set; }

        /// <summary>
        /// Directory where "nuget.exe" is installed. This will be used to publish packages.
        /// </summary>
        [Required]
        public string NugetPath { get; set; }

        /// <summary>
        /// Maximum number of parallel uploads for the upload tasks
        /// </summary>
        public int MaxClients { get; set; } = 16;

        /// <summary>
        /// Whether this build is internal or not. If true, extra checks are done to avoid accidental
        /// publishing of assets to public feeds or storage accounts.
        /// </summary>
        public bool InternalBuild { get; set; } = false;

        /// <summary>
        /// If true, safety checks only print messages and do not error
        /// - Internal asset to public feed
        /// - Stable packages to non-isolated feeds
        /// </summary>
        public bool SkipSafetyChecks { get; set; } = false;

        /// <summary>
        /// Which build model (i.e., parsed build manifest) the publishing task will operate on.
        /// This is set by the main publishing task before dispatching the execution to one of
        /// the version specific publishing task.
        /// </summary>
        public BuildModel BuildModel { get; set; }

        public string AkaMSClientId { get; set; }

        public string AkaMSClientSecret { get; set; }

        public string AkaMSTenant { get; set; }

        public string AkaMsOwners { get; set; }

        public string AkaMSCreatedBy { get; set; }

        public string AkaMSGroupOwner { get; set; }

        public readonly Dictionary<TargetFeedContentType, HashSet<TargetFeedConfig>> FeedConfigs = 
            new Dictionary<TargetFeedContentType, HashSet<TargetFeedConfig>>();

        private readonly Dictionary<TargetFeedContentType, HashSet<PackageArtifactModel>> PackagesByCategory = 
            new Dictionary<TargetFeedContentType, HashSet<PackageArtifactModel>>();

        private readonly Dictionary<TargetFeedContentType, HashSet<BlobArtifactModel>> BlobsByCategory = 
            new Dictionary<TargetFeedContentType, HashSet<BlobArtifactModel>>();

        private readonly ConcurrentDictionary<(int AssetId, string AssetLocation, AddAssetLocationToAssetAssetLocationType LocationType), ValueTuple> NewAssetLocations =
            new ConcurrentDictionary<(int AssetId, string AssetLocation, AddAssetLocationToAssetAssetLocationType LocationType), ValueTuple>();

        protected LatestLinksManager LinkManager { get; set; } = null;

        public override bool Execute()
        {
            return ExecuteAsync().GetAwaiter().GetResult();
        }

        public abstract Task<bool> ExecuteAsync();

        /// <summary>
        ///     Lookup an asset in the build asset dictionary by name and version
        /// </summary>
        /// <param name="name">Name of asset</param>
        /// <param name="version">Version of asset</param>
        /// <returns>Asset if one with the name and version exists, null otherwise</returns>
        private Asset LookupAsset(string name, string version, Dictionary<string, HashSet<Asset>> buildAssets)
        {
            if (!buildAssets.TryGetValue(name, out HashSet<Asset> assetsWithName))
            {
                return null;
            }
            return assetsWithName.FirstOrDefault(asset => asset.Version == version);
        }

        /// <summary>
        ///     Lookup an asset in the build asset dictionary by name only.
        ///     This is for blob lookup purposes.
        /// </summary>
        /// <param name="name">Name of asset</param>
        /// <returns>
        ///     Asset if one with the name exists and is the only asset with the name.
        ///     Throws if there is more than one asset with that name.
        /// </returns>
        private Asset LookupAsset(string name, Dictionary<string, HashSet<Asset>> buildAssets)
        {
            if (!buildAssets.TryGetValue(name, out HashSet<Asset> assetsWithName))
            {
                return null;
            }
            return assetsWithName.Single();
        }

        /// <summary>
        ///     Build up a map of asset name -> asset list so that we can avoid n^2 lookups when processing assets.
        ///     We use name only because blobs are only looked up by id (version is not recorded in the manifest).
        ///     This could mean that there might be multiple versions of the same asset in the build.
        /// </summary>
        /// <param name="buildInformation">Build information</param>
        /// <returns>Map of asset name -> list of assets with that name.</returns>
        protected Dictionary<string, HashSet<Asset>> CreateBuildAssetDictionary(Maestro.Client.Models.Build buildInformation)
        {
            Dictionary<string, HashSet<Asset>> buildAssets = new Dictionary<string, HashSet<Asset>>();

            foreach (var asset in buildInformation.Assets)
            {
                if (buildAssets.TryGetValue(asset.Name, out HashSet<Asset> assetsWithName))
                {
                    assetsWithName.Add(asset);
                }
                else
                {
                    buildAssets.Add(asset.Name, new HashSet<Asset>(new AssetComparer()) { asset });
                }
            }

            return buildAssets;
        }

        /// <summary>
        ///   Records the association of Asset -> AssetLocation to be persisted later in BAR.
        /// </summary>
        /// <param name="assetId">Id of the asset (i.e., name of the package) whose the location should be updated.</param>
        /// <param name="assetVersion">Version of the asset whose the location should be updated.</param>
        /// <param name="buildAssets">List of BAR Assets for the build that's being modified.</param>
        /// <param name="feedConfig">Configuration of where the asset was published.</param>
        /// <param name="assetLocationType">Type of feed location that is being added.</param>
        /// <returns>True if that asset didn't have the informed location recorded already.</returns>
        private bool TryAddAssetLocation(string assetId, string assetVersion, Dictionary<string, HashSet<Asset>> buildAssets, TargetFeedConfig feedConfig, AddAssetLocationToAssetAssetLocationType assetLocationType)
        {
            Asset assetRecord = string.IsNullOrEmpty(assetVersion) ?
                LookupAsset(assetId, buildAssets) :
                LookupAsset(assetId, assetVersion, buildAssets);

            if (assetRecord == null)
            {
                string versionMsg = string.IsNullOrEmpty(assetVersion) ? string.Empty : $"and version {assetVersion}";
                Log.LogError($"Asset with Id {assetId} {versionMsg} isn't registered on the BAR Build with ID {BARBuildId}");
                return false;
            }

            return NewAssetLocations.TryAdd((assetRecord.Id, feedConfig.TargetURL, assetLocationType), ValueTuple.Create());
        }

        /// <summary>
        ///   Persist in BAR all pending associations of Asset -> AssetLocation stored in `NewAssetLocations`.
        /// </summary>
        /// <param name="client">Maestro++ API client</param>
        protected Task PersistPendingAssetLocationAsync(IMaestroApi client)
        {
            var updates = NewAssetLocations.Keys.Select(nal => new AssetAndLocation(nal.AssetId, (LocationType)nal.LocationType)
            {
                Location = nal.AssetLocation
            }).ToImmutableList();

            return client.Assets.BulkAddLocationsAsync(updates);
        }

        /// <summary>
        /// Protect against accidental publishing of internal assets to non-internal feeds.
        /// </summary>
        /// <returns></returns>
        protected void CheckForInternalBuildsOnPublicFeeds(TargetFeedConfig feedConfig)
        {
            // If separated out for clarity.
            if (!SkipSafetyChecks)
            {
                if (InternalBuild && !feedConfig.Internal)
                {
                    Log.LogError($"Use of non-internal feed '{feedConfig.TargetURL}' is invalid for an internal build. This can be overridden with '{nameof(SkipSafetyChecks)}= true'");
                }
            }
        }

        /// <summary>
        ///     Handle package publishing for all the feed configs.
        /// </summary>
        /// <param name="client">Maestro API client</param>
        /// <param name="buildAssets">Assets information about build being published.</param>
        /// <returns>Task</returns>
        protected async Task HandlePackagePublishingAsync(Dictionary<string, HashSet<Asset>> buildAssets)
        {
            List<Task> publishTasks = new List<Task>();

            // Just log a empty line for better visualization of the logs
            Log.LogMessage(MessageImportance.High, "\nPublishing packages: ");

            foreach (var packagesPerCategory in PackagesByCategory)
            {
                var category = packagesPerCategory.Key;
                var packages = packagesPerCategory.Value;

                if (FeedConfigs.TryGetValue(category, out HashSet<TargetFeedConfig> feedConfigsForCategory))
                {
                    foreach (var feedConfig in feedConfigsForCategory)
                    {
                        HashSet<PackageArtifactModel> filteredPackages = FilterPackages(packages, feedConfig);

                        foreach (var package in filteredPackages)
                        {
                            string isolatedString = feedConfig.Isolated ? "Isolated" : "Non-Isolated";
                            string internalString = feedConfig.Internal ? $", Internal" : ", Public";
                            string shippingString = package.NonShipping ? "NonShipping" : "Shipping";
                            Log.LogMessage(MessageImportance.High, $"Package {package.Id}@{package.Version} ({shippingString}) should go to {feedConfig.TargetURL} ({isolatedString}{internalString})");
                        }

                        switch (feedConfig.Type)
                        {
                            case FeedType.AzDoNugetFeed:
                                publishTasks.Add(PublishPackagesToAzDoNugetFeedAsync(filteredPackages, buildAssets, feedConfig));
                                break;
                            case FeedType.AzureStorageFeed:
                                publishTasks.Add(PublishPackagesToAzureStorageNugetFeedAsync(filteredPackages, buildAssets, feedConfig));
                                break;
                            default:
                                Log.LogError($"Unknown target feed type for category '{category}': '{feedConfig.Type}'.");
                                break;
                        }
                    }
                }
                else
                {
                    Log.LogError($"No target feed configuration found for artifact category: '{category}'.");
                }
            }

            await Task.WhenAll(publishTasks);
        }

        private HashSet<PackageArtifactModel> FilterPackages(HashSet<PackageArtifactModel> packages, TargetFeedConfig feedConfig)
        {
            return feedConfig.AssetSelection switch
            {
                AssetSelection.All => packages,
                AssetSelection.NonShippingOnly => packages.Where(p => p.NonShipping).ToHashSet(),
                AssetSelection.ShippingOnly => packages.Where(p => !p.NonShipping).ToHashSet(),

                // Throw NIE here instead of logging an error because error would have already been logged in the
                // parser for the user.
                _ => throw new NotImplementedException($"Unknown asset selection type '{feedConfig.AssetSelection}'")
            };
        }

        protected async Task HandleBlobPublishingAsync(Dictionary<string, HashSet<Asset>> buildAssets)
        {
            List<Task> publishTasks = new List<Task>();

            // Just log a empty line for better visualization of the logs
            Log.LogMessage(MessageImportance.High, "\nPublishing blobs: ");

            foreach (var blobsPerCategory in BlobsByCategory)
            {
                var category = blobsPerCategory.Key;
                var blobs = blobsPerCategory.Value;

                if (FeedConfigs.TryGetValue(category, out HashSet<TargetFeedConfig> feedConfigsForCategory))
                {
                    foreach (var feedConfig in feedConfigsForCategory)
                    {
                        HashSet<BlobArtifactModel> filteredBlobs = FilterBlobs(blobs, feedConfig);

                        foreach (var blob in filteredBlobs)
                        {
                            string isolatedString = feedConfig.Isolated ? "Isolated" : "Non-Isolated";
                            string internalString = feedConfig.Internal ? $", Internal" : ", Public";
                            string shippingString = blob.NonShipping ? "NonShipping" : "Shipping";
                            Log.LogMessage(MessageImportance.High, $"Blob {blob.Id} ({shippingString}) should go to {feedConfig.TargetURL} ({isolatedString}{internalString})");
                        }

                        switch (feedConfig.Type)
                        {
                            case FeedType.AzDoNugetFeed:
                                publishTasks.Add(PublishBlobsToAzDoNugetFeedAsync(filteredBlobs, buildAssets, feedConfig));
                                break;
                            case FeedType.AzureStorageFeed:
                                publishTasks.Add(PublishBlobsToAzureStorageNugetFeedAsync(filteredBlobs, buildAssets, feedConfig));
                                break;
                            default:
                                Log.LogError($"Unknown target feed type for category '{category}': '{feedConfig.Type}'.");
                                break;
                        }
                    }
                }
                else
                {
                    Log.LogError($"No target feed configuration found for artifact category: '{category}'.");
                }
            }

            await Task.WhenAll(publishTasks);
        }

        /// <summary>
        ///     Filter the blobs by the feed config information
        /// </summary>
        /// <param name="blobs"></param>
        /// <param name="feedConfig"></param>
        /// <returns></returns>
        private HashSet<BlobArtifactModel> FilterBlobs(HashSet<BlobArtifactModel> blobs, TargetFeedConfig feedConfig)
        {
            return feedConfig.AssetSelection switch
            {
                AssetSelection.All => blobs,
                AssetSelection.NonShippingOnly => blobs.Where(p => p.NonShipping).ToHashSet(),
                AssetSelection.ShippingOnly => blobs.Where(p => !p.NonShipping).ToHashSet(),

                // Throw NYI here instead of logging an error because error would have already been logged in the
                // parser for the user.
                _ => throw new NotImplementedException("Unknown asset selection type '{feedConfig.AssetSelection}'")
            };
        }

        /// <summary>
        ///     Split the artifacts into categories.
        ///     
        ///     Categories are either specified explicitly when publishing (with the asset attribute "Category", separated by ';'),
        ///     or they are inferred based on the extension of the asset.
        /// </summary>
        /// <param name="buildModel"></param>
        public void SplitArtifactsInCategories(BuildModel buildModel)
        {
            foreach (var packageAsset in buildModel.Artifacts.Packages)
            {
                string categories = string.Empty;

                if (!packageAsset.Attributes.TryGetValue("Category", out categories))
                {
                    // Package artifacts don't have extensions. They are always nupkgs.
                    // Set the category explicitly to "PACKAGE"
                    categories = GeneralUtils.PackagesCategory;
                }

                foreach (var category in categories.Split(';'))
                {
                    if (!Enum.TryParse(category, ignoreCase: true, out TargetFeedContentType categoryKey))
                    {
                        Log.LogError($"Invalid target feed config category '{category}'.");
                    }

                    if (PackagesByCategory.ContainsKey(categoryKey))
                    {
                        PackagesByCategory[categoryKey].Add(packageAsset);
                    }
                    else
                    {
                        PackagesByCategory[categoryKey] = new HashSet<PackageArtifactModel>() { packageAsset };
                    }
                }
            }

            foreach (var blobAsset in buildModel.Artifacts.Blobs)
            {
                string categories = string.Empty;

                if (!blobAsset.Attributes.TryGetValue("Category", out categories))
                {
                    categories = GeneralUtils.InferCategory(blobAsset.Id, Log);
                }

                foreach (var category in categories.Split(';'))
                {
                    if (!Enum.TryParse(category, ignoreCase: true, out TargetFeedContentType categoryKey))
                    {
                        Log.LogError($"Invalid target feed config category '{category}'.");
                    }

                    if (BlobsByCategory.ContainsKey(categoryKey))
                    {
                        BlobsByCategory[categoryKey].Add(blobAsset);
                    }
                    else
                    {
                        BlobsByCategory[categoryKey] = new HashSet<BlobArtifactModel>() { blobAsset };
                    }
                }
            }
        }

        private async Task PublishPackagesToAzDoNugetFeedAsync(
            HashSet<PackageArtifactModel> packagesToPublish,
            Dictionary<string, HashSet<Asset>> buildAssets,
            TargetFeedConfig feedConfig)
        {
            await PushNugetPackagesAsync(packagesToPublish, feedConfig, maxClients: MaxClients,
                async (feed, httpClient, package, feedAccount, feedVisibility, feedName) =>
                {
                    string localPackagePath = Path.Combine(PackageAssetsBasePath, $"{package.Id}.{package.Version}.nupkg");
                    if (!File.Exists(localPackagePath))
                    {
                        Log.LogError($"Could not locate '{package.Id}.{package.Version}' at '{localPackagePath}'");
                        return;
                    }

                    TryAddAssetLocation(package.Id, package.Version, buildAssets, feedConfig, AddAssetLocationToAssetAssetLocationType.NugetFeed);

                    await PushNugetPackageAsync(feed, httpClient, localPackagePath, package.Id, package.Version, feedAccount, feedVisibility, feedName);
                });
        }

        /// <summary>
        ///     Push nuget packages to the azure devops feed.
        /// </summary>
        /// <param name="packagesToPublish">List of packages to publish</param>
        /// <param name="feedConfig">Information about feed to publish to</param>
        public async Task PushNugetPackagesAsync<T>(HashSet<T> packagesToPublish, TargetFeedConfig feedConfig, int maxClients,
            Func<TargetFeedConfig, HttpClient, T, string, string, string, Task> packagePublishAction)
        {
            if (!packagesToPublish.Any())
            {
                return;
            }

            var parsedUri = Regex.Match(feedConfig.TargetURL, PublishingConstants.AzDoNuGetFeedPattern);
            if (!parsedUri.Success)
            {
                Log.LogError($"Azure DevOps NuGetFeed was not in the expected format '{PublishingConstants.AzDoNuGetFeedPattern}'");
                return;
            }
            string feedAccount = parsedUri.Groups["account"].Value;
            string feedVisibility = parsedUri.Groups["visibility"].Value;
            string feedName = parsedUri.Groups["feed"].Value;

            using (var clientThrottle = new SemaphoreSlim(maxClients, maxClients))
            {
                using (HttpClient httpClient = new HttpClient(new HttpClientHandler { CheckCertificateRevocationList = true }))
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(180);
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                        "Basic",
                        Convert.ToBase64String(Encoding.ASCII.GetBytes(string.Format("{0}:{1}", "", feedConfig.Token))));

                    await System.Threading.Tasks.Task.WhenAll(packagesToPublish.Select(async packageToPublish =>
                    {
                        try
                        {
                            // Wait to avoid starting too many processes.
                            await clientThrottle.WaitAsync();
                            await packagePublishAction(feedConfig, httpClient, packageToPublish, feedAccount, feedVisibility, feedName);
                        }
                        finally
                        {
                            clientThrottle.Release();
                        }
                    }));
                }
            }
        }

        /// <summary>
        ///     Push a single package to the azure devops nuget feed.
        /// </summary>
        /// <param name="feedConfig">Infos about the target feed</param>
        /// <param name="packageToPublish">Package to push</param>
        /// <returns>Task</returns>
        /// <remarks>
        ///     This method attempts to take the most efficient path to push the package.
        ///     
        ///     There are three cases:
        ///         - The package does not exist, and is pushed normally
        ///         - The package exists, and its contents may or may not be equivalent.
        ///         - Azure DevOps is having some issue and we didn't succeed to publish at first.
        ///         
        ///     The second case is by far the most common. So, we first attempt to push the 
        ///     package normally using nuget.exe. If this fails, this could mean any number of 
        ///     things (like failed auth). But in normal circumstances, this might mean the 
        ///     package already exists. This either means that we are attempting to push the 
        ///     same package, or attemtping to push a different package with the same id and 
        ///     version. The second case is an error, as azure devops feeds are immutable, 
        ///     the former is simply a case where we should continue onward.
        ///     
        ///     To handle the third case we rely on the call to compare file contents 
        ///     `IsLocalPackageIdenticalToFeedPackage` to return null - meaning that it got 
        ///     a 404 when looking up the file in the feed - to trigger a retry on the publish 
        ///     operation. This was implemented this way becase we didn't want to rely on 
        ///     parsing the output of the push operation - which does a call to `nuget.exe` 
        ///     behind the scenes.
        /// </remarks>
        private async Task PushNugetPackageAsync(TargetFeedConfig feedConfig, HttpClient client, string localPackageLocation, string id, string version,
            string feedAccount, string feedVisibility, string feedName)
        {
            try
            {
                /// true - Package exists on the feed AND is identical to local one.
                /// false - Package exists on the feed AND is not identical to local one.
                /// null - Package DOES NOT EXIST on the feed.
                bool? packageExistIsIdentical = null;
                const int maxNuGetPushAttempts = 1;
                const int delayInSecondsBetweenAttempts = 3;
                int attemptIndex = 0;

                do
                {
                    // The feed key when pushing to AzDo feeds is "AzureDevOps" (works with the credential helper).
                    int result = await GeneralUtils.StartProcessAsync(NugetPath, $"push \"{localPackageLocation}\" -Source \"{feedConfig.TargetURL}\" -NonInteractive -ApiKey AzureDevOps -Verbosity quiet");

                    if (result == 0)
                    {
                        break;
                    }

                    Log.LogMessage(MessageImportance.Low, $"Failed to push {localPackageLocation}, attempting to determine whether the package already exists on the feed with the same content.");

                    string packageContentUrl = $"https://pkgs.dev.azure.com/{feedAccount}/{feedVisibility}_apis/packaging/feeds/{feedName}/nuget/packages/{id}/versions/{version}/content";
                    packageExistIsIdentical = await GeneralUtils.IsLocalPackageIdenticalToFeedPackage(localPackageLocation, packageContentUrl, client, Log);

                    if (packageExistIsIdentical == true)
                    {
                        Log.LogMessage(MessageImportance.Normal, $"Package '{localPackageLocation}' already exists on '{feedConfig.TargetURL}' but has the same content. Skipping.");
                    }
                    else if (packageExistIsIdentical == false)
                    {
                        Log.LogError($"Package '{localPackageLocation}' already exists on '{feedConfig.TargetURL}' with different content.");
                    }
                    else
                    {
                        // packageExist == null, which means we didn't find the package on the feed
                        // will retry the push and check again
                        await Task.Delay(TimeSpan.FromSeconds(delayInSecondsBetweenAttempts))
                            .ConfigureAwait(false);
                    }
                }
                while (packageExistIsIdentical == null && attemptIndex++ <= maxNuGetPushAttempts);

                if (attemptIndex > maxNuGetPushAttempts)
                {
                    Log.LogError($"Failed to publish package '{id}@{version}' to '{feedConfig.TargetURL}' after {maxNuGetPushAttempts} attempts.");
                }
                else if (!Log.HasLoggedErrors)
                {
                    Log.LogMessage(MessageImportance.High, $"Succeeded publishing package '{localPackageLocation}' to feed {feedConfig.TargetURL}");
                }
            }
            catch (Exception e)
            {
                Log.LogError($"Unexpected exception pushing package '{id}@{version}': {e.Message}");
            }
        }

        private async Task PublishBlobsToAzDoNugetFeedAsync(
            HashSet<BlobArtifactModel> blobsToPublish,
            Dictionary<string, HashSet<Asset>> buildAssets,
            TargetFeedConfig feedConfig)
        {
            HashSet<BlobArtifactModel> packagesToPublish = new HashSet<BlobArtifactModel>();

            foreach (var blob in blobsToPublish)
            {
                // Applies to symbol packages and core-sdk's VS feed packages
                if (blob.Id.EndsWith(GeneralUtils.PackageSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    packagesToPublish.Add(blob);
                }
                else
                {
                    Log.LogWarning($"AzDO feed publishing not available for blobs. Blob '{blob.Id}' was not published.");
                }
            }

            await PushNugetPackagesAsync<BlobArtifactModel>(packagesToPublish, feedConfig, maxClients: MaxClients,
                async (feed, httpClient, blob, feedAccount, feedVisibility, feedName) =>
                {
                    if (TryAddAssetLocation(blob.Id, assetVersion: null, buildAssets, feedConfig, AddAssetLocationToAssetAssetLocationType.Container))
                    {
                        // Determine the local path to the blob
                        string fileName = Path.GetFileName(blob.Id);
                        string localBlobPath = Path.Combine(BlobAssetsBasePath, fileName);
                        if (!File.Exists(localBlobPath))
                        {
                            Log.LogError($"Could not locate '{blob.Id} at '{localBlobPath}'");
                            return;
                        }

                        string id;
                        string version;
                        // Determine package ID and version by asking the nuget libraries
                        using (var packageReader = new NuGet.Packaging.PackageArchiveReader(localBlobPath))
                        {
                            PackageIdentity packageIdentity = packageReader.GetIdentity();
                            id = packageIdentity.Id;
                            version = packageIdentity.Version.ToString();
                        }

                        await PushNugetPackageAsync(feed, httpClient, localBlobPath, id, version, feedAccount, feedVisibility, feedName);
                    }
                });
        }

        private async Task PublishPackagesToAzureStorageNugetFeedAsync(
            HashSet<PackageArtifactModel> packagesToPublish,
            Dictionary<string, HashSet<Asset>> buildAssets,
            TargetFeedConfig feedConfig)
        {
            var packages = packagesToPublish.Select(p =>
            {
                var localPackagePath = Path.Combine(PackageAssetsBasePath, $"{p.Id}.{p.Version}.nupkg");
                if (!File.Exists(localPackagePath))
                {
                    Log.LogError($"Could not locate '{p.Id}.{p.Version}' at '{localPackagePath}'");
                }
                return localPackagePath;
            });

            if (Log.HasLoggedErrors)
            {
                return;
            }

            var blobFeedAction = CreateBlobFeedAction(feedConfig);

            var pushOptions = new PushOptions
            {
                AllowOverwrite = feedConfig.AllowOverwrite,
                PassIfExistingItemIdentical = true
            };

            packagesToPublish
                .ToList()
                .ForEach(package => TryAddAssetLocation(package.Id, package.Version, buildAssets, feedConfig, AddAssetLocationToAssetAssetLocationType.NugetFeed));

            await blobFeedAction.PushToFeedAsync(packages, pushOptions);
        }

        private async Task PublishBlobsToAzureStorageNugetFeedAsync(
            HashSet<BlobArtifactModel> blobsToPublish,
            Dictionary<string, HashSet<Asset>> buildAssets,
            TargetFeedConfig feedConfig)
        {
            var blobs = blobsToPublish
                .Select(blob =>
                {
                    var fileName = Path.GetFileName(blob.Id);
                    var localBlobPath = Path.Combine(BlobAssetsBasePath, fileName);
                    if (!File.Exists(localBlobPath))
                    {
                        Log.LogError($"Could not locate '{blob.Id} at '{localBlobPath}'");
                    }

                    return new Microsoft.Build.Utilities.TaskItem(localBlobPath, new Dictionary<string, string>
                    {
                        {"RelativeBlobPath", blob.Id}
                    });
                })
                .ToArray();

            if (Log.HasLoggedErrors)
            {
                return;
            }

            var blobFeedAction = CreateBlobFeedAction(feedConfig);
            var pushOptions = new PushOptions
            {
                AllowOverwrite = feedConfig.AllowOverwrite,
                PassIfExistingItemIdentical = true
            };

            blobsToPublish
                .ToList()
                .ForEach(blob => TryAddAssetLocation(blob.Id, assetVersion: null, buildAssets, feedConfig, AddAssetLocationToAssetAssetLocationType.Container));

            await blobFeedAction.PublishToFlatContainerAsync(blobs, maxClients: MaxClients, pushOptions);

            if (LinkManager == null)
            {
                LinkManager = new LatestLinksManager(AkaMSClientId, AkaMSClientSecret, AkaMSTenant, AkaMSGroupOwner, AkaMSCreatedBy, AkaMsOwners, Log);
            }

            // The latest links should be updated only after the publishing is complete, to avoid
            // dead links in the interim.
            await LinkManager.CreateOrUpdateLatestLinksAsync(blobsToPublish, feedConfig, PublishingConstants.ExpectedFeedUrlSuffix.Length);
        }

        private BlobFeedAction CreateBlobFeedAction(TargetFeedConfig feedConfig)
        {
            var proxyBackedFeedMatch = Regex.Match(feedConfig.TargetURL, PublishingConstants.AzureStorageProxyFeedPattern);
            var proxyBackedStaticFeedMatch = Regex.Match(feedConfig.TargetURL, PublishingConstants.AzureStorageProxyFeedStaticPattern);
            var azureStorageStaticBlobFeedMatch = Regex.Match(feedConfig.TargetURL, PublishingConstants.AzureStorageStaticBlobFeedPattern);

            if (proxyBackedFeedMatch.Success || proxyBackedStaticFeedMatch.Success)
            {
                var regexMatch = (proxyBackedFeedMatch.Success) ? proxyBackedFeedMatch : proxyBackedStaticFeedMatch;
                var containerName = regexMatch.Groups["container"].Value;
                var baseFeedName = regexMatch.Groups["baseFeedName"].Value;
                var feedURL = regexMatch.Groups["feedURL"].Value;
                var storageAccountName = "dotnetfeed";

                // Initialize the feed using sleet
                SleetSource sleetSource = new SleetSource()
                {
                    Name = baseFeedName,
                    Type = "azure",
                    BaseUri = feedURL,
                    AccountName = storageAccountName,
                    Container = containerName,
                    FeedSubPath = baseFeedName,
                    ConnectionString = $"DefaultEndpointsProtocol=https;AccountName={storageAccountName};AccountKey={feedConfig.Token};EndpointSuffix=core.windows.net"
                };

                return new BlobFeedAction(sleetSource, feedConfig.Token, Log);
            }
            else if (azureStorageStaticBlobFeedMatch.Success)
            {
                return new BlobFeedAction(feedConfig.TargetURL, feedConfig.Token, Log);
            }
            else
            {
                Log.LogError($"Could not parse Azure feed URL: '{feedConfig.TargetURL}'");
                return null;
            }
        }

        protected bool AnyMissingRequiredProperty()
        {
            foreach (PropertyInfo prop in this.GetType().GetProperties())
            {
                RequiredAttribute attribute =
                    (RequiredAttribute)Attribute.GetCustomAttribute(prop, typeof(RequiredAttribute));

                if (attribute != null)
                {
                    string value = prop.GetValue(this, null)?.ToString();

                    if (string.IsNullOrEmpty(value))
                    {
                        Log.LogError($"The property {prop.Name} is required but doesn't have a value set.");
                    }
                }
            }

            return Log.HasLoggedErrors;
        }
    }
}
