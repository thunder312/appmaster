<#
  Baut den MSI-Installer für Appmaster (self-contained, keine .NET-Runtime nötig).
    .\build.ps1                 -> installer\Appmaster-<Version>.msi
    .\build.ps1 -Installieren   -> baut und installiert danach (per-user, ohne Admin)
  Voraussetzung: dotnet tool install --global wix --version 5.0.2
#>
[CmdletBinding()]
param([string]$Version = "0.1.0", [switch]$Installieren)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
# Publish-Ordner außerhalb der Dropbox (Dropbox sperrt sonst einzelne Dateien)
$pub = Join-Path $env:TEMP "Appmaster-publish"
$msi = Join-Path $PSScriptRoot "Appmaster-$Version.msi"
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }

Write-Host "==> dotnet publish (self-contained win-x64)..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "Appmaster\Appmaster.csproj") -c Release -r win-x64 --self-contained `
    -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false -o $pub
if ($LASTEXITCODE) { throw "dotnet publish fehlgeschlagen" }

Write-Host "==> Dateiliste erzeugen..." -ForegroundColor Cyan
$dirIds = @{ "" = "INSTALLFOLDER" }
$dirXml = New-Object System.Text.StringBuilder
$compXml = New-Object System.Text.StringBuilder
$n = 0
function DirId($rel) {
    if ($dirIds.ContainsKey($rel)) { return $dirIds[$rel] }
    $parent = Split-Path $rel -Parent; if ($null -eq $parent) { $parent = "" }
    $pid_ = DirId $parent
    $id = "D" + ($dirIds.Count)
    $dirIds[$rel] = $id
    [void]$dirXml.AppendLine("    <DirectoryRef Id=`"$pid_`"><Directory Id=`"$id`" Name=`"$(Split-Path $rel -Leaf)`" /></DirectoryRef>")
    return $id
}
Get-ChildItem $pub -Recurse -File | ForEach-Object {
    $rel = $_.DirectoryName.Substring($pub.Length).TrimStart('\')
    $dir = DirId $rel
    $n++
    [void]$compXml.AppendLine("      <Component Id=`"C$n`" Directory=`"$dir`" Guid=`"*`"><File Id=`"F$n`" Source=`"$($_.FullName)`" KeyPath=`"yes`" /></Component>")
}
$wxs = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<Wix xmlns=`"http://wixtoolset.org/schemas/v4/wxs`">`n  <Fragment>`n$dirXml  </Fragment>`n  <Fragment>`n    <ComponentGroup Id=`"AppDateien`">`n$compXml    </ComponentGroup>`n  </Fragment>`n</Wix>`n"
$dateien = Join-Path $env:TEMP "Appmaster-Dateien.wxs"
[IO.File]::WriteAllText($dateien, $wxs, (New-Object Text.UTF8Encoding $false))

Write-Host "==> wix build..." -ForegroundColor Cyan
Copy-Item (Join-Path $root "Appmaster\Assets\app.ico") (Join-Path $PSScriptRoot "app.ico") -Force
& wix build (Join-Path $PSScriptRoot "Appmaster.wxs") $dateien -arch x64 -d "Version=$Version" `
    -bindpath $PSScriptRoot -o $msi
if ($LASTEXITCODE) { throw "wix build fehlgeschlagen" }
Write-Host "Fertig: $msi" -ForegroundColor Green

if ($Installieren) {
    Write-Host "==> installiere..." -ForegroundColor Cyan
    Start-Process msiexec -ArgumentList "/i `"$msi`" /qb" -Wait
    Write-Host "Installiert nach $env:LOCALAPPDATA\Programs\Appmaster" -ForegroundColor Green
}
