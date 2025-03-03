﻿using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;
using Migration.Shared;
using Migration.Shared.DataContracts;
using Newtonsoft.Json;

namespace Migration.Executor.WebJob
{
    public class ChangeFeedProcessorHost
    {
        private static readonly Regex failedDocLineFeedRemoverRegex =
            new Regex(@"\\r\\n?|\\n?|\\\?|\\", RegexOptions.Compiled);

        private readonly CosmosClient destinationCollectionClient;
        private readonly CosmosClient sourceCollectionClient;
        private readonly CosmosClient leaseCollectionClient;
        private readonly string SourcePartitionKeys;
        private readonly string TargetPartitionKey;
        private readonly BlobContainerClient deadletterClient;
        private readonly string processorName;

        private readonly MigrationConfig config;
        private ChangeFeedProcessor changeFeedProcessor;
        private Container containerToStoreDocuments;

        public ChangeFeedProcessorHost(MigrationConfig config)
        {
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.SourcePartitionKeys = config.SourcePartitionKeys;
            this.TargetPartitionKey = config.TargetPartitionKey;

            this.leaseCollectionClient = KeyVaultHelper.Singleton.CreateCosmosClientFromKeyVault(
                    EnvironmentConfig.Singleton.MigrationMetadataCosmosAccountName,
                    Program.MigrationClientUserAgentPrefix,
                    useBulk: false,
                    retryOn429Forever: true);

            this.sourceCollectionClient = KeyVaultHelper.Singleton.CreateCosmosClientFromKeyVault(
                    config.MonitoredAccount,
                    Program.SourceClientUserAgentPrefix,
                    useBulk: false,
                    retryOn429Forever: true);

            this.destinationCollectionClient = KeyVaultHelper.Singleton.CreateCosmosClientFromKeyVault(
                    config.DestAccount,
                    Program.DestinationClientUserAgentPrefix,
                    useBulk: true,
                    retryOn429Forever: true);

            this.deadletterClient = KeyVaultHelper.Singleton.GetBlobContainerClientFromKeyVault(
                EnvironmentConfig.Singleton.DeadLetterAccountName,
                config.Id?.ToLowerInvariant().Replace("-", String.Empty));

            // Make sure to allow multiple active migrations for the same source container
            // by creating a unique processor name for every config document
            this.processorName = config.ProcessorName;
        }

        public async Task StartAsync()
        {
            try
            {
                TelemetryHelper.Singleton.LogInfo(
                   "Starting lease (transaction log of change feed) standard collection creation: ProcessorName: {0} - Url {1} - dbName {2} - collectionName {3}",
                   this.processorName,
                   EnvironmentConfig.Singleton.MigrationMetadataCosmosAccountName,
                   EnvironmentConfig.Singleton.MigrationMetadataDatabaseName,
                   EnvironmentConfig.Singleton.MigrationLeasesContainerName);

                await this.CreateCollectionIfNotExistsAsync(
                    this.leaseCollectionClient,
                    EnvironmentConfig.Singleton.MigrationMetadataDatabaseName,
                    EnvironmentConfig.Singleton.MigrationLeasesContainerName,
                    "id").ConfigureAwait(false);

                TelemetryHelper.Singleton.LogInfo(
                    "ProcessorName {0} - destination (sink) collection : Url {1} - dbName {2} - collectionName {2}",
                    this.processorName,
                    this.config.DestAccount,
                    this.config.DestDbName,
                    this.config.DestCollectionName);

                this.containerToStoreDocuments = await this.CreateCollectionIfNotExistsAsync(
                    this.destinationCollectionClient,
                    this.config.DestDbName,
                    this.config.DestCollectionName,
                    this.config.TargetPartitionKey).ConfigureAwait(false);

                await this.RunChangeFeedHostAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                TelemetryHelper.Singleton.LogError(
                    "Attempt to start the changefeed processor {0} failed: {1}",
                    this.processorName,
                    error);

                throw;
            }
        }

        public async Task<Container> CreateCollectionIfNotExistsAsync(
            CosmosClient client,
            string databaseName,
            string collectionName,
            string partitionKey)
        {
            Database db = await client.CreateDatabaseIfNotExistsAsync(databaseName).ConfigureAwait(false);

            string effectivePKDefinition = partitionKey ?? "id";
            if (!effectivePKDefinition.StartsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                effectivePKDefinition = String.Concat("/", effectivePKDefinition);
            }

            return await db
                .CreateContainerIfNotExistsAsync(collectionName, effectivePKDefinition)
                .ConfigureAwait(false);
        }

        public async Task CloseAsync()
        {
            ChangeFeedProcessor processorSnapshot = this.changeFeedProcessor;
            if (processorSnapshot != null)
            {
                await processorSnapshot.StopAsync().ConfigureAwait(false);
            }

            CosmosClient clientSnapshot = this.destinationCollectionClient;

            if (clientSnapshot != null)
            {
                clientSnapshot.Dispose();
            }

            clientSnapshot = this.sourceCollectionClient;

            if (clientSnapshot != null)
            {
                clientSnapshot.Dispose();
            }

            clientSnapshot = this.destinationCollectionClient;

            if (clientSnapshot != null)
            {
                clientSnapshot.Dispose();
            }

            this.containerToStoreDocuments = null;
            this.changeFeedProcessor = null;
        }

        public async Task<ChangeFeedProcessor> RunChangeFeedHostAsync()
        {
            string hostName = Guid.NewGuid().ToString();
            TelemetryHelper.Singleton.LogInfo("ProcessorName {0} - Host name {1}", this.processorName, hostName);

            DefaultDocumentTransformer docTransformer = new DefaultDocumentTransformer();

            DateTime starttime = DateTime.MinValue.ToUniversalTime();
            if (this.config.DataAgeInHours.HasValue)
            {
                if (this.config.DataAgeInHours.Value >= 0)
                {
                    starttime = DateTime.UtcNow.Subtract(TimeSpan.FromHours(this.config.DataAgeInHours.Value));
                }
            }

            this.changeFeedProcessor = this.sourceCollectionClient.GetContainer(this.config.MonitoredDbName, this.config.MonitoredCollectionName)
                .GetChangeFeedProcessorBuilder<DocumentMetadata>(this.processorName, this.ProcessChangesAsync)
                .WithInstanceName(hostName)
                .WithLeaseContainer(
                    this.leaseCollectionClient.GetContainer(
                        EnvironmentConfig.Singleton.MigrationMetadataDatabaseName,
                        EnvironmentConfig.Singleton.MigrationLeasesContainerName))
                .WithLeaseConfiguration(TimeSpan.FromSeconds(30))
                .WithStartTime(starttime)
                .WithMaxItems(1000)
                .Build();

            TelemetryHelper.Singleton.LogInfo(
                "Starting changefeed processor '{0}' on host '{1}'",
                this.processorName,
                hostName);
            await this.changeFeedProcessor.StartAsync().ConfigureAwait(false);

            return this.changeFeedProcessor;
        }

        private async Task ProcessChangesAsync(IReadOnlyCollection<DocumentMetadata> docs, CancellationToken cancellationToken)
        {
            try
            {
                Boolean isSyntheticKey = this.SourcePartitionKeys.Contains(",");
                Boolean isNestedAttribute = this.SourcePartitionKeys.Contains("/");
                Container targetContainer = this.destinationCollectionClient.GetContainer(this.config.DestDbName, this.config.DestCollectionName);
                this.containerToStoreDocuments = targetContainer;
                DocumentMetadata document;
                BulkOperations<DocumentMetadata> bulkOperations = new BulkOperations<DocumentMetadata>(docs.Count);
                foreach (DocumentMetadata doc in docs)
                {
                    document = (this.SourcePartitionKeys != null & this.TargetPartitionKey != null) ?
                        MapPartitionKey(doc, isSyntheticKey, this.TargetPartitionKey, isNestedAttribute, this.SourcePartitionKeys) :
                        document = doc;
                    if (this.config.OnlyInsertMissingItems)
                    {
                        bulkOperations.Tasks.Add(this.containerToStoreDocuments.CreateItemAsync(
                            item: document,
                            cancellationToken: cancellationToken).CaptureOperationResponse(document, ignoreConflicts: true));
                    }
                    else
                    {
                        bulkOperations.Tasks.Add(this.containerToStoreDocuments.UpsertItemAsync(
                            item: document,
                            cancellationToken: cancellationToken).CaptureOperationResponse(document, ignoreConflicts: true));
                    }
                }
                BulkOperationResponse<DocumentMetadata> bulkOperationResponse = await bulkOperations.ExecuteAsync().ConfigureAwait(false);
                if (bulkOperationResponse.Failures.Count > 0 && this.deadletterClient != null)
                {
                    await this.WriteFailedDocsToBlob("FailedImportDocs", this.deadletterClient, bulkOperationResponse)
                        .ConfigureAwait(false);
                }
                TelemetryHelper.Singleton.LogMetrics(bulkOperationResponse);
            }
            catch (Exception error)
            {
                TelemetryHelper.Singleton.LogError(
                    "Processing changes in change feed processor {0} failed: {1}",
                    this.processorName,
                    error);

                throw;
            }
        }

        private async Task WriteFailedDocsToBlob(
            string failureType,
            BlobContainerClient containerClient,
            BulkOperationResponse<DocumentMetadata> bulkOperationResponse)
        {
            try
            {
                string failedDocs;
                string failures;
                byte[] byteArray;
                BlobClient blobClient = containerClient.GetBlobClient(failureType + Guid.NewGuid().ToString() + ".csv");

                failures = JsonConvert.SerializeObject(String.Join(",", bulkOperationResponse.DocFailures));
                failedDocs = JsonConvert.SerializeObject(String.Join(",", bulkOperationResponse.FailedDocs));
                failedDocs = failedDocLineFeedRemoverRegex.Replace(failedDocs, String.Empty);
                byteArray = Encoding.ASCII.GetBytes(failures + "|" + bulkOperationResponse.Failures.Count + "|" + failedDocs);

                using (MemoryStream ms = new MemoryStream(byteArray))
                {
                    await blobClient
                        .UploadAsync(ms, overwrite: true)
                        .ConfigureAwait(false);
                }

                TelemetryHelper.Singleton.LogWarning(
                    "Processor {0} - FAILED TO INGEST DOCUMENTS: Writing {1} failed documents to the deadletter blob store.",
                    this.processorName,
                    bulkOperationResponse.FailedDocs.Count);
            }
            catch (Exception error)
            {
                TelemetryHelper.Singleton.LogError(
                    "Change feed processor {0} - Writing document to deadletter blob store failed: {1}",
                    this.processorName,
                    error);

                throw;
            }
        }

        public static DocumentMetadata MapPartitionKey(
            DocumentMetadata doc,
            Boolean isSyntheticKey,
            string targetPartitionKey,
            Boolean isNestedAttribute,
            string sourcePartitionKeys)
        {
            if (isSyntheticKey)
            {
                doc = CreateSyntheticKey(doc, sourcePartitionKeys, isNestedAttribute, targetPartitionKey);
            }
            else
            {
                doc.SetPropertyValue(targetPartitionKey, isNestedAttribute == true ? GetNestedValue(doc, sourcePartitionKeys) : doc.GetPropertyValue<string>(sourcePartitionKeys));
            }

            return doc;
        }

        public static DocumentMetadata CreateSyntheticKey(
            DocumentMetadata doc,
            string sourcePartitionKeys,
            Boolean isNestedAttribute,
            string targetPartitionKey)
        {

            string resultKey = "";
            string key = doc.GetPropertyValue<string>("key");
            if (key.EndsWith("tenants"))
            {
                string val = doc.GetPropertyValue<string>("id");
                resultKey = "tn-" + val;
            }
            else if (key.EndsWith("subprojects"))
            {
                string val = doc.GetPropertyValue<string>("id");
                resultKey = "sp-" + val;
            }
            else if (key.EndsWith("apps"))
            {
                string val = doc.GetPropertyValue<string>("id");
                resultKey = "ap-" + val;
            }
            else
            {

                string tenant = GetNestedValue(doc, "data/tenant");
                string subproject = GetNestedValue(doc, "data/subproject");
                string path = GetNestedValue(doc, "data/path");
                string name = GetNestedValue(doc, "data/name");

                using (SHA512 sha512Hash = SHA512.Create())
                {
                    byte[] sourceBytes = Encoding.UTF8.GetBytes(path + name);
                    byte[] hashBytes = sha512Hash.ComputeHash(sourceBytes);
                    string hash = BitConverter.ToString(hashBytes).Replace("-", String.Empty);
                    hash = hash.ToLower();

                    resultKey = "ds-" + tenant + "-" + subproject + "-" + hash;

                }
            }

            doc.SetPropertyValue(targetPartitionKey, resultKey);

            return doc;
        }

        public static string GetNestedValue(DocumentMetadata doc, string path)
        {
            System.Xml.XmlDictionaryReader jsonReader = JsonReaderWriterFactory.CreateJsonReader(
                Encoding.UTF8.GetBytes(doc.ToString()),
                new System.Xml.XmlDictionaryReaderQuotas());
            XElement root = XElement.Load(jsonReader);
            string value = root.XPathSelectElement("//" + path).Value;
            return value;
        }
    }
}