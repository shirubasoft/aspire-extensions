import path from "node:path";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const extensionScopedCommitsPlugin = fileURLToPath(
  new URL("./extension-scoped-conventional-commits.mjs", import.meta.url),
);
const execPlugin = require.resolve("@semantic-release/exec");
const githubPlugin = require.resolve("@semantic-release/github");
const verifyReleaseAssetsScript = shellQuote(fileURLToPath(
  new URL("./verify-release-assets.sh", import.meta.url),
));
const publishReleaseAssetsScript = shellQuote(fileURLToPath(
  new URL("./publish-release-assets.sh", import.meta.url),
));

function shellQuote(value) {
  return `'${value.replaceAll("'", "'\\''")}'`;
}

export function makeExtensionTag(tagPrefix, version) {
  return `${tagPrefix}-v${version}`;
}

export function createExtensionReleaseConfig({
  extensionPath,
  packageId,
  packages = [{ id: packageId, symbols: true }],
  tagPrefix,
}) {
  if (!extensionPath || !packageId || !tagPrefix) {
    throw new TypeError("extensionPath, packageId, and tagPrefix are required.");
  }
  if (!Array.isArray(packages) || packages.length === 0) {
    throw new TypeError("packages must contain at least one package.");
  }
  for (const packageDefinition of packages) {
    if (!packageDefinition?.id || typeof packageDefinition.symbols !== "boolean") {
      throw new TypeError("Each package requires an id and a symbols boolean.");
    }
  }

  const artifactPath = process.env.RELEASE_ARTIFACTS_DIR || path.resolve(`artifacts/${packageId}`);
  if (!path.isAbsolute(artifactPath)) {
    throw new TypeError("RELEASE_ARTIFACTS_DIR must be an absolute path.");
  }
  const packageSpecs = packages
    .map((packageDefinition) =>
      `${packageDefinition.id}${packageDefinition.symbols ? ":symbols" : ""}`)
    .join(";");
  const conventionalConfig = {
    preset: "conventionalcommits",
    extensionPath,
    sharedPaths: ["Directory.Build.props", "Directory.Packages.props", "global.json"],
  };

  return {
    branches: ["main"],
    tagFormat: makeExtensionTag(tagPrefix, "${version}"),
    plugins: [
      [extensionScopedCommitsPlugin, conventionalConfig],
      [
        execPlugin,
        {
          verifyConditionsCmd:
            "if [ -z \"$NEXT_RELEASE_VERSION_FILE\" ] && [ -z \"$NUGET_API_KEY\" ]; "
            + "then echo \"NUGET_API_KEY is required.\"; exit 1; fi",
          verifyReleaseCmd:
            `if [ -n "$NEXT_RELEASE_VERSION_FILE" ]; then printf '%s\\n' '\${nextRelease.version}' `
            + `> "$NEXT_RELEASE_VERSION_FILE"; else bash ${verifyReleaseAssetsScript} `
            + `'\${nextRelease.version}' '${packageId}' '${packageSpecs}'; fi`,
          publishCmd:
            `bash ${publishReleaseAssetsScript} `
            + `'\${nextRelease.version}' '${packageId}' '${packageSpecs}'`,
        },
      ],
      [
        githubPlugin,
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
