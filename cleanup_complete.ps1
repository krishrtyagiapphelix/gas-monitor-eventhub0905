# Stop running Azure Functions processes
Get-Process -Name func,dotnet -ErrorAction SilentlyContinue | Stop-Process -Force

# Clean local Azure Functions metadata
$localAppDataPath = "$env:LOCALAPPDATA\AzureFunctionsTools"
if (Test-Path $localAppDataPath) {
    Write-Host "Removing Azure Functions metadata from $localAppDataPath"
    Remove-Item -Path $localAppDataPath -Recurse -Force
}

# Check for .azure folder in user profile and remove it if needed
$azureFolder = "$env:USERPROFILE\.azure"
if (Test-Path $azureFolder) {
    Write-Host "Removing Azure Functions metadata from $azureFolder"
    Remove-Item -Path $azureFolder -Recurse -Force
}

# Clean build output
Write-Host "Cleaning build output..."
Remove-Item -Path "bin" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "obj" -Recurse -Force -ErrorAction SilentlyContinue

# Clean local Azure Storage emulator data
Write-Host "Cleaning local Azure Storage emulator data..."
$storageEmulatorPath = "$env:USERPROFILE\.azurite"
if (Test-Path $storageEmulatorPath) {
    Remove-Item -Path $storageEmulatorPath -Recurse -Force
}

# Rebuild the project
Write-Host "Rebuilding project..."
dotnet clean
dotnet build

Write-Host "Cleanup completed successfully!"
