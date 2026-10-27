# Builds out\otto-lab13.zip (manifest.json, agenticUserTemplateManifest.json, color.png, outline.png at the zip root).
# The blueprint app id is substituted into the zip only; the committed template keeps <BLUEPRINT_APP_ID>.
param([string]$BlueprintAppId)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$here = $PSScriptRoot

if (-not $BlueprintAppId) {
    $config = Join-Path $here '..\code\lab13.config.json'
    if (-not (Test-Path $config)) { throw "Pass -BlueprintAppId or fill in code\lab13.config.json (AzureAd:ClientId)." }
    $BlueprintAppId = (Get-Content $config -Raw | ConvertFrom-Json).AzureAd.ClientId
}
if (-not ($BlueprintAppId -as [guid])) { throw "Blueprint app id '$BlueprintAppId' is not a GUID." }

$manifest = Get-Content (Join-Path $here 'manifest.json') -Raw | ConvertFrom-Json
$template = (Get-Content (Join-Path $here 'agenticUserTemplateManifest.json') -Raw).Replace('<BLUEPRINT_APP_ID>', $BlueprintAppId)
$parsed = $template | ConvertFrom-Json
if ($manifest.agenticUserTemplates[0].id -ne $parsed.id) { throw "Template id in manifest.json and agenticUserTemplateManifest.json differ." }
if ($manifest.agenticUserTemplates[0].file -ne 'agenticUserTemplateManifest.json') { throw "manifest.json must point to agenticUserTemplateManifest.json." }

foreach ($icon in @{ 'color.png' = 192; 'outline.png' = 32 }.GetEnumerator()) {
    $img = [System.Drawing.Image]::FromFile((Join-Path $here $icon.Key))
    try { if ($img.Width -ne $icon.Value -or $img.Height -ne $icon.Value) { throw "$($icon.Key) must be $($icon.Value)x$($icon.Value), is $($img.Width)x$($img.Height)." } }
    finally { $img.Dispose() }
}

$out = Join-Path $here 'out'
$stage = Join-Path $out 'stage'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $stage -Force | Out-Null
Copy-Item (Join-Path $here 'manifest.json'), (Join-Path $here 'color.png'), (Join-Path $here 'outline.png') $stage
[IO.File]::WriteAllText((Join-Path $stage 'agenticUserTemplateManifest.json'), $template, [Text.UTF8Encoding]::new($false))

$zip = Join-Path $out 'otto-lab13.zip'
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item $stage -Recurse -Force
Write-Host "Created $zip (blueprint $BlueprintAppId)"
