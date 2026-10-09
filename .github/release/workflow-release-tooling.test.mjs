import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { cp, mkdir, mkdtemp, readFile, rm, symlink, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);
const toolingDirectory = fileURLToPath(new URL(".", import.meta.url));
const workflow = await readFile(new URL("../workflows/_extension-publish.yml", import.meta.url), "utf8");
const packageId = "Shirubasoft.Aspire.Extensions.ResourceGroups";
const extensionPath = `extensions/${packageId}`;
const releaseConfig = `./${extensionPath}/release.config.mjs`;

function stepCommand(name) {
  const step = workflow.split(`      - name: ${name}\n`)[1]?.split("      - name:")[0];
  assert.ok(step, `Workflow step '${name}' exists.`);
  const block = /        run: (\||>-)\n((?:          [^\n]*\n?)+)/u.exec(step);
  if (block) {
    const lines = block[2].trimEnd().split("\n").map((line) => line.slice(10));
    return lines.join(block[1] === "|" ? "\n" : " ");
  }
  return /        run: (.+)/u.exec(step)[1];
}

async function fixture(t, { releaseFiles = {} } = {}) {
  const directory = await mkdtemp(path.join(os.tmpdir(), "workflow-release-tooling-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const cwd = path.join(directory, "release");
  const runnerTemp = path.join(directory, "runner's temporary files");
  const githubEnv = path.join(directory, "github-env");
  const env = {
    PATH: process.env.PATH,
    HOME: directory,
    TMPDIR: directory,
    GIT_CONFIG_NOSYSTEM: "1",
    GIT_CONFIG_GLOBAL: os.devNull,
    GIT_CONFIG_COUNT: "0",
    GIT_CONFIG_PARAMETERS: undefined,
    GIT_AUTHOR_NAME: "Release test",
    GIT_AUTHOR_EMAIL: "release-test@example.test",
    GIT_COMMITTER_NAME: "Release test",
    GIT_COMMITTER_EMAIL: "release-test@example.test",
    RUNNER_TEMP: runnerTemp,
    GITHUB_ENV: githubEnv,
    REPOSITORY: "shirubasoft/aspire-extensions",
    WORKFLOW_REPOSITORY: "shirubasoft/aspire-extensions",
    NEXT_RELEASE_VERSION_FILE: "",
  };
  const run = async (command, extraEnv = {}) => execFileAsync("bash", ["-c", command], {
    cwd, env: { ...env, ...extraEnv }, timeout: 10000,
  });
  const git = async (...args) => (await execFileAsync("git", args, {
    cwd, env, timeout: 10000,
  })).stdout.trim();
  await mkdir(path.join(cwd, extensionPath), { recursive: true });
  await mkdir(runnerTemp);
  await git("init", "--initial-branch=main", "--object-format=sha1");
  await writeFile(path.join(cwd, extensionPath, "source.txt"), "baseline\n");
  await git("add", ".");
  await git("commit", "-m", "feat(resource-groups): baseline");
  await git("tag", "resource-groups-v1.2.0");
  await writeFile(path.join(cwd, extensionPath, "source.txt"), "fixed\n");
  for (const [filename, contents] of Object.entries(releaseFiles)) {
    await mkdir(path.dirname(path.join(cwd, filename)), { recursive: true });
    await writeFile(path.join(cwd, filename), contents);
  }
  await git("add", ".");
  await git("commit", "-m", "fix(resource-groups): repair the extension");
  env.HEAD_SHA = await git("rev-parse", "HEAD");
  for (const filename of Object.keys(releaseFiles)) {
    await rm(path.join(cwd, filename));
  }
  await cp(toolingDirectory, path.join(cwd, ".github/release"), {
    recursive: true, filter: (source) => path.basename(source) !== "node_modules",
  });
  await writeFile(path.join(cwd, releaseConfig),
    'import { createExtensionReleaseConfig } from "../../.github/release/create-extension-release-config.mjs";\n'
    + `export default createExtensionReleaseConfig(${JSON.stringify({
      extensionPath, packageId, tagPrefix: "resource-groups",
    })});\n`);
  await git("add", ".");
  await git("commit", "-m", "chore(ci): add release tooling");
  env.WORKFLOW_SHA = await git("rev-parse", "HEAD");
  await git("update-ref", "refs/remotes/origin/main", env.WORKFLOW_SHA);
  return { cwd, env, git, githubEnv, run, runnerTemp };
}

test("verifies the CI workflow path alongside the caller's run identity", async (t) => {
  const { env, run } = await fixture(t);
  const caller = {
    EVENT_NAME: "workflow_run", RUN_EVENT: "push", RUN_CONCLUSION: "success", RUN_HEAD_BRANCH: "main",
    RUN_HEAD_REPOSITORY: env.REPOSITORY, RUN_HEAD_SHA: env.HEAD_SHA, INPUT_HEAD_SHA: env.HEAD_SHA,
    RUN_ID: "123", INPUT_RUN_ID: "123", RUN_PATH: ".github/workflows/resource-groups-ci.yml",
    EXPECTED_RUN_PATH: ".github/workflows/resource-groups-ci.yml",
  };
  await run(stepCommand("Verify the caller's CI run"), caller);
  for (const override of [
    { RUN_PATH: "" }, { RUN_PATH: ".github/workflows/kafka-ci.yml" },
    { RUN_EVENT: "pull_request" }, { RUN_CONCLUSION: "failure" }, { RUN_HEAD_BRANCH: "other" },
    { RUN_HEAD_REPOSITORY: "someone/fork" }, { INPUT_HEAD_SHA: "other" }, { INPUT_RUN_ID: "456" },
  ]) {
    await assert.rejects(run(stepCommand("Verify the caller's CI run"), { ...caller, ...override }));
  }
  for (const extension of ["kafka", "cloudflare-tunnels", "multirepo", "resource-groups", "test-projects"]) {
    const callerWorkflow = await readFile(new URL(`../workflows/${extension}-publish.yml`, import.meta.url), "utf8");
    assert.ok(callerWorkflow.includes(`ci_workflow_path: .github/workflows/${extension}-ci.yml`));
  }
});

test("installs tooling with npm configuration outside the release checkout", async (t) => {
  const { run, runnerTemp, env } = await fixture(t, {
    releaseFiles: { ".npmrc": "registry=https://older-checkout.example.test\n" },
  });
  await run(stepCommand("Switch to the release commit after verifying it is on main"));
  await run(stepCommand("Check out release tooling from this workflow's commit"));
  const releaseTooling = path.join(runnerTemp, "release-tooling");
  const { stdout: realNpm } = await run("command -v npm");
  const bin = path.join(runnerTemp, "bin");
  await mkdir(bin);
  // Intercept ci to avoid reinstalling dependencies; use real npm's config loader in the step's cwd.
  await writeFile(path.join(bin, "npm"),
    '#!/bin/sh\n[ "$1" = ci ] || exit 1\npwd\nexec "$REAL_NPM" config get registry\n', { mode: 0o755 });
  const { stdout } = await run(stepCommand("Install locked release tooling"), {
    RELEASE_TOOLING_DIR: releaseTooling, REAL_NPM: realNpm.trim(),
    PATH: `${bin}${path.delimiter}${env.PATH}`,
  });
  assert.equal(stdout.trim(), `${releaseTooling}/.github/release\nhttps://registry.npmjs.org/`);
});

test("never executes an older checkout's config with the real pinned semantic-release CLI", async (t) => {
  const { cwd, env, git, run, runnerTemp } = await fixture(t, {
    releaseFiles: {
      "release.config.cjs": `
        require("node:fs").writeFileSync(process.env.CONFIG_PROBE_FILE, JSON.stringify({
          githubToken: process.env.GITHUB_TOKEN, nugetApiKey: process.env.NUGET_API_KEY,
        }));
        throw new Error("RELEASE_CHECKOUT_CONFIG_EXECUTED");
      `,
    },
  });
  await run(stepCommand("Switch to the release commit after verifying it is on main"));
  await run(stepCommand("Check out release tooling from this workflow's commit"));
  const releaseTooling = path.join(runnerTemp, "release-tooling");
  await assert.rejects(readFile(path.join(releaseTooling, "release.config.cjs")), { code: "ENOENT" });
  await symlink(path.join(toolingDirectory, "node_modules"),
    path.join(releaseTooling, ".github/release/node_modules"), "junction");
  const probeFile = path.join(runnerTemp, "config-probe.json");
  const error = await run(stepCommand("Version and publish"), {
    RELEASE_TOOLING_DIR: releaseTooling, RELEASE_CONFIG: releaseConfig,
    GITHUB_TOKEN: "fake-github-token", NUGET_API_KEY: "fake-nuget-key",
    CONFIG_PROBE_FILE: probeFile,
  }).then(() => undefined, (error) => error);
  const probe = await readFile(probeFile, "utf8").catch((error) => {
    if (error.code !== "ENOENT") throw error;
    return null;
  });
  t.diagnostic(`Older checkout config probe: ${probe ?? "not executed"}`);
  assert.equal(probe, null, "The release checkout config must never read publish credentials.");
  assert.match(error?.stderr ?? "", /Untrusted release checkout configuration: release\.config\.cjs/u);
  assert.equal(await git("rev-parse", "HEAD"), env.HEAD_SHA);
  assert.ok(await readFile(path.join(cwd, "release.config.cjs"), "utf8"));
});

test("runs newer workflow tooling with the release checkout at an older commit lacking it", async (t) => {
  const { cwd, env, git, githubEnv, run, runnerTemp } = await fixture(t);
  await run(stepCommand("Switch to the release commit after verifying it is on main"));
  await assert.rejects(readFile(path.join(cwd, ".github/release/remove-incomplete-release-tag.mjs")),
    { code: "ENOENT" });
  await run(stepCommand("Check out release tooling from this workflow's commit"));
  const releaseTooling = path.join(runnerTemp, "release-tooling");
  assert.equal(await readFile(githubEnv, "utf8"), `RELEASE_TOOLING_DIR=${releaseTooling}\n`);
  assert.equal(await git("rev-parse", "HEAD"), env.HEAD_SHA);
  assert.equal(await git("-C", releaseTooling, "rev-parse", "HEAD"), env.WORKFLOW_SHA);
  await symlink(path.join(toolingDirectory, "node_modules"),
    path.join(releaseTooling, ".github/release/node_modules"), "junction");

  // GitHub PR mode stops after real configuration/plugin loading, before any remote operations.
  const { stdout: cliOutput, stderr: cliDebug } = await run(stepCommand("Version and publish"), {
    RELEASE_TOOLING_DIR: releaseTooling, RELEASE_CONFIG: releaseConfig,
    GITHUB_TOKEN: "fake-github-token", NUGET_API_KEY: "fake-nuget-key",
    GITHUB_ACTIONS: "true", GITHUB_EVENT_NAME: "pull_request", GITHUB_REF: "refs/pull/1/merge",
    GITHUB_HEAD_REF: "test-branch", DEBUG: "semantic-release:config",
    GIT_CONFIG_COUNT: "1", GIT_CONFIG_KEY_0: "core.hooksPath", GIT_CONFIG_VALUE_0: os.devNull,
  });
  assert.match(cliOutput, /Running semantic-release version 25\.0\.9/u);
  assert.match(cliOutput, /Loaded plugin "analyzeCommits"/u);
  assert.match(cliOutput, /triggered by a pull request/u);
  assert.match(cliDebug, /repositoryUrl: 'https:\/\/github\.com\/shirubasoft\/aspire-extensions\.git'/u);
  assert.ok(cliDebug.includes(releaseTooling));

  const { stdout } = await run(stepCommand("Remove the tag left by an earlier unpublished attempt of this release"), {
    RELEASE_TOOLING_DIR: releaseTooling, GITHUB_REPOSITORY: env.REPOSITORY,
    GITHUB_TOKEN: "test-token-with-no-credentials", PACKAGE_ID: packageId, TAG_PREFIX: "resource-groups",
  });
  assert.match(stdout, /CI artifact does not select a release version/u);

  const { default: config } = await import(pathToFileURL(path.join(releaseTooling, releaseConfig)));
  const [analyzerPath, analyzerConfig] = config.plugins[0];
  assert.ok(analyzerPath.startsWith(releaseTooling));
  const { analyzeCommits } = await import(pathToFileURL(analyzerPath));
  assert.equal(await analyzeCommits(analyzerConfig, {
    cwd, commits: [{ hash: env.HEAD_SHA, message: "fix(resource-groups): repair the extension" }],
    logger: { log() {} },
  }), "patch");

  const artifactPath = path.join(cwd, "artifacts", packageId);
  await mkdir(artifactPath, { recursive: true });
  await writeFile(path.join(artifactPath, "release-version.txt"), "1.2.1\n");
  await writeFile(path.join(artifactPath, `${packageId}.1.2.1.nupkg`), "package");
  await writeFile(path.join(artifactPath, `${packageId}.1.2.1.snupkg`), "symbols");
  const commands = config.plugins[1][1];
  await run(commands.verifyReleaseCmd.replaceAll("${nextRelease.version}", "1.2.1"));
  await assert.rejects(run(commands.verifyReleaseCmd.replaceAll("${nextRelease.version}", "1.2.2")),
    /CI packaged version 1\.2\.1/u);
  const bin = path.join(runnerTemp, "bin");
  const argsFile = path.join(runnerTemp, "dotnet-args");
  await mkdir(bin);
  await writeFile(path.join(bin, "dotnet"), '#!/bin/sh\nprintf "%s\\n" "$@" > "$DOTNET_ARGS_FILE"\n',
    { mode: 0o755 });
  await run(commands.publishCmd.replaceAll("${nextRelease.version}", "1.2.1"), {
    PATH: `${bin}${path.delimiter}${env.PATH}`, DOTNET_ARGS_FILE: argsFile,
    NUGET_API_KEY: "test-key-with-no-credentials",
  });
  assert.match(await readFile(argsFile, "utf8"),
    new RegExp(`artifacts/${packageId.replaceAll(".", "\\.")}/${packageId.replaceAll(".", "\\.")}\\.1\\.2\\.1\\.nupkg`, "u"));
  assert.equal(await git("rev-parse", "HEAD"), env.HEAD_SHA);
});

test("rejects tooling outside main, invalid workflow SHAs, and other workflow repositories", async (t) => {
  const { env, git, githubEnv, run } = await fixture(t);
  await git("checkout", "--detach", env.HEAD_SHA);
  await git("commit", "--allow-empty", "-m", "chore: unmerged tooling");
  const unmergedSha = await git("rev-parse", "HEAD");
  for (const extraEnv of [
    { WORKFLOW_SHA: "" },
    { WORKFLOW_SHA: "main" },
    { WORKFLOW_SHA: unmergedSha },
    { WORKFLOW_REPOSITORY: "someone/fork" },
  ]) {
    await assert.rejects(run(stepCommand("Check out release tooling from this workflow's commit"), extraEnv));
    await assert.rejects(readFile(githubEnv), { code: "ENOENT" });
  }
  await assert.rejects(run(stepCommand("Switch to the release commit after verifying it is on main"),
    { HEAD_SHA: unmergedSha }), /not on main/u);
});
