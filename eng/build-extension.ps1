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
$packProjectsFile = Join-Path $extensionPath "pack-projects.txt"
$artifactPath = "artifacts/$packageId"

if (-not (Test-Path $solution) -or -not (Test-Path $packageProject)) {
    throw "The extension does not follow the repository layout: $extensionPath"
}

$versionArguments = @()
if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
    $versionArguments += "-p:Version=$PackageVersion"
}

$packageProjects = @($packageProject)
if (Test-Path $packProjectsFile) {
    $packageProjects = @(
        Get-Content $packProjectsFile |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and -not $_.StartsWith("#", [StringComparison]::Ordinal) } |
            ForEach-Object {
                $relativeProject = $_
                $pathSeparators = [char[]] @(
                    [IO.Path]::DirectorySeparatorChar,
                    [IO.Path]::AltDirectorySeparatorChar
                )
                if ([IO.Path]::IsPathRooted($relativeProject) -or
                    $relativeProject.Split($pathSeparators, [StringSplitOptions]::RemoveEmptyEntries) -contains "..") {
                    throw "Pack project paths must stay within the extension: $relativeProject"
                }
                $project = Join-Path $extensionPath $relativeProject
                if (-not (Test-Path $project)) {
                    throw "Pack project does not exist: $project"
                }
                $project
            }
    )
    if ($packageProjects.Count -eq 0) {
        throw "The pack project list is empty: $packProjectsFile"
    }
}

Invoke-DotNet restore $solution
Invoke-DotNet format $solution --verify-no-changes --no-restore
Invoke-DotNet build $solution --configuration Release --no-restore @versionArguments
Invoke-DotNet test $solution --configuration Release --no-build --no-restore @versionArguments
foreach ($project in $packageProjects) {
    Invoke-DotNet pack $project `
        --configuration Release --no-build --no-restore `
        --output $artifactPath @versionArguments
}
