# Deploy a File Server

Start by clicking on the Storage link on the left-hand vertical menu.  On the overview page, click Add Storage Repository.

The following information needs to be specified.

1. File Server name - A name for the file server, this will determine the Azure resource names.
2. Storage Repository Type - Select File Server
3. Subscription ID - The Azure Subscription the resources should be deployed into.
4. Deploy to Environment - Select this option if the File Server will be used by a single environment.
The File Server will use the environments VNet and Subnet and be tagged with the environment name.
5. Storage will be Shared by multiple Environments - Select this option if your File Server will be shared by multiple Environments.
The environments must share a VNet or have peered VNets.

Return to the Render Hub [docs](README.md).
