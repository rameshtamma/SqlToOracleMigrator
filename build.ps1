param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

Write-Host "Building SqlToOracleMigrator ($Configuration)..." -ForegroundColor Cyan
dotnet --version
dotnet restore .\SqlToOracleMigrator.sln
dotnet build .\SqlToOracleMigrator.sln -c $Configuration
Write-Host "Build completed." -ForegroundColor Green
