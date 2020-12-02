﻿using Azure.Identity;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Cosmos;
using Migration.Shared;
using Migration.Shared.DataContracts;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Migration.Monitor.WebJob
{
    class Program
    {
        public const string MigrationClientUserAgentPrefix = "MigrationMonitor.MigrationMetadata";
        public const string SourceClientUserAgentPrefix = "MigrationMonitor.Source";
        public const string DestinationClientUserAgentPrefix = "MigrationMonitor.Destination";

        const int SleepTime = 10000;
        const int MaxConcurrentMonitoringJobs = 5;

        private static readonly string keyVaultUri = ConfigurationManager.AppSettings["keyvaulturi"];
        private static readonly string migrationMetadataAccount = ConfigurationManager.AppSettings["cosmosdbaccount"];
        private static readonly string migrationDetailsDB = ConfigurationManager.AppSettings["cosmosdbdb"];
        private static readonly string migrationDetailsColl = ConfigurationManager.AppSettings["cosmosdbcollection"];
        private static readonly string appInsightsInstrumentationKey =
            ConfigurationManager.AppSettings["appinsightsinstrumentationkey"];

        private static readonly Dictionary<string, CosmosClient> sourceClients = 
            new Dictionary<string, CosmosClient>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, CosmosClient> destinationClients =
            new Dictionary<string, CosmosClient>(StringComparer.OrdinalIgnoreCase);

#pragma warning disable IDE0060 // Remove unused parameter
        static void Main(string[] args)
#pragma warning restore IDE0060 // Remove unused parameter
        {
            TelemetryConfiguration telemetryConfig = new TelemetryConfiguration(appInsightsInstrumentationKey);
            TelemetryHelper.Initilize(telemetryConfig);

            KeyVaultHelper.Initialize(new Uri(keyVaultUri), new DefaultAzureCredential());

            RunAsync().Wait();
        }

        private static async Task RunAsync()
        {
            SemaphoreSlim concurrencySemaphore = new SemaphoreSlim(MaxConcurrentMonitoringJobs);

            using (CosmosClient client =
               KeyVaultHelper.Singleton.CreateCosmosClientFromKeyVault(
                   migrationMetadataAccount,
                   MigrationClientUserAgentPrefix,
                   useBulk: false,
                   retryOn429Forever: true))
            {
                Database db = await client
                    .CreateDatabaseIfNotExistsAsync(migrationDetailsDB)
                    .ConfigureAwait(false);

                Container container =await db
                    .CreateContainerIfNotExistsAsync(new ContainerProperties(migrationDetailsColl, "/id"))
                    .ConfigureAwait(false);

                while (true)
                {
                    List<MigrationConfig> configDocs = new List<MigrationConfig>();
                    FeedIterator<MigrationConfig> iterator = container.GetItemQueryIterator<MigrationConfig>(
                        "select * from c where NOT c.completed");

                    while (iterator.HasMoreResults)
                    {
                        FeedResponse<MigrationConfig> response = await iterator.ReadNextAsync().ConfigureAwait(false);
                        configDocs.AddRange(response.Resource);
                    }

                    if (configDocs.Count == 0)
                    {
                        TelemetryHelper.Singleton.LogInfo(
                            "No Migration to monitor for process '{0}'",
                            Process.GetCurrentProcess().Id);
                    }
                    else
                    {
                        TelemetryHelper.Singleton.LogInfo(
                            "Starting to monitor migration by process '{0}'",
                            Process.GetCurrentProcess().Id);

                        Task[] tasks = new Task[configDocs.Count];
                        for (int i = 0; i < tasks.Length; i++)
                        {
                            await concurrencySemaphore.WaitAsync().ConfigureAwait(false);
                            await TrackMigrationProgressAsync(container, configDocs[i], concurrencySemaphore)
                                .ConfigureAwait(false);
                        }
                    }

                    await Task.Delay(SleepTime).ConfigureAwait(false);
                }
            }
        }

        private static async Task<long> GetDoucmentCountAsync(Container container)
        {
            if (container == null) { throw new ArgumentNullException(nameof(container)); }

            FeedIterator<long> iterator = container.GetItemQueryIterator<long>(
                "SELECT VALUE COUNT(1) FROM c");

            return (await iterator.ReadNextAsync().ConfigureAwait(false)).Resource.Single();
        }

        private static async Task TrackMigrationProgressAsync(
            Container migrationContainer,
            MigrationConfig migrationConfig,
            SemaphoreSlim concurrencySempahore)
        {
            if (migrationContainer== null) { throw new ArgumentNullException(nameof(migrationContainer)); }
            if (migrationConfig == null) { throw new ArgumentNullException(nameof(migrationConfig)); }
            if (concurrencySempahore == null) { throw new ArgumentNullException(nameof(concurrencySempahore)); }

            try
            {
                CosmosClient sourceClient = GetOrCreateSourceCosmosClient(migrationConfig.MonitoredAccount);
                CosmosClient destinationClient = GetOrCreateSourceCosmosClient(migrationConfig.DestAccount);
                Container sourceContainer = sourceClient.GetContainer(
                    migrationConfig.MonitoredDbName, 
                    migrationConfig.MonitoredCollectionName);
                Container destinationContainer = destinationClient.GetContainer(
                    migrationConfig.DestDbName,
                    migrationConfig.DestCollectionName);

                while (true)
                {
                    MigrationConfig migrationConfigSnapshot = await migrationContainer
                        .ReadItemAsync<MigrationConfig>(migrationConfig.Id, new PartitionKey(migrationConfig.Id))
                        .ConfigureAwait(false);

                    DateTimeOffset now = DateTimeOffset.UtcNow;

                    long sourceCollectionCount = await GetDoucmentCountAsync(sourceContainer).ConfigureAwait(false);
                    long currentDestinationCollectionCount = await GetDoucmentCountAsync(destinationContainer)
                        .ConfigureAwait(false);
                    double currentPercentage = sourceCollectionCount == 0 ? 
                        100 : 
                        currentDestinationCollectionCount * 100.0 / sourceCollectionCount;
                    long insertedCount = 
                        currentDestinationCollectionCount - migrationConfigSnapshot.MigratedDocumentCount;
                    double currentRate = insertedCount * 1000.0 / SleepTime;
                    DateTime currentTime = DateTime.UtcNow;
                    long nowEpochMs = now.ToUnixTimeMilliseconds();
                    long totalSeconds = (nowEpochMs - migrationConfigSnapshot.StartTimeEpochMs) / 1000;
                    double averageRate = currentDestinationCollectionCount / totalSeconds;
                    double eta = (sourceCollectionCount - currentDestinationCollectionCount) * 1000 / (averageRate * 3600);
                    long etaMs = averageRate == 0 ? 0: (long)eta;

                    migrationConfigSnapshot.ExpectedDurationLeft = etaMs;
                    migrationConfigSnapshot.AvgRate = averageRate;
                    migrationConfigSnapshot.CurrentRate = currentRate;
                    migrationConfigSnapshot.SourceCountSnapshot = sourceCollectionCount;
                    migrationConfigSnapshot.DestinationCountSnapshot = currentDestinationCollectionCount;
                    migrationConfigSnapshot.PercentageCompleted = currentPercentage;
                    migrationConfigSnapshot.StatisticsLastUpdatedEpochMs = nowEpochMs;

                    try
                    {
                        ItemResponse<MigrationConfig> response = await migrationContainer
                            .ReplaceItemAsync(
                                migrationConfigSnapshot,
                                migrationConfigSnapshot.Id,
                                new PartitionKey(migrationConfigSnapshot.Id),
                                new ItemRequestOptions
                                {
                                    IfMatchEtag = migrationConfigSnapshot.ETag
                                })
                            .ConfigureAwait(false);
                    }
                    catch (CosmosException error)
                    {
                        if (error.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
                        {
                            continue;
                        }

                        throw;
                    }

                    TelemetryHelper.Singleton.TrackStatistics(
                        sourceCollectionCount,
                        currentDestinationCollectionCount,
                        currentPercentage,
                        currentRate,
                        averageRate,
                        eta);

                    return;
                }
            }
            finally
            {
                concurrencySempahore.Release();
            }
        }

        private static CosmosClient GetOrCreateSourceCosmosClient(string accountName)
        {
            return GetOrCreateCosmosClient(
                sourceClients,
                SourceClientUserAgentPrefix,
                accountName);
        }

        private static CosmosClient GetOrCreateDestinationCosmosClient(string accountName)
        {
            return GetOrCreateCosmosClient(
                destinationClients,
                DestinationClientUserAgentPrefix,
                accountName);
        }

        private static  CosmosClient GetOrCreateCosmosClient(
            Dictionary<string, CosmosClient> cache,
            string userAgentPrefix,
            string accountName)
        {
            if (cache == null) { throw new ArgumentNullException(nameof(cache)); }
            if (String.IsNullOrWhiteSpace(accountName)) { throw new ArgumentNullException(nameof(accountName)); }

            lock (cache)
            {
                if (cache.TryGetValue(accountName, out CosmosClient client))
                {
                    return client;
                }

                client = KeyVaultHelper.Singleton.CreateCosmosClientFromKeyVault(
                    accountName,
                    userAgentPrefix,
                    useBulk: false,
                    retryOn429Forever: true);
                cache.Add(accountName, client);

                return client;
            }
        }
    }
}
