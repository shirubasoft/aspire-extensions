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

function fakeGit({ localTags = [tag] } = {}) {
  const calls = [];
  const git = async (args) => {
    calls.push(args.join(" "));
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
    `DELETE /repos/${repository}/git/refs/tags/${tag}`,
  ]);
  assert.deepEqual(git.calls, [`tag --list ${tag}`, `tag --delete ${tag}`]);
});

test("keeps the remote clone consistent when the tag was never fetched locally", async () => {
  const github = fakeGitHub();
  const git = fakeGit({ localTags: [] });

  const result = await removeIncompleteReleaseTag({ tag, headSha, repository, request: github.request, git: git.git });

  assert.equal(result.removed, true);
  assert.deepEqual(git.calls, [`tag --list ${tag}`]);
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
  const github = fakeGitHub({ deleteStatus: 403 });
  await assert.rejects(
    removeIncompleteReleaseTag({ tag, headSha, repository, request: github.request, git: fakeGit().git }),
    /Deleting tag resource-groups-v1\.2\.1 returned HTTP 403/u,
  );
});

test("reads the CI-selected release version and tolerates a missing version file", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "release-version-"));
  const versionFile = path.join(directory, "release-version.txt");

  assert.equal(await readReleaseVersion(versionFile), null);
  await writeFile(versionFile, "1.2.1\n");
  assert.equal(await readReleaseVersion(versionFile), "1.2.1");
});
