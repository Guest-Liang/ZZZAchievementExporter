[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$solutionPath = Join-Path $projectRoot "ZZZAchievementExporter.slnx"
$hookProject = Join-Path $projectRoot "src\ZZZae.Hook\ZZZae.Hook.csproj"
$appProject = Join-Path $projectRoot "src\ZZZae.App\ZZZae.App.csproj"
$hookOutput = Join-Path $projectRoot "artifacts\hook"
$publishOutput = Join-Path $projectRoot "artifacts\publish"

function Reset-OutputDirectory {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($projectRoot)
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $requiredPrefix = $resolvedRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar

    if (-not $resolvedPath.StartsWith(
        $requiredPrefix,
        [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to clean an output directory outside '$resolvedRoot'."
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $resolvedPath | Out-Null
}

Reset-OutputDirectory -Path $hookOutput
Reset-OutputDirectory -Path $publishOutput

& dotnet restore $solutionPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

& dotnet publish $hookProject `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $hookOutput
if ($LASTEXITCODE -ne 0) {
    throw "Publishing the hook library failed with exit code $LASTEXITCODE."
}

$hookBinary = Join-Path $hookOutput "ZZZae.Hook.dll"
if (-not (Test-Path -LiteralPath $hookBinary -PathType Leaf)) {
    throw "The NativeAOT hook library was not produced at '$hookBinary'."
}

& dotnet publish $appProject `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $publishOutput `
    "-p:HookBinaryPath=$hookBinary" `
    "-p:RequireEmbeddedHook=true"
if ($LASTEXITCODE -ne 0) {
    throw "Publishing the application failed with exit code $LASTEXITCODE."
}

$publishedFiles = @(Get-ChildItem -LiteralPath $publishOutput -File)
if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne "ZZZae.exe") {
    $names = ($publishedFiles.Name -join ", ")
    throw "Expected exactly one published file named ZZZae.exe, found: $names"
}

Write-Host "Published: $($publishedFiles[0].FullName)"
