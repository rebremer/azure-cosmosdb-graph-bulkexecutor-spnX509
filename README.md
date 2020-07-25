---
page_type: sample
languages:
- csharp
products:
- azure
- azure-cosmos-db
- azure-batch
- azure-data-factory
name: Azure Cosmos DB BulkExecutor library for .NET using SPN .X509 certificate to authenticate to key vault to retrieve Cosmos DB secret
description: "The Azure Cosmos DB BulkExecutor library for .NET acts as an extension library to the .NET SDK and provides functionality to perform bulk operations in Azure Cosmos DB. Executable can be used in Azure Batch and as Custom Activity in Azure Data Factory"
urlFragment: azure-cosmosdb-graph-bulkexecutor-spnX509
---

Original project without SPN and .X509 certifcate can be found here: https://github.com/Azure-Samples/azure-cosmosdb-graph-bulkexecutor-dotnet-getting-started. The following steps are executed:

1. Create a Cosmos DB graph API, Azure Key vault and add Cosmos DB secret to key vault
2. Create a self-signed certificate, service principal and add access policy to key vault
3. Run project locally 
4. Run project as custom activity in Azure Data Factory using Azure Batch
5. Add firewall rules on Azure Key vault

### 1. Create a Cosmos DB graph API, Azure Keyvault and add Cosmos DB secret to key vault ###

- Follow [this tutorial](https://docs.microsoft.com/en-us/azure/cosmos-db/create-graph-dotnet#create-a-database-account) to create a Cosmos DB graph database. Also, add a database with the name `TestDB`
- Follow [this tutorial](https://docs.microsoft.com/en-us/azure/key-vault/general/quick-create-portal#create-a-vault) to create an Azure key vault
- Go to your Cosmos DB account, get key and add this as [secret to key vault](https://docs.microsoft.com/en-us/azure/key-vault/secrets/quick-create-portal#add-a-secret-to-key-vault). In this example, master keys are used. To achieve more granular access, resource tokens can be used, see [here](https://docs.microsoft.com/en-us/azure/cosmos-db/secure-access-to-data)

### 2. Create a self-signed certificate, service principal and add access policy to key vault ###

- Use PowerShell script below to create a self-signed certificate and add this to your local certificate store. Make sure that you note the thumbprint of the cerficate, that is needed in next steps.

```powershell
$cert = New-SelfSignedCertificate -CertStoreLocation "cert:\CurrentUser\My" `
  -Subject "CN=bulkexecutorX509" `
  -KeySpec KeyExchange
$keyValue = [System.Convert]::ToBase64String($cert.GetRawCertData())
```

-  User PowerShell script below to create SPN with certificate. Make sure that you note the application id (aka client id) of SPN, that is needed in next steps.

```powershell
Connect-AzAccount
$sp = New-AzADServicePrincipal -DisplayName bulkexecutorX509 `
  -CertValue $keyValue `
  -EndDate $cert.NotAfter `
  -StartDate $cert.NotBefore
Sleep 20
New-AzRoleAssignment -RoleDefinitionName Reader -ServicePrincipalName $sp.ApplicationId
```

- Follow [this tutorial](https://docs.microsoft.com/en-us/azure/key-vault/general/group-permissions-for-apps#give-the-principal-access-to-your-key-vault) to create an access policy in the key vault such that the spn can retrieve the secret

### 3. Run project locally ###

- git clone [the project](https://github.com/rebremer/azure-cosmosdb-graph-bulkexecutor-spnX509.git) to your local repository and open the solution in Visual Studio 2019
- Navigate to `App.config` and change the values of `EndPointUrl`, `ThumbprintCertificate`, `ClientId`, `Authority` and `KeyvaultURLSecretId` with your own values created in previous steps
- Hit debug and run the project. If everything went well, then graph `TestColl` is added to `TestDB` with 10 vertices which can be verified by running `G.V().count()` in the data explorer in Cosmos DB

### 4. Run project as custom activity in Azure Data Factory using Azure Batch ###

- Use PowerShell script below to export certificate from local store as pfx file

```powershell
$NewPwd = ConvertTo-SecureString -String "<<your password>>" -Force -AsPlainText
Export-PfxCertificate `
  -cert cert:\CurrentUser\my\<<your thumbprint> `
  -FilePath c:\users\bulkexecutorX509.pfx `
  -Password $NewPwd
```

- Follow [this tutorial](https://docs.microsoft.com/en-us/azure/batch/quick-create-portal#create-a-pool-of-compute-nodes) to create an Azure Batch account and pool. When creating the pool, make sure that you 1) `microsoftwindowsserver` is choosen as VM type, 2) pfx file created in previous step is imported as `certificate` and 3) a `VNET` is created in which the Azure pool will run

- Follow [this tutorial](https://docs.microsoft.com/en-us/azure/storage/blobs/storage-quickstart-blobs-portal) to create an Azure Storage Account and create a container. Subsequenlty, go to the `Debug` folder of your dotnet project and upload the `BulkExecutorSample.exe` and all adhering files (DLLs, config) to this container

- Follow [this tutorial](https://docs.microsoft.com/en-us/azure/data-factory/quickstart-create-data-factory-portal) to create a data factory instance and this tutorial to (https://mrpaulandrew.com/2018/11/12/creating-an-azure-data-factory-v2-custom-activity/) to create a custom activity in Azure Batch. Make sure that 1) the storage account points to storage account and container created in the previous step and 2) `BulkExecutorSample.exe` is used as command

- (Optional) By default, Azure Key vault has no firewall rules. To limit network access, add Azure Batch VNET as only allowed network to access the keyvault, see [this tutorial](https://docs.microsoft.com/en-us/azure/key-vault/general/network-security). Similarly, the Azure Batch VNET can be added as only allowedd network to acces Cosmos DB, see [this tutorial](https://docs.microsoft.com/en-us/azure/cosmos-db/how-to-configure-vnet-service-endpoint)

## Contributing & feedback

This project has adopted the [Microsoft Open Source Code of
Conduct](https://opensource.microsoft.com/codeofconduct/).  For more information
see the [Code of Conduct
FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact
[opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional
questions or comments.

To give feedback and/or report an issue, open a [GitHub
Issue](https://help.github.com/articles/creating-an-issue/).

## Other relevant projects

* [Cosmos DB BulkExecutor library for SQl API ](https://github.com/Azure/azure-cosmosdb-bulkexecutor-dotnet-getting-started)
