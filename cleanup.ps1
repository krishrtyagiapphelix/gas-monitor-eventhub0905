Write-Host "Stopping any running Azure Functions processes..." -ForegroundColor Cyan
Stop-Process -Name "func" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "dotnet" -Force -ErrorAction SilentlyContinue

Write-Host "Cleaning up build artifacts..." -ForegroundColor Cyan
Get-ChildItem -Path . -Include bin,obj -Recurse -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Removing Azure Storage lock files..." -ForegroundColor Cyan
# This is just informational since we can't directly access the Azure Storage from this script
Write-Host "NOTE: To fully resolve function ID conflicts, you may need to clear blob containers in your Azure Storage account."

Write-Host "Clean-up complete. Now running 'dotnet build'..." -ForegroundColor Green
dotnet build

Write-Host "You can now start the function app with 'func start'" -ForegroundColor Green
