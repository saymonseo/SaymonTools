param(
    # Version of the build, e.g. 1.2.0 (release tags vX.Y.Z pass it). Default: <Version> in the app's .csproj.
    [string]$Version
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (-not $Version) {
        $Version = ([xml](Get-Content src/SaymonTools.App/SaymonTools.App.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    }
    dotnet publish src/SaymonTools.App/SaymonTools.App.csproj -c Release -r win-x64 --self-contained -p:Version=$Version -o publish
    if ($LASTEXITCODE -ne 0) { throw 'Сборка SaymonTools завершилась с ошибкой.' }
    & ./scripts/bundle-pandoc.ps1 -Destination ./publish/tools
    Copy-Item LICENSE, THIRD-PARTY-NOTICES.md, README.md publish/
    Write-Output "Готово: publish/SaymonTools.exe (версия $Version)"
}
finally { Pop-Location }
