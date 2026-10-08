import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import process from "node:process";
import { pathToFileURL } from "node:url";
import { promisify } from "node:util";
import { makeExtensionTag } from "./create-extension-release-config.mjs";

const execFileAsync = promisify(execFile);

async function runGit(args) {
  const { stdout } = await execFileAsync("git", args);
  return stdout;
}

async function githubRequest(method, path, { apiUrl, token }) {
  const response = await fetch(`${apiUrl}${path}`, {
    method,
    headers: {
      accept: "application/vnd.github+json",
      authorization: `Bearer ${token}`,
      "user-agent": "aspire-extensions-release",
      "x-github-api-version": "2022-11-28",
    },
  });
  const text = await response.text();
  let body = null;
  try {
    body = text ? JSON.parse(text) : null;
  } catch {
    body = text;
  }
  return { status: response.status, body };
}

function expectStatus(response, expected, description) {
  if (response.status !== expected) {
    throw new Error(
      `${description} returned HTTP ${response.status} instead of ${expected}: `
      + `${JSON.stringify(response.body)}`,
    );
  }
}

export async function readReleaseVersion(versionFile) {
  try {
    return (await readFile(versionFile, "utf8")).trim() || null;
  } catch (error) {
    if (error.code === "ENOENT") {
      return null;
    }
    throw error;
  }
}

/**
 * semantic-release pushes the release tag before it publishes the packages and the GitHub
 * release. When a later step fails, the tag stays behind and every following run treats it as a
 * completed release, so that version is never published. Removing the tag lets a rerun select
 * the same version again. Publishing stays idempotent because NuGet pushes skip duplicates and
 * the GitHub release is created only once.
 */
export async function removeIncompleteReleaseTag({
  tag,
  headSha,
  repository,
  request,
  git = runGit,
}) {
  const encodedTag = encodeURIComponent(tag);
  const tagRef = await request("GET", `/repos/${repository}/git/ref/tags/${encodedTag}`);
  if (tagRef.status === 404) {
    return { removed: false, reason: `Tag ${tag} does not exist, so there is nothing to remove.` };
  }
  expectStatus(tagRef, 200, `Reading tag ${tag}`);

  const { object } = tagRef.body;
  if (object.type !== "commit" || object.sha !== headSha) {
    return {
      removed: false,
      reason: `Tag ${tag} points at ${object.type} ${object.sha}, not at release commit ${headSha}, `
        + "so it was not created by an earlier attempt of this release.",
    };
  }

  const release = await request("GET", `/repos/${repository}/releases/tags/${encodedTag}`);
  if (release.status === 200) {
    return { removed: false, reason: `Release ${tag} is already published, so the tag stays.` };
  }
  expectStatus(release, 404, `Reading release ${tag}`);

  const deletion = await request("DELETE", `/repos/${repository}/git/refs/tags/${encodedTag}`);
  expectStatus(deletion, 204, `Deleting tag ${tag}`);

  if ((await git(["tag", "--list", tag])).trim() === tag) {
    await git(["tag", "--delete", tag]);
  }

  return {
    removed: true,
    reason: `Removed tag ${tag}: an earlier attempt pushed it to ${headSha} without publishing the `
      + "release, so semantic-release can now retry that version.",
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [tagPrefix, artifactDirectoryName, headSha] = process.argv.slice(2);
  const { GITHUB_API_URL = "https://api.github.com", GITHUB_REPOSITORY, GITHUB_TOKEN } = process.env;
  if (!tagPrefix || !artifactDirectoryName || !/^[0-9a-f]{40}$/u.test(headSha ?? "")) {
    console.error("Usage: remove-incomplete-release-tag.mjs <tag-prefix> <artifact-directory-name> <head-sha>");
    process.exitCode = 2;
  } else if (!GITHUB_REPOSITORY || !GITHUB_TOKEN) {
    console.error("GITHUB_REPOSITORY and GITHUB_TOKEN are required.");
    process.exitCode = 2;
  } else {
    const version = await readReleaseVersion(`artifacts/${artifactDirectoryName}/release-version.txt`);
    if (version === null) {
      console.log("The CI artifact does not select a release version, so there is no release tag to remove.");
    } else {
      const result = await removeIncompleteReleaseTag({
        tag: makeExtensionTag(tagPrefix, version),
        headSha,
        repository: GITHUB_REPOSITORY,
        request: (method, path) => githubRequest(method, path, { apiUrl: GITHUB_API_URL, token: GITHUB_TOKEN }),
      });
      console.log(result.reason);
    }
  }
}
