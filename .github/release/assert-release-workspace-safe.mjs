import { lstat, readFile, readdir, realpath } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import { pathToFileURL } from "node:url";

// Re-review ancestor discovery and Git behavior when the locked tooling changes.
export const reviewedVersions = { semanticRelease: "25.0.9", cosmiconfig: "9.0.2" };

export function assertReviewedVersions(versions) {
  for (const [name, version] of Object.entries(reviewedVersions)) {
    if (versions[name] !== version) {
      throw new Error(`Re-check empty-workspace release behavior before using ${name} ${versions[name]}; reviewed ${version}.`);
    }
  }
}

export async function assertReleaseWorkspaceSafe(cwd = process.cwd()) {
  const semanticReleasePackage = new URL("./node_modules/semantic-release/package.json", import.meta.url);
  const require = createRequire(semanticReleasePackage);
  assertReviewedVersions({
    semanticRelease: JSON.parse(await readFile(semanticReleasePackage, "utf8")).version,
    cosmiconfig: JSON.parse(await readFile(require.resolve("cosmiconfig/package.json"), "utf8")).version,
  });

  const workspace = await realpath(cwd);
  const entries = await readdir(workspace);
  if (entries.length !== 1 || entries[0] !== ".git"
      || !(await lstat(path.join(workspace, ".git"))).isDirectory()) {
    throw new Error("Publish workspace must contain only a .git directory. Refusing to launch semantic-release.");
  }

  // read-package-up and .NET search parents. NuGet can also use .nuget/nuget.config.
  let directory = path.dirname(workspace);
  while (true) {
    for (const name of await readdir(directory)) {
      if (name === "package.json" || name === "global.json"
          || /^nuget\.config$/iu.test(name) || /^Directory\.Build\./iu.test(name)) {
        throw new Error(`Untrusted ancestor configuration: ${path.join(directory, name)}. Refusing to launch semantic-release.`);
      }
      if (name === ".nuget") {
        const nested = await readdir(path.join(directory, name)).catch((error) => {
          if (error.code === "ENOTDIR") return [];
          throw error;
        });
        if (nested.some((entry) => /^nuget\.config$/iu.test(entry))) {
          throw new Error(`Untrusted ancestor NuGet configuration: ${path.join(directory, name)}.`);
        }
      }
    }
    const parent = path.dirname(directory);
    if (parent === directory) break;
    directory = parent;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    await assertReleaseWorkspaceSafe();
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
