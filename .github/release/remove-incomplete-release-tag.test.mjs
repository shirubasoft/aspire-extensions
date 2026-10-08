import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { readReleaseVersion, removeIncompleteReleaseTag } from "./remove-incomplete-release-tag.mjs";

const headSha = "edd808d19e24692a2ee163afbc0a07cc6d286954";
const tag = "resource-groups-v1.2.1";
const repository = "shirubasoft/aspire-extensions";

function fakeGitHub({
  tagSha = headSha,
  tagType = "commit",
  releaseStatus = 404,
  releases = releaseStatus === 200 ? [{ tag_name: tag, draft: false }] : [],
  listStatus = 200,
  deleteStatus = 204,
} = {}) {
  const calls = [];
  const responses = {
    [`GET /repos/${repository}/git/ref/tags/${tag}`]: tagSha === null
      ? { status: 404, body: { message: "Not Found" } }
      : { status: 200, body: { ref: `refs/tags/${tag}`, object: { type: tagType, sha: tagSha } } },
    [`GET /repos/${repository}/releases/tags/${tag}`]: { status: releaseStatus, body: {} },
    [`DELETE /repos/${repository}/git/refs/tags/${tag}`]: { status: deleteStatus, body: null },
  };
  const request = async (method, requestPath) => {
    calls.push(`${method} ${requestPath}`);
    const page = requestPath.match(/\/releases\?per_page=100&page=(\d+)$/u);
    if (method === "GET" && page) {
      const offset = (Number(page[1]) - 1) * 100;
      return { status: listStatus, body: releases.slice(offset, offset + 100) };
    }
    return responses[`${method} ${requestPath}`] ?? { status: 500, body: { message: "unexpected" } };
  };
  return { calls, request };
}

function fakeGit({ localTags = [tag], remoteMainSha = headSha } = {}) {
  const calls = [];
  const git = async (args) => {
    calls.push(args.join(" "));
    if (args[0] === "rev-parse") {
      return `${remoteMainSha}\n`;
    }
    if (args[0] === "tag" && args[1] === "--list") {
      return localTags.includes(args[2]) ? `${args[2]}\n` : "";
    }
    return "";
  };
  return { calls, git };
}

test("removes a tag that an earlier attempt pushed without publishing its release", async () => {
  const github = fakeGitHub();
  const git = fakeGit();

  const result = await removeIncompleteReleaseTag({ tag, headSha, repository, request: github.request, git: git.git });

  assert.equal(result.removed, true);
  assert.deepEqual(github.calls, [
    `GET /repos/${repository}/git/ref/tags/${tag}`,
    `GET /repos/${repository}/releases?per_page=100&page=1`,
    `GET /repos/${repository}/releases?per_page=100&page=1`,
  ]);
  assert.deepEqual(git.calls, [
    "fetch --no-tags origin +refs/heads/main:refs/remotes/origin/main",
    "rev-parse refs/remotes/origin/main",
    `push --force-with-lease=refs/tags/${tag}:${headSha} origin :refs/tags/${tag}`,
    `tag --list ${tag}`,
    `tag --delete ${tag}`,
  ]);
});

test("keeps the remote clone consistent when the tag was never fetched locally", async () => {
  const github = fakeGitHub();
  const git = fakeGit({ localTags: [] });

  const result = await removeIncompleteReleaseTag({ tag, headSha, repository, request: github.request, git: git.git });

  assert.equal(result.removed, true);
  assert.deepEqual(git.calls, [
    "fetch --no-tags origin +refs/heads/main:refs/remotes/origin/main",
    "rev-parse refs/remotes/origin/main",
    `push --force-with-lease=refs/tags/${tag}:${headSha} origin :refs/tags/${tag}`,
    `tag --list ${tag}`,
  ]);
});

test("does nothing when the tag does not exist", async () => {
  const github = fakeGitHub({ tagSha: null });
  const git = fakeGit();

  const result = await removeIncompleteReleaseTag({ tag, headSha, repository, request: github.request, git: git.git });

  assert.equal(result.removed, false);
  assert.match(result.reason, /does not exist/u);
  assert.deepEqual(github.calls, [`GET /repos/${repository}/git/ref/tags/${tag}`]);
  assert.deepEqual(git.calls, []);
});

test("fails without deleting a tag whose release is already published", async () => {
  const github = fakeGitHub({ releaseStatus: 200 });
  const git = fakeGit();

  await assert.rejects(
    removeIncompleteReleaseTag({ tag, headSha, repository, request: github.request, git: git.git }),
    /published.*tag stays/iu,
  );
  assert.ok(!github.calls.some((call) => call.startsWith("DELETE")));
  assert.deepEqual(git.calls, []);
});

test("preserves a draft and asks for manual recovery even when lookup by tag returns 404", async () => {
  const github = fakeGitHub({ releases: [{ tag_name: tag, draft: true }] });
  const git = fakeGit();

  await assert.rejects(
    removeIncompleteReleaseTag({ tag, headSha, repository, request: github.request, git: git.git }),
    /draft.*publish or delete.*manually.*rerun/iu,
  );

  assert.ok(!github.calls.some((call) => call.startsWith("DELETE")));
  assert.deepEqual(git.calls, []);
});

test("finds a matching release after the first full page", async () => {
  for (const draft of [true, false]) {
    const releases = Array.from({ length: 100 }, (_, index) => ({ tag_name: `other-v${index}`, draft: false }));
    releases.push({ tag_name: tag, draft });
    const github = fakeGitHub({ releases });
    const git = fakeGit();

    await assert.rejects(
      removeIncompleteReleaseTag({ tag, headSha, repository, request: github.request, git: git.git }),
      /tag stays/iu,
    );

    assert.ok(github.calls.includes(`GET /repos/${repository}/releases?per_page=100&page=2`));
    assert.ok(!github.calls.some((call) => call.startsWith("DELETE")));
    assert.deepEqual(git.calls, []);
  }
});

test("checks the empty page after a full page before removing an orphan", async () => {
  const releases = Array.from({ length: 100 }, (_, index) => ({ tag_name: `other-v${index}`, draft: false }));
  const github = fakeGitHub({ releases });
  const git = fakeGit();

  const result = await removeIncompleteReleaseTag({ tag, headSha, repository, request: github.request, git: git.git });

  assert.equal(result.removed, true);
  assert.ok(github.calls.includes(`GET /repos/${repository}/releases?per_page=100&page=2`));
});

test("a tag retargeted after the identity check survives the deletion lease", async () => {
  let remoteSha = headSha;
  const changedSha = "1".repeat(40);
  const github = fakeGitHub();
  const gitCalls = [];
  const request = async (method, requestPath) => {
    const response = await github.request(method, requestPath);
    if (requestPath.includes("/releases?")) {
      remoteSha = changedSha;
    }
    if (method === "DELETE") {
      remoteSha = null;
    }
    return response;
  };
  const git = async (args) => {
    gitCalls.push(args);
    if (args[0] === "rev-parse") {
      return headSha;
    }
    if (args[0] === "push") {
      assert.ok(args.includes(`--force-with-lease=refs/tags/${tag}:${headSha}`));
      if (remoteSha !== headSha) {
        throw new Error("stale info");
      }
      remoteSha = null;
    }
    return "";
  };

  await assert.rejects(removeIncompleteReleaseTag({ tag, headSha, repository, request, git }), /stale info/u);

  assert.equal(remoteSha, changedSha);
  assert.ok(!gitCalls.some((args) => args[0] === "tag"));
});

test("rechecks releases before deleting and preserves a draft created after the first scan", async () => {
  let scans = 0;
  const github = fakeGitHub();
  const git = fakeGit();
  const request = async (method, requestPath) => {
    if (requestPath.includes("/releases?")) {
      scans += 1;
      if (scans === 2) {
        return { status: 200, body: [{ tag_name: tag, draft: true }] };
      }
    }
    return github.request(method, requestPath);
  };

  await assert.rejects(
    removeIncompleteReleaseTag({ tag, headSha, repository, request, git: git.git }),
    /draft.*tag stays/iu,
  );
  assert.equal(scans, 2);
  assert.ok(!github.calls.some((call) => call.startsWith("DELETE")));
  assert.deepEqual(git.calls, [
    "fetch --no-tags origin +refs/heads/main:refs/remotes/origin/main",
    "rev-parse refs/remotes/origin/main",
  ]);
});

test("authenticates Git with process-only configuration without exposing credentials on failure", async () => {
  const { createAuthenticatedGit } = await import("./remove-incomplete-release-tag.mjs");
  const token = "test-only-token";
  const authorization = `AUTHORIZATION: basic ${Buffer.from(`x-access-token:${token}`).toString("base64")}`;
  const args = ["push", `--force-with-lease=refs/tags/${tag}:${headSha}`, "origin", `:refs/tags/${tag}`];
  const envBefore = { ...process.env };
  let calls = 0;
  const git = createAuthenticatedGit({
    token,
    serverUrl: "https://github.example.test",
    git: async (actualArgs, options) => {
      calls += 1;
      assert.deepEqual(actualArgs, args);
      assert.equal(options.env.GIT_CONFIG_COUNT, "3");
      assert.equal(options.env.GIT_CONFIG_KEY_0, "credential.helper");
      assert.equal(options.env.GIT_CONFIG_VALUE_0, "");
      assert.equal(options.env.GIT_CONFIG_KEY_1, "http.https://github.example.test/.extraheader");
      assert.equal(options.env.GIT_CONFIG_VALUE_1, "");
      assert.equal(options.env.GIT_CONFIG_KEY_2, "http.https://github.example.test/.extraheader");
      assert.equal(options.env.GIT_CONFIG_VALUE_2, authorization);
      assert.ok(!actualArgs.join(" ").includes(token));
      if (calls === 2) {
        throw new Error(`git error containing ${token} and ${authorization}`);
      }
      return "success";
    },
  });

  assert.equal(await git(args), "success");
  await assert.rejects(git(args), (error) => {
    assert.match(error.message, /authenticated git.*failed/iu);
    assert.ok(!error.message.includes(token));
    assert.ok(!error.message.includes(authorization));
    assert.equal(error.cause, undefined);
    return true;
  });
  assert.ok(JSON.stringify({ ...process.env }) === JSON.stringify(envBefore), "Git must not mutate the parent environment");
});

test("preserves remote and local tags when a fresh fetch finds main has advanced", async () => {
  const github = fakeGitHub();
  const currentMain = "2".repeat(40);
  const git = fakeGit({ remoteMainSha: currentMain });

  const result = await removeIncompleteReleaseTag({ tag, headSha, repository, request: github.request, git: git.git });

  assert.equal(result.removed, false);
  assert.match(result.reason, /main.*advanced|current remote main/iu);
  assert.ok(result.reason.includes(currentMain));
  assert.match(result.reason, /tag .*stays.*verify.*fresh.*push/iu);
  assert.deepEqual(git.calls, [
    "fetch --no-tags origin +refs/heads/main:refs/remotes/origin/main",
    "rev-parse refs/remotes/origin/main",
  ]);
  assert.ok(!github.calls.some((call) => call.startsWith("DELETE")));
});

test("fetches main before deletion rather than trusting the checkout's cached tip", async () => {
  const github = fakeGitHub();
  const git = fakeGit();

  const result = await removeIncompleteReleaseTag({ tag, headSha, repository, request: github.request, git: git.git });

  assert.equal(result.removed, true);
  const fetchIndex = git.calls.indexOf("fetch --no-tags origin +refs/heads/main:refs/remotes/origin/main");
  const mainIndex = git.calls.indexOf("rev-parse refs/remotes/origin/main");
  const deletionIndex = git.calls.findIndex((call) => call.startsWith("push "));
  assert.ok(fetchIndex >= 0 && mainIndex > fetchIndex && deletionIndex > mainIndex);
});

test("a failed main fetch prevents remote and local deletion", async () => {
  const github = fakeGitHub();
  const git = fakeGit();

  await assert.rejects(
    removeIncompleteReleaseTag({ tag, headSha, repository, request: github.request, git: async (args) => {
      if (args[0] === "fetch") {
        throw new Error("fetch failed");
      }
      return git.git(args);
    } }),
    /fetch failed/u,
  );

  assert.deepEqual(git.calls, []);
  assert.ok(!github.calls.some((call) => call.startsWith("DELETE")));
});

test("leaves a tag that points at another commit or at an annotated tag object", async () => {
  for (const options of [{ tagSha: "0".repeat(40) }, { tagType: "tag" }]) {
    const github = fakeGitHub(options);
    const git = fakeGit();

    const result = await removeIncompleteReleaseTag({ tag, headSha, repository, request: github.request, git: git.git });

    assert.equal(result.removed, false);
    assert.match(result.reason, /not at release commit/u);
    assert.deepEqual(github.calls, [`GET /repos/${repository}/git/ref/tags/${tag}`]);
    assert.deepEqual(git.calls, []);
  }
});

test("fails loudly on unexpected GitHub responses", async () => {
  await assert.rejects(
    removeIncompleteReleaseTag({ tag, headSha, repository, request: async () => ({ status: 503, body: "down" }), git: fakeGit().git }),
    /Reading tag resource-groups-v1\.2\.1 returned HTTP 503/u,
  );
  await assert.rejects(
    removeIncompleteReleaseTag({ tag, headSha, repository, request: fakeGitHub({ listStatus: 500 }).request, git: fakeGit().git }),
    /Listing releases for resource-groups-v1\.2\.1 returned HTTP 500/u,
  );
  const github = fakeGitHub();
  const git = fakeGit();
  await assert.rejects(
    removeIncompleteReleaseTag({ tag, headSha, repository, request: github.request, git: async (args) => {
      if (args[0] === "push") {
        throw new Error("push rejected");
      }
      return git.git(args);
    } }),
    /push rejected/u,
  );
  assert.deepEqual(git.calls, [
    "fetch --no-tags origin +refs/heads/main:refs/remotes/origin/main",
    "rev-parse refs/remotes/origin/main",
  ]);
});

test("reads the CI-selected release version and tolerates a missing version file", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "release-version-"));
  const versionFile = path.join(directory, "release-version.txt");

  assert.equal(await readReleaseVersion(versionFile), null);
  await writeFile(versionFile, "1.2.1\n");
  assert.equal(await readReleaseVersion(versionFile), "1.2.1");
});
