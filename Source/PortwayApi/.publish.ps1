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
Get-ChildItem -Path "C:\Github\portwayapi\Deployment\PortwayApi" -Directory |
Where-Object {
    ($_.Name -ne "en" -and $_.Name -ne "nl") -and
    (Test-Path "$($_.FullName)\Microsoft.Data.SqlClient.resources.dll")
} | Remove-Item -Recurse -Force

# Generate web.config
$webConfigContent = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet" arguments=".\PortwayApi.dll" stdoutLogEnabled="false" stdoutLogFile=".\logs\stdout" hostingModel="inprocess" />
    </system.webServer>
  </location>
  <system.webServer>
    <httpProtocol>
      <customHeaders>
        <remove name="X-Powered-By" />
        <remove name="X-Content-Type-Options" />
        <remove name="X-Frame-Options" />
        <remove name="Strict-Transport-Security" />
        <remove name="Referrer-Policy" />
        <remove name="Permissions-Policy" />
        <remove name="Content-Security-Policy" />

        <add name="X-Content-Type-Options" value="nosniff" />
        <add name="X-Frame-Options" value="DENY" />
        <add name="Strict-Transport-Security" value="max-age=31536000; includeSubDomains; preload" />
        <add name="Referrer-Policy" value="strict-origin-when-cross-origin" />
        <add name="Permissions-Policy" value="geolocation=(), camera=(), microphone=(), payment=()" />
        <add name="Content-Security-Policy" value="default-src 'self'; script-src 'self' https://online.swagger.io https://validator.swagger.io; style-src 'self' 'unsafe-inline'; img-src 'self' data: https://online.swagger.io https://validator.swagger.io; connect-src 'self' https://online.swagger.io https://validator.swagger.io; font-src 'self'; object-src 'none'; base-uri 'self'; form-action 'none'" />
      </customHeaders>
    </httpProtocol>
  </system.webServer>
</configuration>
"@

# Write the web.config file to the deployment directory
$webConfigPath = "C:\Github\portwayapi\Deployment\PortwayApi\web.config"
$webConfigContent | Out-File -FilePath $webConfigPath -Encoding UTF8

# Ensure .gitignore exists
$gitignorePath = "C:\Github\portwayapi\.gitignore"
$logIgnoreRules = @(
    "`n# Ignore log files",
    "*.log",
    "/logs/"
)
if (-not (Test-Path $gitignorePath)) {
    New-Item -Path $gitignorePath -ItemType File -Force | Out-Null
}
$existingRules = Get-Content $gitignorePath -ErrorAction SilentlyContinue
foreach ($rule in $logIgnoreRules) {
    if ($existingRules -notcontains $rule) {
        Add-Content -Path $gitignorePath -Value $rule
    }
}

Write-Host ".gitignore updated to exclude log files and /logs/ directory"


Write-Host "✅ Deployment complete. The application has been published to C:\Github\portwayapi\Deployment\PortwayApi"
Write-Host "📄 web.config file generated at $webConfigPath"