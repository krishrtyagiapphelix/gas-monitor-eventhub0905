param(
    [Parameter(Mandatory=$false)]
    [string]$connectionString = ""
)

# Get connection string from local.settings.json if not provided
if ([string]::IsNullOrEmpty($connectionString)) {
    Write-Host "Using connection string from local.settings.json"
    $settingsFile = Join-Path $PSScriptRoot "local.settings.json"
    if (Test-Path $settingsFile) {
        $settings = Get-Content $settingsFile | ConvertFrom-Json
        $connectionString = $settings.Values.AzureWebJobsStorage
    }
    else {
        Write-Error "local.settings.json not found. Please provide a storage connection string as parameter."
        exit 1
    }
}

if ([string]::IsNullOrEmpty($connectionString)) {
    Write-Error "Storage connection string not found in local.settings.json. Please provide a storage connection string as parameter."
    exit 1
}

Write-Host "This script will clear Azure Storage containers used by Azure Functions"

# Stop any running Azure Functions processes
Write-Host "Stopping any running Azure Functions processes..."
Get-Process -Name "func" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*functions*" } | Stop-Process -Force -ErrorAction SilentlyContinue

# Install the Azure Storage module if not present
if (-not (Get-Module -ListAvailable -Name Az.Storage)) {
    Write-Host "Installing Az.Storage module..."
    Install-Module -Name Az.Storage -Force -Scope CurrentUser
}

# Import Az.Storage module
Import-Module Az.Storage -ErrorAction SilentlyContinue

# Connect to Azure Storage
$context = New-AzStorageContext -ConnectionString $connectionString -ErrorAction SilentlyContinue
if (-not $context) {
    Write-Error "Failed to connect to Azure Storage. Please check your connection string."
    exit 1
}

# Define containers to delete
$containersToDelete = @(
    "azure-webjobs-hosts",
    "azure-webjobs-secrets",
    "azure-webjobs-eventhub"
)

# Delete containers
foreach ($containerName in $containersToDelete) {
    Write-Host "Deleting container: $containerName..."
    try {
        Remove-AzStorageContainer -Name $containerName -Context $context -Force -ErrorAction SilentlyContinue
        Write-Host "Container $containerName deleted successfully or did not exist."
    }
    catch {
        Write-Warning "Failed to delete container $containerName. It may not exist or you may not have permissions."
    }
}

# Clean local build artifacts
Write-Host "Cleaning build output..."
dotnet clean "$(Join-Path $PSScriptRoot 'EventHubConsumer.csproj')" --configuration Debug
Remove-Item (Join-Path $PSScriptRoot "bin") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $PSScriptRoot "obj") -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Storage cleanup completed successfully."
Write-Host "Next steps:"
Write-Host "1. Rebuild the project: dotnet build"
Write-Host "2. Start the function: func start"
