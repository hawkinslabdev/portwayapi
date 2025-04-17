# Set the correct paths for your environment
$repoBase = "C:\Github\PortwayApi"
$toolsDir = "$repoBase\Source\Tools\TokenGenerator"
$deploymentDir = "$repoBase\Deployment\PortwayApi\tools\TokenGenerator"

# Ensure deployment directory exists
if (-not (Test-Path -Path $deploymentDir)) {
    New-Item -Path $deploymentDir -ItemType Directory -Force
}

# Clean the project
dotnet clean $toolsDir -c Release

# Remove obj and bin directories to avoid assembly conflicts
Remove-Item -Path "$toolsDir\obj" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "$toolsDir\bin" -Recurse -Force -ErrorAction SilentlyContinue

# Publish as self-contained
dotnet publish $toolsDir -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true  -o "$deploymentDir"

# Remove .pdb files from the deployment directory
Write-Host "Removing .pdb files from deployment directory..." -ForegroundColor Yellow
Get-ChildItem -Path $deploymentDir -Filter "*.pdb" -Recurse | Remove-Item -Force
Write-Host "Removed .pdb files" -ForegroundColor Green

# Remove other unnecessary files
$filesToRemove = @(
    "*.pdb",
    "*.xml",
    "*.deps.json",
    "*.dev.json"
)

foreach ($pattern in $filesToRemove) {
    Get-ChildItem -Path $deploymentDir -Filter $pattern -Recurse | Remove-Item -Force
}

Write-Host "✅ TokenGenerator published successfully to $deploymentDir" -ForegroundColor Green