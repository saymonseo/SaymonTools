param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [string]$ArchivePath
)
$ErrorActionPreference = 'Stop'
$version = '3.12.1'
$expected = '6ef431cb20b2c24a0371fc19200dc49d452be54b1b5c9239f3462841ef53e587'
$url = "https://github.com/jgm/pandoc/releases/download/$version/pandoc-$version-windows-x86_64.zip"
$downloaded = [string]::IsNullOrWhiteSpace($ArchivePath)
if ($downloaded) {
    $ArchivePath = Join-Path ([System.IO.Path]::GetTempPath()) ("saymontools-pandoc-" + [guid]::NewGuid() + '.zip')
}
try {
    if ($downloaded) { Invoke-WebRequest $url -OutFile $ArchivePath }
    if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
        throw 'Pandoc archive checksum does not match the official release.'
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    # .NET resolves relative paths against the process directory, not the PowerShell location.
    $Destination = (Resolve-Path -LiteralPath $Destination).ProviderPath
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).ProviderPath)
    try {
        foreach ($name in @('pandoc.exe', 'COPYING.rtf', 'COPYRIGHT.txt', 'MANUAL.html')) {
            $entry = $archive.GetEntry("pandoc-$version/$name")
            if ($null -eq $entry) { throw "Pandoc archive is missing $name" }
            $target = Join-Path $Destination $name
            $part = $target + '.part'
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $part, $true)
            Move-Item -LiteralPath $part -Destination $target -Force
        }
    }
    finally { $archive.Dispose() }
    "Pandoc $version bundled and SHA-256 verified."
}
finally {
    if ($downloaded -and (Test-Path -LiteralPath $ArchivePath)) { Remove-Item -LiteralPath $ArchivePath -Force }
}
