param(
    # Version for the file names and the installer; default: <Version> in the app's .csproj (as build.ps1).
    [string]$Version
)
# Turns ./publish (from build.ps1) into artifacts/SaymonTools-Setup-X.Y.Z.exe and artifacts/SaymonTools-X.Y.Z-win-x64.zip.
# Needs Inno Setup 6 (ISCC.exe); on CI it is installed with Chocolatey when missing.
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    if (-not $Version) {
        $Version = ([xml](Get-Content src/SaymonTools.App/SaymonTools.App.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    }
    if (-not (Test-Path publish/SaymonTools.exe)) { throw 'Run ./build.ps1 first: publish/SaymonTools.exe is missing.' }

    $iscc = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Inno Setup*\ISCC.exe", "$env:ProgramFiles\Inno Setup*\ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $iscc -and $env:CI) {
        choco install innosetup -y --no-progress | Out-Null
        $iscc = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Inno Setup*\ISCC.exe", "$env:ProgramFiles\Inno Setup*\ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if (-not $iscc) { throw 'Inno Setup (ISCC.exe) not found. Install it: winget install JRSoftware.InnoSetup' }

    New-Item -ItemType Directory -Force artifacts | Out-Null
    & $iscc.FullName "/DAppVersion=$Version" installer/SaymonTools.iss
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }
    Compress-Archive -Path ./publish/* -DestinationPath "./artifacts/SaymonTools-$Version-win-x64.zip" -Force
    Get-ChildItem artifacts | ForEach-Object { '{0}  {1:N1} MB' -f $_.Name, ($_.Length / 1MB) }
}
finally { Pop-Location }
