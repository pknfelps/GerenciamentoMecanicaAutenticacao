#Requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'S3')]
param(
    [Parameter(Mandatory, ParameterSetName = 'S3')][string]$Bucket,
    [Parameter(Mandatory, ParameterSetName = 'Local')][string]$PackagePath,
    [string]$Region = 'us-east-1',
    [string]$ExpectedAccount = '121754142617',
    [string]$ExpectedSha256 = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$definition = Get-Content -LiteralPath (Join-Path $root 'ContractTests/ContractTests.csproj') -Raw
$reference = @($definition.Project.ItemGroup.PackageReference) | Where-Object Include -eq 'GerenciamentoMecanica.Auth.Contracts'
if ($reference.Version -notmatch '^\[(\d+\.\d+\.\d+)\]$') { throw 'PackageReference deve fixar versão exata [x.y.z].' }
$version = $Matches[1]
$name = "GerenciamentoMecanica.Auth.Contracts.$version.nupkg"
$key = "packages/GerenciamentoMecanica.Auth.Contracts/$version/$name"
$download = [IO.Path]::GetTempFileName()
$sidecar = [IO.Path]::GetTempFileName()
try {
    if ($PSCmdlet.ParameterSetName -eq 'S3') {
        & aws s3api get-object --bucket $Bucket --key $key --expected-bucket-owner $ExpectedAccount --region $Region --no-cli-pager $download | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Pacote $version indisponível; publique-o antes do build." }
        & aws s3api get-object --bucket $Bucket --key "$key.sha256" --expected-bucket-owner $ExpectedAccount --region $Region --no-cli-pager $sidecar | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Sidecar indisponível; publicação incompleta.' }
    }
    else {
        Copy-Item -LiteralPath $PackagePath -Destination $download
        Copy-Item -LiteralPath "$PackagePath.sha256" -Destination $sidecar
    }
    $checksumText = [IO.File]::ReadAllText($sidecar)
    if ($checksumText -cnotmatch '\A[0-9a-f]{64}\n\z') { throw 'Sidecar SHA-256 inválido.' }
    $hash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($checksumText -cne "$hash`n") { throw 'Checksum do pacote divergente.' }
    if ($ExpectedSha256 -and $ExpectedSha256 -cne $hash) { throw 'Checksum divergente da referência esperada.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($download)
    try {
        $entry = $zip.GetEntry('GerenciamentoMecanica.Auth.Contracts.nuspec')
        if ($null -eq $entry) { throw 'Nuspec esperado ausente.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($metadata.package.metadata.id -cne 'GerenciamentoMecanica.Auth.Contracts' -or
            $metadata.package.metadata.version -cne $version) { throw 'Identidade/versão interna do pacote divergente.' }
    }
    finally { $zip.Dispose() }
    $feed = Join-Path $root 'artifacts/auth-contracts'
    New-Item -ItemType Directory -Path $feed -Force | Out-Null
    Copy-Item -LiteralPath $download -Destination (Join-Path $feed $name) -Force
    Copy-Item -LiteralPath $sidecar -Destination (Join-Path $feed "$name.sha256") -Force
    # Cache separado por checksum impede reutilizar binário antigo da mesma versão.
    [pscustomobject]@{ Version = $version; Sha256 = $hash; PackagesPath = (Join-Path $root "artifacts/nuget/$hash") }
}
finally {
    Remove-Item -LiteralPath $download, $sidecar -Force
}
