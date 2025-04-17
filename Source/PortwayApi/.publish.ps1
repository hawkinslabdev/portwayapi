# Remove the existing deployment folder (except auth.db which we backed up)
if (Test-Path "C:\Github\portwayapi\Deployment\PortwayApi") {
    # Use robocopy to delete the directory with a purge option
    Write-Host "🗑️ Removing existing deployment folder..."
    Get-ChildItem -Path "C:\Github\portwayapi\Deployment\PortwayApi" -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1 # Give the system a moment
    Remove-Item -Path "C:\Github\portwayapi\Deployment\PortwayApi" -Force -ErrorAction SilentlyContinue
}

# Publish the application
dotnet publish C:\Github\portwayapi\Source\PortwayApi -c Release -o C:\Github\portwayapi\Deployment\PortwayApi

# Clean up unnecessary development files
Write-Host "Removing development files..."
$filesToRemove = @(
    "*.pdb",
    "*.xml",
    "appsettings.Development.json",
    "*.publish.ps1",
    "*.db"
)

foreach ($pattern in $filesToRemove) {
    Get-ChildItem -Path "C:\Github\portwayapi\Deployment\PortwayApi" -Filter $pattern -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
}

# Remove all localized folders with SqlClient resources, except for 'en' and 'nl'
Get-ChildItem -Path "C:\Repository\portwayapi\Deployment\PortwayApi" -Directory |
Where-Object {
    ($_.Name -ne "en" -and $_.Name -ne "nl") -and
    (Test-Path "$($_.FullName)\Microsoft.Data.SqlClient.resources.dll")
} | Remove-Item -Recurse -Force

Write-Host "✅ Deployment complete. The application has been published to C:\Github\portwayapi\Deployment\PortwayApi"