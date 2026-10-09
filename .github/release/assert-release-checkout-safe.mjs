import { lstat, readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import { pathToFileURL } from "node:url";

// Reviewed against semantic-release/lib/get-config.js and cosmiconfig/dist/{index,defaults}.js.
// Both CLI and API discover configuration before applying explicit options.
export const reviewedVersions = { semanticRelease: "25.0.9", cosmiconfig: "9.0.2" };
export const releaseSearchPlaces = [
  "package.json",
  ".releaserc",
  ".releaserc.json",
  ".releaserc.yaml",
  ".releaserc.yml",
  ".releaserc.js",
  ".releaserc.ts",
  ".releaserc.cjs",
  ".releaserc.mjs",
  ".config/releaserc",
  ".config/releaserc.json",
  ".config/releaserc.yaml",
  ".config/releaserc.yml",
  ".config/releaserc.js",
  ".config/releaserc.ts",
  ".config/releaserc.cjs",
  ".config/releaserc.mjs",
  "release.config.js",
  "release.config.ts",
  "release.config.cjs",
  "release.config.mjs",
];
export const metaSearchPlaces = [
  "package.json",
  "package.yaml",
  ".config/config.json",
  ".config/config.yaml",
  ".config/config.yml",
  ".config/config.js",
  ".config/config.ts",
  ".config/config.cjs",
  ".config/config.mjs",
];

export function assertReviewedVersions(versions) {
  for (const [name, version] of Object.entries(reviewedVersions)) {
    if (versions[name] !== version) {
      throw new Error(`Re-check release configuration discovery before using ${name} ${versions[name]}; reviewed ${version}.`);
    }
  }
}

async function exists(filename) {
  try {
    await lstat(filename);
    return true;
  } catch (error) {
    if (error.code === "ENOENT") return false;
    throw error;
  }
}

export async function assertReleaseCheckoutSafe(cwd = process.cwd()) {
  const semanticReleasePackage = new URL("./node_modules/semantic-release/package.json", import.meta.url);
  const require = createRequire(semanticReleasePackage);
  assertReviewedVersions({
    semanticRelease: JSON.parse(await readFile(semanticReleasePackage, "utf8")).version,
    cosmiconfig: JSON.parse(await readFile(require.resolve("cosmiconfig/package.json"), "utf8")).version,
  });

  // Reject package.json even without a release key: get-config also reads its repository URL.
  // Meta configs can change search places or import another file. Do not parse any checkout config.
  for (const filename of new Set([...releaseSearchPlaces, ...metaSearchPlaces, ".npmrc", "node_modules"])) {
    if (await exists(path.join(cwd, filename))) {
      throw new Error(`Untrusted release checkout configuration: ${filename}. Refusing to launch semantic-release.`);
    }
  }
  // read-package-up walks ancestors even though cosmiconfig's reviewed default searches only cwd.
  let directory = path.dirname(path.resolve(cwd));
  while (true) {
    if (await exists(path.join(directory, "package.json"))) {
      throw new Error(`Untrusted ancestor package.json: ${directory}. Refusing to launch semantic-release.`);
    }
    const parent = path.dirname(directory);
    if (parent === directory) break;
    directory = parent;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    await assertReleaseCheckoutSafe();
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
