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

async function fixture(t) {
  const directory = await mkdtemp(path.join(os.tmpdir(), "workflow-release-tooling-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const cwd = path.join(directory, "release");
  const runnerTemp = path.join(directory, "runner's temporary files");
  const githubEnv = path.join(directory, "github-env");
  const env = {
    ...process.env,
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
  await git("commit", "-am", "fix(resource-groups): repair the extension");
  env.HEAD_SHA = await git("rev-parse", "HEAD");
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
