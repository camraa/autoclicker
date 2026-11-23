# Construit les deux éditions d'Auto Clic :
#   - perso  : dist\AutoClic\AutoClic.exe (+ dist\AutoClic.exe), avec la config perso camra_afk intégrée
#   - client : dist\client\AutoClic.exe et dist\AutoClic-<version>-client.zip, sans aucune config perso
# Usage : powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$root = $PSScriptRoot

[xml]$proj = Get-Content "$root\src\AutoClic.csproj"
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

if (-not (Test-Path "$root\src\app.ico")) { & "$root\tools\make-icon.ps1" }

# Mode d'emploi pour les utilisateurs (sans la partie développeur), intégré à l'exe et livré à côté
$readme = [IO.File]::ReadAllText("$root\LISEZ-MOI.txt", [Text.Encoding]::UTF8)
$dev = $readme.IndexOf('POUR LE DÉVELOPPEUR')
if ($dev -gt 0) { $readme = $readme.Substring(0, $dev).TrimEnd() + "`r`n" }
New-Item -ItemType Directory -Force "$root\build" | Out-Null
$appReadme = "$root\build\LISEZ-MOI-app.txt"
[IO.File]::WriteAllText($appReadme, $readme, (New-Object Text.UTF8Encoding($true)))

function Build-Edition([string]$edition, [string]$out) {
    Write-Host "Compilation d'Auto Clic $version ($edition)..."
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    # --no-incremental : les deux éditions n'embarquent pas les mêmes fichiers
    dotnet build "$root\src\AutoClic.csproj" -c Release -o $out --nologo -v quiet --no-incremental "-p:Edition=$edition" "-p:AppReadme=$appReadme"
    if ($LASTEXITCODE -ne 0) { throw "La compilation ($edition) a échoué" }
}

# ---------- Édition perso ----------
$bin = "$root\build\bin"
Build-Edition 'Perso' $bin
$app = "$root\dist\AutoClic"                       # on garde boutons.json et le journal s'ils y sont déjà
New-Item -ItemType Directory -Force $app | Out-Null
Copy-Item "$bin\AutoClic.exe" $app -Force
Copy-Item "$bin\AutoClic.exe" "$root\dist\AutoClic.exe" -Force
Copy-Item "$root\LISEZ-MOI.txt" $app -Force

# ---------- Édition client ----------
$clientBin = "$root\build\client"
Build-Edition 'Client' $clientBin
$client = "$root\dist\client"
if (Test-Path $client) { Remove-Item $client -Recurse -Force }
New-Item -ItemType Directory -Force $client | Out-Null
Copy-Item "$clientBin\AutoClic.exe" $client -Force
Copy-Item $appReadme "$client\LISEZ-MOI.txt" -Force

$zip = "$root\dist\AutoClic-$version-client.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
$stage = "$root\build\zip\AutoClic"
if (Test-Path "$root\build\zip") { Remove-Item "$root\build\zip" -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item "$client\AutoClic.exe", "$client\LISEZ-MOI.txt" $stage
Compress-Archive -Path $stage -DestinationPath $zip

# Anciennes archives
Get-ChildItem "$root\dist" -Filter 'AutoClic-*.zip' | Where-Object { $_.FullName -ne $zip } | Remove-Item -Force

foreach ($f in "$app\AutoClic.exe", "$client\AutoClic.exe") {
    Write-Host ("OK : {0} ({1} Ko)" -f $f, [Math]::Round((Get-Item $f).Length / 1KB))
}
Write-Host "OK : $zip"
