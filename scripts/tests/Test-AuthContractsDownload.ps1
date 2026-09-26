#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$consumer = Join-Path $PSScriptRoot '../Get-AuthContracts.ps1'
$original = (Resolve-Path -LiteralPath $PackagePath).Path
$taskDirectory = Join-Path ([IO.Path]::GetTempPath()) ("auth-consumer-tests-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $taskDirectory | Out-Null
$package = Join-Path $taskDirectory ([IO.Path]::GetFileName($original))
function Reset-Package {
    Copy-Item -LiteralPath $original -Destination $package -Force
    Copy-Item -LiteralPath "$original.sha256" -Destination "$package.sha256" -Force
}
function Expect-Failure([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        return
    }
    throw "Falha esperada não ocorreu: $Message"
}
try {
    Reset-Package
    & $consumer -PackagePath $package | Out-Null

    [IO.File]::AppendAllText($package, 'corrupted')
    Expect-Failure { & $consumer -PackagePath $package } 'Checksum do pacote'
    Reset-Package
    Expect-Failure { & $consumer -PackagePath $package -ExpectedSha256 ('0' * 64) } 'referência esperada'
    [IO.File]::WriteAllText("$package.sha256", "not-a-checksum`n")
    Expect-Failure { & $consumer -PackagePath $package } 'Sidecar SHA-256 inválido'

    Reset-Package
    $zip = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $zip.GetEntry('GerenciamentoMecanica.Auth.Contracts.nuspec')
        $reader = [IO.StreamReader]::new($entry.Open())
        $text = $reader.ReadToEnd()
        $reader.Dispose()
        $entry.Delete()
        $writer = [IO.StreamWriter]::new($zip.CreateEntry('GerenciamentoMecanica.Auth.Contracts.nuspec').Open())
        $writer.Write(($text -replace '<version>[^<]+</version>', '<version>999.0.0</version>'))
        $writer.Dispose()
    }
    finally { $zip.Dispose() }
    [IO.File]::WriteAllText("$package.sha256", (Get-FileHash -LiteralPath $package).Hash.ToLowerInvariant() + "`n")
    Expect-Failure { & $consumer -PackagePath $package } 'Identidade/versão'
    Write-Host '5 cenários de consumo offline aprovados.'
}
finally {
    Remove-Item -LiteralPath $package, "$package.sha256" -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $taskDirectory -Force
}
