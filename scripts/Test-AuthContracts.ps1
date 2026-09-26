#Requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'S3')]
param(
    [Parameter(Mandatory, ParameterSetName = 'S3')][string]$Bucket,
    [Parameter(Mandatory, ParameterSetName = 'Local')][string]$PackagePath,
    [string]$Region = 'us-east-1',
    [string]$ExpectedSha256 = ''
)
$ErrorActionPreference = 'Stop'
$package = & (Join-Path $PSScriptRoot 'Get-AuthContracts.ps1') @PSBoundParameters
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& dotnet restore (Join-Path $root 'ContractTests/ContractTests.csproj') --configfile (Join-Path $root 'NuGet.Config') --packages $package.PackagesPath --force
if ($LASTEXITCODE -ne 0) { throw 'Falha no restore do consumidor.' }
& dotnet test (Join-Path $root 'ContractTests/ContractTests.csproj') -c Release --no-restore "-p:RestorePackagesPath=$($package.PackagesPath)"
if ($LASTEXITCODE -ne 0) { throw 'Falha nos testes do pacote consumido.' }
Write-Host "Consumo validado: Auth.Contracts $($package.Version), SHA-256 $($package.Sha256)"
