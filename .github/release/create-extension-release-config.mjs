export function createExtensionReleaseConfig({ extensionPath, packageId, tagPrefix }) {
  if (!extensionPath || !packageId || !tagPrefix) {
    throw new TypeError("extensionPath, packageId, and tagPrefix are required.");
  }

  const artifactPath = `artifacts/${packageId}`;
  const conventionalConfig = {
    preset: "conventionalcommits",
    paths: [
      `${extensionPath}/**`,
      "Directory.Build.props",
      "Directory.Packages.props",
      "global.json",
    ],
  };

  return {
    branches: ["main"],
    tagFormat: `${tagPrefix}-v\${version}`,
    plugins: [
      ["./.github/release/path-scoped-conventional-commits.mjs", conventionalConfig],
      [
        "@semantic-release/exec",
        {
          verifyConditionsCmd:
            "if [ -z \"$NEXT_RELEASE_VERSION_FILE\" ] && [ -z \"$NUGET_API_KEY\" ]; "
            + "then echo \"NUGET_API_KEY is required.\"; exit 1; fi",
          verifyReleaseCmd:
            `if [ -n "$NEXT_RELEASE_VERSION_FILE" ]; then printf '%s\\n' '\${nextRelease.version}' `
            + `> "$NEXT_RELEASE_VERSION_FILE"; else bash .github/release/verify-release-assets.sh `
            + `'\${nextRelease.version}' '${packageId}'; fi`,
          publishCmd:
            `dotnet nuget push "${artifactPath}/${packageId}.\${nextRelease.version}.nupkg" `
            + "--source https://api.nuget.org/v3/index.json --api-key \"$NUGET_API_KEY\" "
            + "--symbol-source https://api.nuget.org/v3/index.json --symbol-api-key \"$NUGET_API_KEY\" "
            + "--skip-duplicate",
        },
      ],
      [
        "@semantic-release/github",
        {
          assets: [
            { path: `${artifactPath}/*.nupkg`, label: `${packageId} NuGet package` },
            { path: `${artifactPath}/*.snupkg`, label: `${packageId} symbols` },
          ],
          successComment: false,
          failComment: false,
        },
      ],
    ],
  };
}
