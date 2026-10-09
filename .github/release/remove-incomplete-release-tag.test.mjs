import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { once } from "node:events";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { promisify } from "node:util";
import { readReleaseVersion, removeIncompleteReleaseTag } from "./remove-incomplete-release-tag.mjs";

const headSha = "edd808d19e24692a2ee163afbc0a07cc6d286954";
const tag = "resource-groups-v1.2.1";
const repository = "shirubasoft/aspire-extensions";
const execFileAsync = promisify(execFile);

async function listen(t, handler) {
  const server = createServer(handler);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  t.after(() => new Promise((resolve) => server.close(resolve)));
  return `http://127.0.0.1:${server.address().port}`;
}

async function httpGitRepository(t) {
  const directory = await mkdtemp(path.join(os.tmpdir(), "release-http-git-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const remote = path.join(directory, `${repository}.git`);
  const local = path.join(directory, "client");
  const env = {
    ...process.env,
    GIT_CONFIG_NOSYSTEM: "1",
    GIT_CONFIG_GLOBAL: os.devNull,
    GIT_CONFIG_COUNT: "0",
    GIT_CONFIG_PARAMETERS: undefined,
  };
  const git = async (args, options = {}) => (await execFileAsync("git", args, {
    ...options,
    env: { ...env, ...options.env, GIT_CONFIG_NOSYSTEM: "1", GIT_CONFIG_GLOBAL: os.devNull },
    timeout: 10000,
  })).stdout;
  await mkdir(path.dirname(remote), { recursive: true });
  await git(["init", "--bare", "--initial-branch=main", "--object-format=sha1", remote]);
  await git(["-C", remote, "config", "http.receivepack", "true"]);
  await git(["init", "--initial-branch=main", "--object-format=sha1", local]);
  await git(["-C", local, "-c", "user.name=HTTP test", "-c", "user.email=http-test@example.test",
    "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "HTTP authentication fixture"]);
  const sha = (await git(["-C", local, "rev-parse", "HEAD"])).trim();
  const backend = (request, response) => {
    const url = new URL(request.url, "http://localhost");
    const child = execFile("git", ["http-backend"], {
      env: {
        ...env,
        GIT_PROJECT_ROOT: directory,
        GIT_HTTP_EXPORT_ALL: "1",
        PATH_INFO: url.pathname.replace(`/${repository}/`, `/${repository}.git/`),
        QUERY_STRING: url.search.slice(1),
        REQUEST_METHOD: request.method,
        CONTENT_TYPE: request.headers["content-type"] ?? "",
        CONTENT_LENGTH: request.headers["content-length"] ?? "",
        REMOTE_ADDR: "127.0.0.1",
        SERVER_PROTOCOL: "HTTP/1.1",
      },
      encoding: "buffer",
      timeout: 10000,
    }, (error, stdout) => {
      if (error) {
        response.writeHead(500);
        response.end();
        return;
      }
      const boundary = stdout.indexOf("\r\n\r\n");
      let status = 200;
      const headers = {};
      for (const line of stdout.subarray(0, boundary).toString().split("\r\n")) {
        const separator = line.indexOf(":");
        const key = line.slice(0, separator).toLowerCase();
        const value = line.slice(separator + 1).trim();
        if (key === "status") {
          status = Number(value.split(" ")[0]);
        } else {
          headers[key] = value;
        }
      }
      response.writeHead(status, headers);
      response.end(stdout.subarray(boundary + 4));
    });
    request.pipe(child.stdin);
  };
  return { backend, git, local, remote, sha };
}

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
    repository,
    serverUrl: "https://github.example.test",
    git: async (actualArgs, options) => {
      calls += 1;
      assert.deepEqual(actualArgs, args);
      assert.equal(options.env.GIT_CONFIG_COUNT, "7");
      assert.equal(options.env.GIT_CONFIG_KEY_0, "credential.helper");
      assert.equal(options.env.GIT_CONFIG_VALUE_0, "");
      assert.equal(options.env.GIT_CONFIG_KEY_1, "http.followRedirects");
      assert.equal(options.env.GIT_CONFIG_VALUE_1, "false");
      assert.equal(options.env.GIT_CONFIG_KEY_2, `http.https://github.example.test/${repository}.extraheader`);
      assert.equal(options.env.GIT_CONFIG_VALUE_2, "");
      assert.equal(options.env.GIT_CONFIG_KEY_3, `http.https://github.example.test/${repository}.extraheader`);
      assert.equal(options.env.GIT_CONFIG_VALUE_3, authorization);
      assert.equal(options.env.GIT_CONFIG_KEY_4, `http.https://github.example.test/${repository}.git.extraheader`);
      assert.equal(options.env.GIT_CONFIG_VALUE_4, "");
      assert.equal(options.env.GIT_CONFIG_KEY_5, `http.https://github.example.test/${repository}.git.extraheader`);
      assert.equal(options.env.GIT_CONFIG_VALUE_5, authorization);
      assert.equal(options.env.GIT_CONFIG_KEY_6, "core.hooksPath");
      assert.equal(options.env.GIT_CONFIG_VALUE_6, os.devNull);
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

test("actual authenticated HTTP Git sends the header without enabling HTTP tracing", async (t) => {
  const { createAuthenticatedGit } = await import("./remove-incomplete-release-tag.mjs");
  const token = "local-http-test-token";
  let authorization;
  const server = createServer((request, response) => {
    authorization = request.headers.authorization;
    response.writeHead(404);
    response.end();
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  t.after(() => new Promise((resolve) => server.close(resolve)));
  const serverUrl = `http://127.0.0.1:${server.address().port}`;
  let stderr = "";
  const git = createAuthenticatedGit({ token, repository, serverUrl, git: async (args, options) => {
    try {
      return (await promisify(execFile)("git", args, options)).stdout;
    } catch (error) {
      stderr = error.stderr;
      throw error;
    }
  } });

  await assert.rejects(git(["ls-remote", `${serverUrl}/${repository}.git`]), /Authenticated Git recovery failed/u);
  assert.equal(authorization, `basic ${Buffer.from(`x-access-token:${token}`).toString("base64")}`);
  assert.ok(!stderr.includes("Send header"), "Authenticated Git must not enable HTTP tracing");
});

test("actual authenticated Git refuses a cross-host redirect before sending credentials to its target", async (t) => {
  const { createAuthenticatedGit } = await import("./remove-incomplete-release-tag.mjs");
  const fixture = await httpGitRepository(t);
  const token = "local-redirect-test-token";
  const authorization = `basic ${Buffer.from(`x-access-token:${token}`).toString("base64")}`;
  const redirectedRequests = [];
  const targetUrl = await listen(t, (request, response) => {
    redirectedRequests.push({ method: request.method, url: request.url, authorization: request.headers.authorization });
    fixture.backend(request, response);
  });
  const originalRequests = [];
  const serverUrl = (await listen(t, (request, response) => {
    originalRequests.push({ method: request.method, url: request.url, authorization: request.headers.authorization });
    response.writeHead(302, { location: `${targetUrl}${request.url}` });
    response.end();
  })).replace("127.0.0.1", "localhost");
  await fixture.git(["-C", fixture.local, "remote", "add", "origin", `${serverUrl}/${repository}.git`]);
  const git = createAuthenticatedGit({ token, repository, serverUrl, git: fixture.git });

  const error = await git(["-C", fixture.local, "push", "origin", "HEAD:refs/heads/redirect-test"])
    .then(() => null, (failure) => failure);

  assert.deepEqual(redirectedRequests.filter((request) => request.authorization), [],
    "No Authorization header may reach the redirect target, including a later git-receive-pack POST");
  assert.deepEqual(redirectedRequests, [], "Authenticated Git must not follow the initial redirect");
  assert.match(error?.message ?? "", /Authenticated Git recovery failed/u);
  assert.deepEqual(originalRequests, [{
    method: "GET", url: `/${repository}.git/info/refs?service=git-receive-pack`, authorization,
  }]);
});

test("actual HTTP Git authenticates pushes to both repository URL forms without persisting credentials", async (t) => {
  const { createAuthenticatedGit } = await import("./remove-incomplete-release-tag.mjs");
  const fixture = await httpGitRepository(t);
  const token = "local-push-test-token";
  const authorization = `basic ${Buffer.from(`x-access-token:${token}`).toString("base64")}`;
  const requests = [];
  const serverUrl = await listen(t, (request, response) => {
    requests.push({ method: request.method, url: request.url, authorization: request.headers.authorization });
    if (request.headers.authorization !== authorization) {
      response.writeHead(401, { "www-authenticate": "Basic realm=repository" });
      response.end();
      return;
    }
    fixture.backend(request, response);
  });
  const configFile = path.join(fixture.local, ".git", "config");
  const hooksPath = path.join(fixture.local, "checkout-hooks");
  const hookProbe = path.join(fixture.local, "hook-ran");
  await mkdir(hooksPath);
  await writeFile(path.join(hooksPath, "pre-push"), '#!/bin/sh\ntouch hook-ran\nexit 1\n', { mode: 0o755 });
  await fixture.git(["-C", fixture.local, "config", "core.hooksPath", hooksPath]);
  const git = createAuthenticatedGit({ token, repository, serverUrl: `${serverUrl}/`, git: fixture.git });

  for (const suffix of ["", ".git"]) {
    await fixture.git(["-C", fixture.local, "config", "remote.origin.url", `${serverUrl}/${repository}${suffix}`]);
    const configBefore = await readFile(configFile, "utf8");
    const branch = suffix ? "git-suffix" : "checkout-url";
    await git(["-C", fixture.local, "push", "origin", `HEAD:refs/heads/${branch}`]);
    assert.equal((await fixture.git(["-C", fixture.remote, "rev-parse", `refs/heads/${branch}`])).trim(), fixture.sha);
    assert.equal(await readFile(configFile, "utf8"), configBefore);
    await assert.rejects(readFile(hookProbe), { code: "ENOENT" });
  }
  assert.deepEqual(requests, ["", ".git"].flatMap((suffix) => [
    { method: "GET", url: `/${repository}${suffix}/info/refs?service=git-receive-pack`, authorization },
    { method: "POST", url: `/${repository}${suffix}/git-receive-pack`, authorization },
  ]));
});

test("actual authenticated HTTP Git sends no credentials to other repositories on the same server", async (t) => {
  const { createAuthenticatedGit } = await import("./remove-incomplete-release-tag.mjs");
  const requests = [];
  const serverUrl = await listen(t, (request, response) => {
    requests.push({ url: request.url, authorization: request.headers.authorization });
    response.writeHead(404);
    response.end();
  });
  const git = createAuthenticatedGit({ token: "local-scope-test-token", repository, serverUrl });

  for (const otherRepository of [`${repository}-other.git`, "shirubasoft/another-repo.git", "other-owner/aspire-extensions.git"]) {
    await assert.rejects(git(["ls-remote", `${serverUrl}/${otherRepository}`]), /Authenticated Git recovery failed/u);
  }
  assert.equal(requests.length, 3);
  assert.ok(requests.every((request) => request.authorization === undefined),
    "A repository-scoped token must not be sent to sibling repositories or a longer repository name");
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
