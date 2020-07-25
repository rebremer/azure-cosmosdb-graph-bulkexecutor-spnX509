﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace GraphBulkImportSample
{
    using System;
    using System.Configuration;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;

    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Client;
    using Microsoft.Azure.CosmosDB.BulkExecutor;
    using Microsoft.Azure.CosmosDB.BulkExecutor.BulkImport;
    using Microsoft.Azure.CosmosDB.BulkExecutor.Graph;
    using System.Security.Cryptography.X509Certificates;
    using System.Linq;
    using Microsoft.Identity.Client;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    class Program
    {
        private static readonly string EndpointUrl = ConfigurationManager.AppSettings["EndPointUrl"];
        //private static readonly string AuthorizationKey = ConfigurationManager.AppSettings["AuthorizationKey"];

        private static readonly string DatabaseName = ConfigurationManager.AppSettings["DatabaseName"];
        private static readonly string CollectionName = ConfigurationManager.AppSettings["CollectionName"];
        private static readonly int CollectionThroughput = int.Parse(ConfigurationManager.AppSettings["CollectionThroughput"]);

        private static readonly string ThumbprintCertificate = ConfigurationManager.AppSettings["ThumbprintCertificate"];
        private static readonly string ClientId = ConfigurationManager.AppSettings["ClientId"];
        private static readonly string Authority = ConfigurationManager.AppSettings["Authority"];
        private static readonly string KeyvaultURLSecretId = ConfigurationManager.AppSettings["KeyvaultURLSecretId"];        

        private static string AuthorizationKey = getCosmosDBKey();

        private static readonly ConnectionPolicy ConnectionPolicy = new ConnectionPolicy
        {
            ConnectionMode = ConnectionMode.Direct,
            ConnectionProtocol = Protocol.Tcp
        };

        private DocumentClient client;

        /// <summary>
        /// Initializes a new instance of the <see cref="Program"/> class.
        /// </summary>
        /// <param name="client">The DocumentDB client instance.</param>
        private Program(DocumentClient client)
        {
            this.client = client;
        }


        public static void Main(string[] args)
        {
            Trace.WriteLine("Summary:");
            Trace.WriteLine("--------------------------------------------------------------------- ");
            Trace.WriteLine(String.Format("Endpoint: {0}", EndpointUrl));
            Trace.WriteLine(String.Format("Collection : {0}.{1}", DatabaseName, CollectionName));
            Trace.WriteLine("--------------------------------------------------------------------- ");
            Trace.WriteLine("");

            try
            {
                using (var client = new DocumentClient(
                    new Uri(EndpointUrl),
                    AuthorizationKey,
                    ConnectionPolicy))
                {
                    var program = new Program(client);
                    program.RunBulkImportAsync().Wait();
                }
            }
            catch (AggregateException e)
            {
                Trace.TraceError("Caught AggregateException in Main, Inner Exception:\n" + e);
                Console.ReadKey();
            }

        }

        private static string getCosmosDBKey()
        {
            // 1. Get certificate from local certificate store using thumbprint
            X509Certificate2 certificate = ReadCertificate();
            
            // 2. Authenticate to SPN using certificate
            IConfidentialClientApplication app;
            app = ConfidentialClientApplicationBuilder.Create(ClientId)
                .WithCertificate(certificate)
                .WithAuthority(new Uri(Authority))
                .Build();

            // 3. Create bearer token using SPN with scope key vault
            string[] scopes = new string[] {"https://vault.azure.net/.default"};
            AuthenticationResult resultToken = app.AcquireTokenForClient(scopes).ExecuteAsync().Result;

            // 4. Retrieve Cosmos DB key from key vault using REST
            var httpClient = new HttpClient();
            var defaultRequestHeaders = httpClient.DefaultRequestHeaders;
            defaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", resultToken.AccessToken);

            //5. Parse result and return Cosmos DB key
            HttpResponseMessage response = httpClient.GetAsync(KeyvaultURLSecretId).Result;
            JObject result = null;
            if (response.IsSuccessStatusCode)
            {
                string json = response.Content.ReadAsStringAsync().Result;
                result = JsonConvert.DeserializeObject(json) as JObject;
            }

            return (string) result["value"];
        }

        private static X509Certificate2 ReadCertificate()
        {
            if (string.IsNullOrWhiteSpace(ThumbprintCertificate))
            {
                throw new ArgumentException("thumbprint should not be empty. Please set the thumbprint setting in the appsettings.json", "certificateName");
            }
            X509Certificate2 cert = null;

            using (X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadOnly);
                X509Certificate2Collection certCollection = store.Certificates;

                // Find unexpired certificates.
                X509Certificate2Collection currentCerts = certCollection.Find(X509FindType.FindByTimeValid, DateTime.Now, false);

                // From the collection of unexpired certificates, find the ones with the correct name.
                X509Certificate2Collection signingCert = currentCerts.Find(X509FindType.FindByThumbprint, ThumbprintCertificate, false);

                // Return the first certificate in the collection, has the right name and is current.
                cert = signingCert.OfType<X509Certificate2>().OrderByDescending(c => c.NotBefore).FirstOrDefault();
            }
            return cert;
        }

        /// <summary>
        /// Driver function for bulk import.
        /// </summary>
        /// <returns></returns>
        private async Task RunBulkImportAsync()
        {
            // Cleanup on start if set in config.

            DocumentCollection dataCollection = null;
            try
            {
                if (bool.Parse(ConfigurationManager.AppSettings["ShouldCleanupOnStart"]))
                {
                    Database database = Utils.GetDatabaseIfExists(client, DatabaseName);
                    if (database != null)
                    {
                        await client.DeleteDatabaseAsync(database.SelfLink);
                    }

                    Trace.TraceInformation("Creating database {0}", DatabaseName);
                    database = await client.CreateDatabaseAsync(new Database { Id = DatabaseName });

                    Trace.TraceInformation(String.Format("Creating collection {0} with {1} RU/s", CollectionName, CollectionThroughput));
                    dataCollection = await Utils.CreatePartitionedCollectionAsync(client, DatabaseName, CollectionName, CollectionThroughput);
                }
                else
                {
                    dataCollection = Utils.GetCollectionIfExists(client, DatabaseName, CollectionName);
                    if (dataCollection == null)
                    {
                        throw new Exception("The data collection does not exist");
                    }
                }
            }
            catch (Exception de)
            {
                Trace.TraceError("Unable to initialize, exception message: {0}", de.Message);
                throw;
            }

            // Prepare for bulk import.

            // Creating documents with simple partition key here.
            string partitionKeyProperty = dataCollection.PartitionKey.Paths[0].Replace("/", "");
            long numberOfDocumentsToGenerate = long.Parse(ConfigurationManager.AppSettings["NumberOfDocumentsToImport"]);

            // Set retry options high for initialization (default values).
            client.ConnectionPolicy.RetryOptions.MaxRetryWaitTimeInSeconds = 30;
            client.ConnectionPolicy.RetryOptions.MaxRetryAttemptsOnThrottledRequests = 9;

            IBulkExecutor graphbulkExecutor = new GraphBulkExecutor(client, dataCollection);
            await graphbulkExecutor.InitializeAsync();

            // Set retries to 0 to pass control to bulk executor.
            client.ConnectionPolicy.RetryOptions.MaxRetryWaitTimeInSeconds = 0;
            client.ConnectionPolicy.RetryOptions.MaxRetryAttemptsOnThrottledRequests = 0;

            var tokenSource = new CancellationTokenSource();
            var token = tokenSource.Token;

            BulkImportResponse vResponse = null;
            BulkImportResponse eResponse = null;

            try
            {
                vResponse = await graphbulkExecutor.BulkImportAsync(
                        Utils.GenerateVertices(numberOfDocumentsToGenerate),
                        enableUpsert: true,
                        disableAutomaticIdGeneration: true,
                        maxConcurrencyPerPartitionKeyRange: null,
                        maxInMemorySortingBatchSize: null,
                        cancellationToken: token);

                eResponse = await graphbulkExecutor.BulkImportAsync(
                        Utils.GenerateEdges(numberOfDocumentsToGenerate),
                        enableUpsert: true,
                        disableAutomaticIdGeneration: true,
                        maxConcurrencyPerPartitionKeyRange: null,
                        maxInMemorySortingBatchSize: null,
                        cancellationToken: token);
            }
            catch (DocumentClientException de)
            {
                Trace.TraceError("Document client exception: {0}", de);
            }
            catch (Exception e)
            {
                Trace.TraceError("Exception: {0}", e);
            }

            Console.WriteLine("\nSummary for batch");
            Console.WriteLine("--------------------------------------------------------------------- ");
            Console.WriteLine(
                "Inserted {0} graph elements ({1} vertices, {2} edges) @ {3} writes/s, {4} RU/s in {5} sec)",
                vResponse.NumberOfDocumentsImported + eResponse.NumberOfDocumentsImported,
                vResponse.NumberOfDocumentsImported,
                eResponse.NumberOfDocumentsImported,
                Math.Round(
                    (vResponse.NumberOfDocumentsImported) /
                    (vResponse.TotalTimeTaken.TotalSeconds + eResponse.TotalTimeTaken.TotalSeconds)),
                Math.Round(
                    (vResponse.TotalRequestUnitsConsumed + eResponse.TotalRequestUnitsConsumed) /
                    (vResponse.TotalTimeTaken.TotalSeconds + eResponse.TotalTimeTaken.TotalSeconds)),
                vResponse.TotalTimeTaken.TotalSeconds + eResponse.TotalTimeTaken.TotalSeconds);
            Console.WriteLine(
                "Average RU consumption per insert: {0}",
                (vResponse.TotalRequestUnitsConsumed + eResponse.TotalRequestUnitsConsumed) /
                (vResponse.NumberOfDocumentsImported + eResponse.NumberOfDocumentsImported));
            Console.WriteLine("---------------------------------------------------------------------\n ");

            if (vResponse.BadInputDocuments.Count > 0 || eResponse.BadInputDocuments.Count > 0)
            {
                using (System.IO.StreamWriter file = new System.IO.StreamWriter(@".\BadVertices.txt", true))
                {
                    foreach (object doc in vResponse.BadInputDocuments)
                    {
                        file.WriteLine(doc);
                    }
                }

                using (System.IO.StreamWriter file = new System.IO.StreamWriter(@".\BadEdges.txt", true))
                {
                    foreach (object doc in eResponse.BadInputDocuments)
                    {
                        file.WriteLine(doc);
                    }
                }
            }

            // Cleanup on finish if set in config.
            if (bool.Parse(ConfigurationManager.AppSettings["ShouldCleanupOnFinish"]))
            {
                Trace.TraceInformation("Deleting Database {0}", DatabaseName);
                await client.DeleteDatabaseAsync(UriFactory.CreateDatabaseUri(DatabaseName));
            }

            //Trace.WriteLine("\nPress any key to exit.");
            //Console.ReadKey();
        }
    }
}