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
$extensionName = Split-Path $extensionPath -Leaf
$solution = Join-Path $extensionPath "$extensionName.slnx"
$packageProject = Join-Path $extensionPath "src/$extensionName/$extensionName.csproj"

if (-not (Test-Path $solution) -or -not (Test-Path $packageProject)) {
    throw "The extension does not follow the repository layout: $extensionPath"
}

$packageId = (& dotnet msbuild $packageProject -getProperty:PackageId -nologo).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Could not read PackageId from $packageProject."
}
if ([string]::IsNullOrWhiteSpace($packageId)) {
    throw "The extension package project does not define PackageId: $packageProject"
}
$artifactPath = "artifacts/$packageId"

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
