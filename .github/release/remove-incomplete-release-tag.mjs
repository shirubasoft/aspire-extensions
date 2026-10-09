import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import os from "node:os";
import process from "node:process";
import { pathToFileURL } from "node:url";
import { promisify } from "node:util";
import { makeExtensionTag } from "./create-extension-release-config.mjs";

const execFileAsync = promisify(execFile);

async function runGit(args, options = {}) {
  const { stdout } = await execFileAsync("git", args, options);
  return stdout;
}

export function createAuthenticatedGit({ token, repository, serverUrl = "https://github.com", git = runGit }) {
  if (!token) {
    throw new Error("A write-capable GITHUB_TOKEN is required for authenticated Git recovery.");
  }
  if (!repository) {
    throw new Error("GITHUB_REPOSITORY is required for authenticated Git recovery.");
  }
  const repositoryUrl = `${serverUrl.replace(/\/+$/u, "")}/${repository}`;
  // Git matches paths at slash boundaries; checkout omits .git while other origins include it.
  const headerKey = `http.${repositoryUrl}.extraheader`;
  const gitHeaderKey = `http.${repositoryUrl}.git.extraheader`;
  const authorization = Buffer.from(`x-access-token:${token}`).toString("base64");
  return async (args) => {
    try {
      return await git(args, {
        env: {
          ...process.env,
          GIT_TERMINAL_PROMPT: "0",
          GIT_TRACE: "0",
          GIT_TRACE_CURL: "0",
          // Git enables this legacy trace whenever it is present, even with value "0".
          GIT_CURL_VERBOSE: undefined,
          GIT_TRACE2: "0",
          GIT_TRACE2_EVENT: "0",
          GIT_TRACE2_PERF: "0",
          GIT_CONFIG_COUNT: "7",
          GIT_CONFIG_KEY_0: "credential.helper",
          GIT_CONFIG_VALUE_0: "",
          GIT_CONFIG_KEY_1: "http.followRedirects",
          GIT_CONFIG_VALUE_1: "false",
          GIT_CONFIG_KEY_2: headerKey,
          GIT_CONFIG_VALUE_2: "",
          GIT_CONFIG_KEY_3: headerKey,
          GIT_CONFIG_VALUE_3: `AUTHORIZATION: basic ${authorization}`,
          GIT_CONFIG_KEY_4: gitHeaderKey,
          GIT_CONFIG_VALUE_4: "",
          GIT_CONFIG_KEY_5: gitHeaderKey,
          GIT_CONFIG_VALUE_5: `AUTHORIZATION: basic ${authorization}`,
          GIT_CONFIG_KEY_6: "core.hooksPath",
          GIT_CONFIG_VALUE_6: os.devNull,
        },
      });
    } catch {
      // Child-process errors can include credentials. Keep their output out of job logs.
      throw new Error("Authenticated Git recovery failed. Check token permissions, origin connectivity, "
        + "and whether the tag changed since verification before rerunning.");
    }
  };
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

async function requireNoRelease({ tag, repository, request }) {
  // The write-capable job token can see drafts in this list; lookup by tag cannot.
  for (let page = 1; ; page += 1) {
    const response = await request("GET", `/repos/${repository}/releases?per_page=100&page=${page}`);
    expectStatus(response, 200, `Listing releases for ${tag}`);
    if (!Array.isArray(response.body)) {
      throw new Error(`Listing releases for ${tag} did not return an array. The tag stays.`);
    }
    const release = response.body.find((item) => item.tag_name === tag);
    if (release) {
      throw new Error(release.draft
        ? `Release ${tag} has a draft; the tag stays. Publish or delete the draft manually, then rerun. `
          + "Check its uploaded assets and NuGet packages before choosing recovery."
        : `Release ${tag} is already published; the tag stays. Verify its assets and NuGet packages `
          + "and recover any missing files manually before rerunning.");
    }
    if (response.body.length < 100) {
      return;
    }
  }
}

/**
 * semantic-release pushes the release tag before it publishes the packages and the GitHub
 * release. When a later step fails, the tag stays behind and every following run treats it as a
 * completed release, so that version is never published. Removing the tag lets a rerun select
 * the same version again. NuGet pushes skip duplicates. Any matching GitHub release, including
 * a draft left by an asset-upload failure, requires manual recovery before retrying.
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

  await requireNoRelease({ tag, repository, request });

  // Fetch without tags so this guard leaves even the local release tag untouched.
  await git(["fetch", "--no-tags", "origin", "+refs/heads/main:refs/remotes/origin/main"]);
  const currentMain = (await git(["rev-parse", "refs/remotes/origin/main"])).trim();
  if (currentMain !== headSha) {
    return {
      removed: false,
      reason: `Current remote main is ${currentMain}, not release commit ${headSha}; tag ${tag} stays. `
        + "Verify releases, drafts and NuGet packages before any manual orphan-tag repair, then use a "
        + "fresh main push build that includes this extension. Keep main at that commit until publishing finishes.",
    };
  }

  await requireNoRelease({ tag, repository, request });
  await git(["push", `--force-with-lease=refs/tags/${tag}:${object.sha}`, "origin", `:refs/tags/${tag}`]);

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
  const {
    GITHUB_API_URL = "https://api.github.com",
    GITHUB_SERVER_URL = "https://github.com",
    GITHUB_REPOSITORY,
    GITHUB_TOKEN,
  } = process.env;
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
        git: createAuthenticatedGit({ token: GITHUB_TOKEN, repository: GITHUB_REPOSITORY, serverUrl: GITHUB_SERVER_URL }),
      });
      console.log(result.reason);
    }
  }
}
