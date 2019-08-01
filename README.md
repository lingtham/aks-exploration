# aks-exploration
Contains project code and documentation for Azure Kubernetes Service (AKS) exploration

Verified that AKS has the following features:
  - Nodepools with Windows OS to run Windows containers
  - Ability to scale replicas
  - ConfigMaps and Environment Variables to inject different config values 
  - AzureDisks for persistent storage
  - Able to incorporate RocksDB for data storage and backup data to Azure Blob Storage
  - Able to implement blob leasing for master/slave election

