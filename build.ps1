param(
    [string] $Extension,
    [string] $PackageVersion
)

$ErrorActionPreference = "Stop"

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Invoke-DotNet tool restore
Invoke-DotNet restore Aspire.Extensions.Tools.slnx
Invoke-DotNet format Aspire.Extensions.Tools.slnx --verify-no-changes --no-restore
Invoke-DotNet build Aspire.Extensions.Tools.slnx --configuration Release --no-restore
Invoke-DotNet test Aspire.Extensions.Tools.slnx --configuration Release --no-build --no-restore

$extensionPaths = if ([string]::IsNullOrWhiteSpace($Extension)) {
    Get-ChildItem extensions -Directory -Filter "Shirubasoft.Aspire.Extensions.*" |
        Sort-Object Name |
        ForEach-Object FullName
}
else {
    @($Extension)
}

foreach ($path in $extensionPaths) {
    $arguments = @("-ExtensionPath", $path)
    if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
        $arguments += @("-PackageVersion", $PackageVersion)
    }

    & "$PSScriptRoot/eng/build-extension.ps1" @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Extension build failed with exit code $LASTEXITCODE."
    }
}
