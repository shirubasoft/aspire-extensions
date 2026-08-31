param(
    [Parameter(Mandatory = $true)]
    [string] $ExtensionPath,
    [string] $PackageVersion
)

$ErrorActionPreference = "Stop"

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$directorySeparators = [char[]] @(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar
)
$extensionPath = $ExtensionPath.TrimEnd($directorySeparators)
$packageId = Split-Path $extensionPath -Leaf
$solution = Join-Path $extensionPath "$packageId.slnx"
$packageProject = Join-Path $extensionPath "src/$packageId/$packageId.csproj"
$artifactPath = "artifacts/$packageId"

if (-not (Test-Path $solution) -or -not (Test-Path $packageProject)) {
    throw "The extension does not follow the repository layout: $extensionPath"
}

$versionArguments = @()
if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
    $versionArguments += "-p:Version=$PackageVersion"
}

Invoke-DotNet restore $solution
Invoke-DotNet format $solution --verify-no-changes --no-restore
Invoke-DotNet build $solution --configuration Release --no-restore @versionArguments
Invoke-DotNet test $solution --configuration Release --no-build --no-restore @versionArguments
Invoke-DotNet pack $packageProject `
    --configuration Release --no-build --no-restore `
    --output $artifactPath @versionArguments
