# Reset Azure Functions Storage script
# This script will completely reset the Azure Storage account used by Azure Functions

# Get connection string from local.settings.json
$localSettings = Get-Content -Path "local.settings.json" | ConvertFrom-Json
$connectionString = $localSettings.Values.AzureWebJobsStorage

Write-Host "Using connection string from local.settings.json"
Write-Host "This script will clear all function metadata from Azure Storage"

# Stop running Azure Functions processes first
Write-Host "Stopping any running Azure Functions processes..."
Get-Process -Name func,dotnet -ErrorAction SilentlyContinue | Stop-Process -Force

# Clean local Azure Functions metadata
$localAppDataPath = "$env:LOCALAPPDATA\AzureFunctionsTools"
if (Test-Path $localAppDataPath) {
    Write-Host "Removing Azure Functions metadata from $localAppDataPath"
    Remove-Item -Path $localAppDataPath -Recurse -Force -ErrorAction SilentlyContinue
}

# Build cleanup and rebuild
Write-Host "Cleaning build output..."
Remove-Item -Path "bin" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "obj" -Recurse -Force -ErrorAction SilentlyContinue

# Use Azure CLI to clean storage account
Write-Host "Clearing Azure Storage account data (function metadata)..."
Write-Host "Please run the following commands manually in Azure Storage Explorer:"
Write-Host "1. Connect to your storage account using the connection string in local.settings.json"
Write-Host "2. Delete the 'azure-webjobs-hosts' container"
Write-Host "3. Delete the 'azure-webjobs-secrets' container"
Write-Host "4. Delete the 'azure-webjobs-eventhub' container"

Write-Host "After deleting these containers, rebuild the project with:"
Write-Host "dotnet clean"
Write-Host "dotnet build"
Write-Host "func start"

# Instructions for manual steps if needed
Write-Host "If you still experience issues, please:"
Write-Host "1. Close all instances of func.exe and dotnet.exe"
Write-Host "2. Clear your %TEMP% folder"
Write-Host "3. Try running with a new storage account connection string"
