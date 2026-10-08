$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$original = Get-Content -LiteralPath "$root\reference-archive\workiq-full-v1\workiq.manifest.json" -Raw | ConvertFrom-Json
$files = @('SKILL.md',
    'references/search-paths-work-iq.md', 'references/get-schema-work-iq.md',
    'references/fetch-work-iq.md', 'references/fetch-blob-work-iq.md',
    'references/upload-blob-work-iq.md', 'references/create-entity-work-iq.md',
    'references/update-entity-work-iq.md', 'references/do-action-work-iq.md',
    'references/call-function-work-iq.md', 'references/teams-work-iq.md',
    'references/sharepoint-work-iq.md', 'references/troubleshooting.md',
    'references/sharepoint-library-metadata.md')
$entries = foreach ($file in $files) {
    $name = "workiq/$file"
    $text = [IO.File]::ReadAllText((Join-Path $root $name.Replace('/', '\'))).Replace("`r`n", "`n")
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $prior = @($original.Files | Where-Object Name -eq $name)
    if ($prior.Count -ne 1) { throw "Missing original provenance for $name" }
    [ordered]@{
        Name = $name
        Bytes = $bytes.Length
        Sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        OriginalSha256 = $prior[0].OriginalSha256
    }
}
$manifest = [ordered]@{ Version = 'workiq-progressive:v2'; Files = @($entries) }
[IO.File]::WriteAllText("$root\workiq.manifest.json", ($manifest | ConvertTo-Json -Depth 6) + "`n",
    [Text.UTF8Encoding]::new($false))
