import process from "node:process";
import { pathToFileURL } from "node:url";
import semver from "semver";

export function isPublishablePrerelease(version) {
  const parsed = semver.parse(version);
  return parsed !== null
    && semver.valid(version) === version
    && parsed.prerelease.length > 0
    && parsed.build.length === 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const version = process.argv[2];
  if (!version || !isPublishablePrerelease(version)) {
    console.error(
      "version must be an exact SemVer prerelease without build metadata "
      + "(for example, 1.0.0-preview.1).",
    );
    process.exitCode = 2;
  }
}
