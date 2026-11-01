param(
    [string]$GameDir = $env:STS2_GAME_DIR,
    [string]$DotnetPath = 'dotnet',
    [switch]$Install,
    [switch]$Workshop
)
$ErrorActionPreference = 'Stop'
if (-not $GameDir) { throw 'Provide -GameDir or STS2_GAME_DIR.' }
$resolvedGameDir = (Resolve-Path -LiteralPath $GameDir).Path
$assembly = Join-Path $resolvedGameDir 'data_sts2_windows_x86_64/sts2.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Game assemblies not found.' }
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mod_manifest.json') -Raw | ConvertFrom-Json
$version = [string]$manifest.version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
if ($manifest.id -ne 'SpireRewind') { throw 'The manifest id must be SpireRewind (the DLL name).' }
& $DotnetPath build (Join-Path $PSScriptRoot 'SpireRewind.csproj') -c Release --nologo "-p:STS2GameDir=$resolvedGameDir"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
# The DLL is published; it must not carry the build machine's paths (PDB path, source paths). Directory.Build.props
# maps them to /_/; this check catches a regression before anything is packaged. Only full path fragments are searched
# (the repository root, the user profile folder and "\Users\<name>\" with its separators, each with both separator
# styles): the bare user name is not, because the DLL legitimately contains words such as "user" or "system" and an
# account with such a name would otherwise fail every build.
function Get-LocalPathNeedles([string]$RepoRoot, [string]$UserProfile, [string]$UserName) {
    $needles = [Collections.Generic.List[string]]::new()
    foreach ($path in @($RepoRoot, $UserProfile)) {
        if (-not $path) { continue }
        $trimmed = $path.TrimEnd('\', '/')
        if ($trimmed.Length -lt 4) { continue }  # A drive root or shorter is not a leak signature.
        $needles.Add($trimmed.Replace('/', '\'))
        $needles.Add($trimmed.Replace('\', '/'))
    }
    if ($UserName) {
        $needles.Add("\Users\$UserName\")
        $needles.Add("/Users/$UserName/")
    }
    return @($needles | Sort-Object -Unique)
}
function Test-ReleaseDll([string]$Path, [string[]]$Needles) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    foreach ($text in @([Text.Encoding]::UTF8.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))) {
        foreach ($needle in $Needles) {
            if ($text.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "The release DLL embeds a local path ('$needle'). Check PathMap and DebugType in Directory.Build.props." }
        }
    }
}
$dllPath = Join-Path $PSScriptRoot 'bin/Release/net9.0/SpireRewind.dll'
Test-ReleaseDll $dllPath (Get-LocalPathNeedles $PSScriptRoot ([Environment]::GetFolderPath('UserProfile')) ([Environment]::UserName))
# The release is one folder players drop into mods/. The game reads every .json under mods/ as a mod manifest, so the
# manifest (renamed to the mod id) is the only .json in the package; the mod itself writes only under the profile's
# rewind/ folder at runtime, never beside the DLL.
$packageDir = Join-Path $PSScriptRoot ("dist/SpireRewind-$version/SpireRewind")
if (Test-Path -LiteralPath $packageDir) { Remove-Item -LiteralPath $packageDir -Recurse -Force }
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net9.0/SpireRewind.dll') -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'mod_manifest.json') -Destination (Join-Path $packageDir 'SpireRewind.json')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'packaging/README.md') -Destination $packageDir
$jsonFiles = @(Get-ChildItem -LiteralPath $packageDir -Recurse -Filter '*.json')
if ($jsonFiles.Count -ne 1 -or $jsonFiles[0].Name -ne 'SpireRewind.json') { throw 'The package must contain exactly one .json: SpireRewind.json.' }
$zipPath = Join-Path $PSScriptRoot "dist/SpireRewind-$version.zip"
Compress-Archive -Path $packageDir -DestinationPath $zipPath -Force
if ($Workshop) {
    # workshop/ is the ModUploader workspace: content/ gets the same files as the release folder. workshop.json, image.png,
    # previews/ and the mod_id.txt written by the first upload stay as they are. The uploader needs image.png under 1 MB
    # (and the same limit for each preview), so the workspace is refused when that is not the case.
    $image = Join-Path $PSScriptRoot 'workshop/image.png'
    if (-not (Test-Path -LiteralPath $image -PathType Leaf)) { throw 'workshop/image.png is missing. ModUploader requires an original thumbnail under 1 MB; see workshop/README.md.' }
    if ((Get-Item -LiteralPath $image).Length -ge 1MB) { throw 'workshop/image.png must be smaller than 1 MB.' }
    $previews = Join-Path $PSScriptRoot 'workshop/previews'
    if (Test-Path -LiteralPath $previews) {
        foreach ($preview in Get-ChildItem -LiteralPath $previews -File) { if ($preview.Length -ge 1MB) { throw "workshop/previews/$($preview.Name) must be smaller than 1 MB." } }
    }
    $contentDir = Join-Path $PSScriptRoot 'workshop/content'
    if (Test-Path -LiteralPath $contentDir) { Remove-Item -LiteralPath $contentDir -Recurse -Force }
    New-Item -ItemType Directory -Path $contentDir -Force | Out-Null
    Copy-Item -Path (Join-Path $packageDir '*') -Destination $contentDir
    Write-Output "Workshop content: $contentDir"
}
if ($Install) {
    if (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) { throw 'Exit the game normally before installing the mod.' }
    $modsDir = Join-Path $resolvedGameDir 'mods'
    $targetDir = Join-Path $modsDir 'SpireRewind'
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    $backupDir = Join-Path $PSScriptRoot ('.install-backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $modsRoot = [IO.Path]::GetFullPath($modsDir) + [IO.Path]::DirectorySeparatorChar
    # Earlier installs put the two files directly in mods/; move those aside as well as anything in mods/SpireRewind/.
    $candidates = @((Join-Path $modsDir 'SpireRewind.dll'), (Join-Path $modsDir 'SpireRewind.json')) + @(Get-ChildItem -LiteralPath $targetDir -File | ForEach-Object { $_.FullName })
    foreach ($source in $candidates) {
        $full = [IO.Path]::GetFullPath($source)
        if (-not $full.StartsWith($modsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe mod backup source.' }
        if (Test-Path -LiteralPath $full) {
            New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
            $relative = $full.Substring($modsRoot.Length)
            $destination = Join-Path $backupDir $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Move-Item -LiteralPath $full -Destination $destination
        }
    }
    Copy-Item -Path (Join-Path $packageDir '*') -Destination $targetDir -Recurse
    [pscustomobject]@{installed_to=$targetDir;backup_directory=$backupDir;dll_sha256=(Get-FileHash -LiteralPath (Join-Path $targetDir 'SpireRewind.dll')).Hash} | ConvertTo-Json
} else { Write-Output "Package: $zipPath" }
