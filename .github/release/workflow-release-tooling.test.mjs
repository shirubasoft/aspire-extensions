import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { cp, mkdir, mkdtemp, readFile, readdir, realpath, rm, symlink, writeFile } from "node:fs/promises";
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
    GIT_CONFIG_COUNT: "1",
    GIT_CONFIG_KEY_0: "core.hooksPath",
    GIT_CONFIG_VALUE_0: os.devNull,
    GIT_CONFIG_PARAMETERS: undefined,
    GIT_AUTHOR_NAME: "Release test",
    GIT_AUTHOR_EMAIL: "release-test@example.test",
    GIT_COMMITTER_NAME: "Release test",
    GIT_COMMITTER_EMAIL: "release-test@example.test",
    RUNNER_TEMP: runnerTemp,
    GITHUB_WORKSPACE: cwd,
    RELEASE_ARTIFACTS_DIR: path.join(runnerTemp, "release-artifacts", packageId),
    DOTNET_CLI_HOME: path.join(runnerTemp, "release-dotnet-home"),
    NUGET_PACKAGES: path.join(runnerTemp, "release-nuget-packages"),
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE: "1",
    DOTNET_NOLOGO: "true",
    GITHUB_ENV: githubEnv,
    REPOSITORY: "shirubasoft/aspire-extensions",
    WORKFLOW_REPOSITORY: "shirubasoft/aspire-extensions",
    NEXT_RELEASE_VERSION_FILE: "",
    PACKAGE_ID: packageId,
  };
  const run = async (command, extraEnv = {}, workingDirectory = cwd) => execFileAsync("bash", ["-c", command], {
    cwd: workingDirectory, env: { ...env, ...extraEnv }, timeout: 30000,
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
    recursive: true, filter: (source) => !["node_modules", "bin", "obj"].includes(path.basename(source)),
  });
  await cp(new URL("../../global.json", import.meta.url), path.join(cwd, "global.json"));
  await writeFile(path.join(cwd, releaseConfig),
    'import { createExtensionReleaseConfig } from "../../.github/release/create-extension-release-config.mjs";\n'
    + `export default createExtensionReleaseConfig(${JSON.stringify({
      extensionPath, packageId, tagPrefix: "resource-groups",
    })});\n`);
  await git("add", ".");
  await git("commit", "-m", "chore(ci): add release tooling");
  env.WORKFLOW_SHA = await git("rev-parse", "HEAD");
  await git("update-ref", "refs/remotes/origin/main", env.WORKFLOW_SHA);
  const prepare = async () => {
    await run(stepCommand("Check out release tooling from this workflow's commit"));
    env.RELEASE_TOOLING_DIR = path.join(runnerTemp, "release-tooling");
    await run(stepCommand("Create the empty publish workspace at the release commit"));
    env.RELEASE_PUBLISH_DIR = path.join(directory, "release-publish");
    await symlink(path.join(toolingDirectory, "node_modules"),
      path.join(env.RELEASE_TOOLING_DIR, ".github/release/node_modules"), "junction");
  };
  const publishRun = (command, extraEnv = {}) => run(command, extraEnv, env.RELEASE_PUBLISH_DIR);
  return { cwd, env, git, githubEnv, run, runnerTemp, prepare, publishRun };
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
  const { cwd, env, git, run, prepare, publishRun, runnerTemp } = await fixture(t, {
    releaseFiles: {
      "release.config.cjs": `
        require("node:fs").writeFileSync(process.env.CONFIG_PROBE_FILE, JSON.stringify({
          githubToken: process.env.GITHUB_TOKEN, nugetApiKey: process.env.NUGET_API_KEY,
        }));
        throw new Error("RELEASE_CHECKOUT_CONFIG_EXECUTED");
      `,
    },
  });
  await prepare();
  const releaseTooling = env.RELEASE_TOOLING_DIR;
  await git("checkout", "--detach", env.HEAD_SHA);
  await assert.rejects(readFile(path.join(releaseTooling, "release.config.cjs")), { code: "ENOENT" });
  const probeFile = path.join(runnerTemp, "config-probe.json");
  const credentials = {
    RELEASE_TOOLING_DIR: releaseTooling, RELEASE_CONFIG: releaseConfig,
    GITHUB_TOKEN: "fake-github-token", NUGET_API_KEY: "fake-nuget-key", CONFIG_PROBE_FILE: probeFile,
  };
  const unguarded = stepCommand("Version and publish").replace(
    'node "$RELEASE_TOOLING_DIR/.github/release/assert-release-workspace-safe.mjs"\n', "",
  );
  await assert.rejects(run(unguarded, credentials), /RELEASE_CHECKOUT_CONFIG_EXECUTED/u);
  assert.deepEqual(JSON.parse(await readFile(probeFile, "utf8")), {
    githubToken: credentials.GITHUB_TOKEN, nugetApiKey: credentials.NUGET_API_KEY,
  });
  t.diagnostic(`Before config isolation, real CLI probe: ${await readFile(probeFile, "utf8")}`);
  await rm(probeFile);
  const { stdout } = await publishRun(stepCommand("Version and publish"), {
    RELEASE_TOOLING_DIR: releaseTooling, RELEASE_CONFIG: releaseConfig,
    GITHUB_TOKEN: "fake-github-token", NUGET_API_KEY: "fake-nuget-key",
    CONFIG_PROBE_FILE: probeFile,
    GITHUB_ACTIONS: "true", GITHUB_EVENT_NAME: "pull_request", GITHUB_REF: "refs/pull/1/merge",
    GITHUB_HEAD_REF: "test-branch",
  });
  const probe = await readFile(probeFile, "utf8").catch((error) => {
    if (error.code !== "ENOENT") throw error;
    return null;
  });
  t.diagnostic(`Older checkout config probe: ${probe ?? "not executed"}`);
  assert.equal(probe, null, "The release checkout config must never read publish credentials.");
  assert.match(stdout, /Running semantic-release version 25\.0\.9/u);
  assert.match(stdout, /triggered by a pull request/u);
  assert.deepEqual(await readdir(env.RELEASE_PUBLISH_DIR), [".git"]);
  assert.equal(await git("rev-parse", "HEAD"), env.HEAD_SHA);
  assert.ok(await readFile(path.join(cwd, "release.config.cjs"), "utf8"));
});

test("runs newer workflow tooling with the release checkout at an older commit lacking it", async (t) => {
  const { cwd, env, git, githubEnv, run, runnerTemp, prepare, publishRun } = await fixture(t);
  await prepare();
  const releaseTooling = env.RELEASE_TOOLING_DIR;
  assert.equal(await readFile(githubEnv, "utf8"),
    `RELEASE_TOOLING_DIR=${releaseTooling}\nRELEASE_PUBLISH_DIR=${env.RELEASE_PUBLISH_DIR}\n`
    + `RELEASE_ARTIFACTS_DIR=${env.RELEASE_ARTIFACTS_DIR}\nDOTNET_CLI_HOME=${env.DOTNET_CLI_HOME}\n`
    + `NUGET_PACKAGES=${env.NUGET_PACKAGES}\n`);
  assert.equal(await git("-C", env.RELEASE_PUBLISH_DIR, "rev-parse", "HEAD"), env.HEAD_SHA);
  assert.equal(await git("-C", releaseTooling, "rev-parse", "HEAD"), env.WORKFLOW_SHA);
  await assert.rejects(readFile(path.join(env.RELEASE_PUBLISH_DIR, ".github/release/remove-incomplete-release-tag.mjs")),
    { code: "ENOENT" });

  // GitHub PR mode stops after real configuration/plugin loading, before any remote operations.
  const { stdout: cliOutput, stderr: cliDebug } = await publishRun(stepCommand("Version and publish"), {
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

  const { stdout } = await publishRun(stepCommand("Remove the tag left by an earlier unpublished attempt of this release"), {
    RELEASE_TOOLING_DIR: releaseTooling, GITHUB_REPOSITORY: env.REPOSITORY,
    GITHUB_TOKEN: "test-token-with-no-credentials", PACKAGE_ID: packageId, TAG_PREFIX: "resource-groups",
  });
  assert.match(stdout, /CI artifact does not select a release version/u);

  process.env.RELEASE_ARTIFACTS_DIR = env.RELEASE_ARTIFACTS_DIR;
  const { default: config } = await import(pathToFileURL(path.join(releaseTooling, releaseConfig)));
  t.after(() => { delete process.env.RELEASE_ARTIFACTS_DIR; });
  assert.equal(config.plugins[2][1].assets[0].path, path.join(env.RELEASE_ARTIFACTS_DIR, "*.nupkg"));
  const [analyzerPath, analyzerConfig] = config.plugins[0];
  assert.ok(analyzerPath.startsWith(releaseTooling));
  const { analyzeCommits } = await import(pathToFileURL(analyzerPath));
  assert.equal(await analyzeCommits(analyzerConfig, {
    cwd: env.RELEASE_PUBLISH_DIR, commits: [{ hash: env.HEAD_SHA, message: "fix(resource-groups): repair the extension" }],
    logger: { log() {} },
  }), "patch");

  const artifactPath = env.RELEASE_ARTIFACTS_DIR;
  await mkdir(artifactPath, { recursive: true });
  await writeFile(path.join(artifactPath, "release-version.txt"), "1.2.1\n");
  await writeFile(path.join(artifactPath, `${packageId}.1.2.1.nupkg`), "package");
  await writeFile(path.join(artifactPath, `${packageId}.1.2.1.snupkg`), "symbols");
  const commands = config.plugins[1][1];
  await publishRun(commands.verifyReleaseCmd.replaceAll("${nextRelease.version}", "1.2.1"));
  await assert.rejects(publishRun(commands.verifyReleaseCmd.replaceAll("${nextRelease.version}", "1.2.2")),
    /CI packaged version 1\.2\.1/u);
  const bin = path.join(runnerTemp, "bin");
  const argsFile = path.join(runnerTemp, "dotnet-args");
  await mkdir(bin);
  await writeFile(path.join(bin, "dotnet"), '#!/bin/sh\nprintf "%s\\n" "$@" > "$DOTNET_ARGS_FILE"\n',
    { mode: 0o755 });
  await publishRun(commands.publishCmd.replaceAll("${nextRelease.version}", "1.2.1"), {
    PATH: `${bin}${path.delimiter}${env.PATH}`, DOTNET_ARGS_FILE: argsFile,
    NUGET_API_KEY: "test-key-with-no-credentials",
  });
  const args = (await readFile(argsFile, "utf8")).trim().split("\n");
  assert.equal(args[2], path.join(artifactPath, `${packageId}.1.2.1.nupkg`));
  assert.equal(args[args.indexOf("--configfile") + 1], path.join(releaseTooling, ".github/release/NuGet.Config"));
  assert.equal(args[args.indexOf("--source") + 1], "https://api.nuget.org/v3/index.json");
  assert.deepEqual(await readdir(env.RELEASE_PUBLISH_DIR), [".git"]);
  assert.equal(await git("-C", env.RELEASE_PUBLISH_DIR, "rev-parse", "HEAD"), env.HEAD_SHA);
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
  await assert.rejects(run(stepCommand("Create the empty publish workspace at the release commit"),
    { HEAD_SHA: unmergedSha, RELEASE_TOOLING_DIR: path.resolve(toolingDirectory, "../..") }), /not on main/u);
});


test("never executes a checkout-local SDK with publish credentials", async (t) => {
  const build = await mkdtemp(path.join(os.tmpdir(), "trusted-sdk-probe-"));
  t.after(() => rm(build, { recursive: true, force: true }));
  await cp(new URL("./fixtures/local-sdk-probe/", import.meta.url), build, {
    recursive: true, filter: (source) => !["bin", "obj"].includes(path.basename(source)),
  });
  await execFileAsync("dotnet", ["build", "Probe.csproj", "-o", "output", "--nologo"], {
    cwd: build, timeout: 60000,
  });
  const { cwd, env, git, run, runnerTemp, prepare, publishRun } = await fixture(t, {
    releaseFiles: {
      "global.json": JSON.stringify({ sdk: { version: "10.0.301", paths: [".sdk", "$host$"] } }),
      ".sdk/sdk/10.0.301/dotnet.dll": await readFile(path.join(build, "output/Probe.dll")),
      ".sdk/sdk/10.0.301/dotnet.runtimeconfig.json": await readFile(path.join(build, "output/Probe.runtimeconfig.json")),
    },
  });
  await prepare();
  await git("checkout", "--detach", env.HEAD_SHA);
  const { stdout: dotnetPath } = await run("command -v dotnet");
  await symlink(path.join(path.dirname(await realpath(dotnetPath.trim())), "shared"), path.join(cwd, ".sdk/shared"));
  const probeFile = path.join(runnerTemp, "sdk-probe.json");
  const credentials = { GITHUB_TOKEN: "fake-github-token", NUGET_API_KEY: "fake-nuget-key", SDK_PROBE_FILE: probeFile };
  const command = 'bash "$RELEASE_TOOLING_DIR/.github/release/publish-release-assets.sh" 1.2.1 "$PACKAGE_ID" "$PACKAGE_ID:symbols"';
  // Reproduce the pre-isolation cwd with the same real host, trusted script and fake credentials.
  await run(command, { ...credentials, PACKAGE_ID: packageId });
  t.diagnostic(`Before empty workspace, checkout SDK probe: ${await readFile(probeFile, "utf8")}`);
  assert.deepEqual(JSON.parse(await readFile(probeFile, "utf8")), {
    githubToken: credentials.GITHUB_TOKEN, nugetApiKey: credentials.NUGET_API_KEY,
  });
  await rm(probeFile);
  const { assertReleaseWorkspaceSafe } = await import("./assert-release-workspace-safe.mjs");
  await assertReleaseWorkspaceSafe(env.RELEASE_PUBLISH_DIR);
  // The real trusted SDK fails on the deliberately absent package before any network operation.
  const after = await publishRun(command, { ...credentials, PACKAGE_ID: packageId })
    .then(() => null, (error) => error);
  assert.match(`${after?.stdout}\n${after?.stderr}`, /does not exist|Could not find/u);
  await assert.rejects(readFile(probeFile), { code: "ENOENT" });
  t.diagnostic("After empty workspace, checkout SDK probe: not executed; trusted SDK rejected the absent package.");
  assert.deepEqual(await readdir(env.RELEASE_PUBLISH_DIR), [".git"]);
  assert.ok(workflow.includes("global-json-file: ${{ env.RELEASE_TOOLING_DIR }}/global.json"));
});

test("isolates NuGet configuration from the checkout and user configuration", async (t) => {
  const { cwd, env, run, prepare, publishRun } = await fixture(t, {
    releaseFiles: { "NuGet.Config": "CHECKOUT_NUGET_CONFIG_PROBE" },
  });
  await prepare();
  // A checked-out NuGet config was accepted by the old guard and read by the real CLI.
  await writeFile(path.join(cwd, "NuGet.Config"), "CHECKOUT_NUGET_CONFIG_PROBE");
  const before = await run("dotnet nuget list source").then(() => null, (error) => error);
  assert.match(`${before?.stdout}\n${before?.stderr}`, /NuGet.Config.*not valid XML/su);
  t.diagnostic("Before isolation, real NuGet CLI reads checkout NuGet.Config and rejects its probe XML.");
  // This invalid user config must also be ignored by the actual publish command's --configfile.
  await mkdir(path.join(env.HOME, ".nuget/NuGet"), { recursive: true });
  await writeFile(path.join(env.HOME, ".nuget/NuGet/NuGet.Config"), "USER_NUGET_CONFIG_PROBE");
  const command = 'bash "$RELEASE_TOOLING_DIR/.github/release/publish-release-assets.sh" 1.2.1 "$PACKAGE_ID" "$PACKAGE_ID:symbols"';
  const after = await publishRun(command, { PACKAGE_ID: packageId, NUGET_API_KEY: "fake-nuget-key" })
    .then(() => null, (error) => error);
  assert.match(`${after?.stdout}\n${after?.stderr}`, /does not exist|Could not find/u);
  assert.doesNotMatch(`${after?.stdout}\n${after?.stderr}`, /not valid XML/u);
  t.diagnostic("After isolation, real publish script ignores checkout and user NuGet probes using trusted --configfile.");
});

test("real semantic-release dry run and exec publish work with only Git metadata and external artifacts", async (t) => {
  const { env, git, runnerTemp, prepare, publishRun } = await fixture(t, {
    releaseFiles: { "untrusted-file.txt": "must never be checked out into the publish workspace" },
  });
  await prepare();
  const remote = path.join(runnerTemp, "remote.git");
  await git("clone", "--bare", env.GITHUB_WORKSPACE, remote);
  await git("-C", remote, "update-ref", "refs/heads/main", env.HEAD_SHA);
  const repositoryUrl = pathToFileURL(remote).href;
  await git("-C", env.RELEASE_PUBLISH_DIR, "remote", "set-url", "origin", repositoryUrl);
  await mkdir(env.RELEASE_ARTIFACTS_DIR, { recursive: true });
  await writeFile(path.join(env.RELEASE_ARTIFACTS_DIR, "release-version.txt"), "1.2.1\n");
  await writeFile(path.join(env.RELEASE_ARTIFACTS_DIR, `${packageId}.1.2.1.nupkg`), "package");
  await writeFile(path.join(env.RELEASE_ARTIFACTS_DIR, `${packageId}.1.2.1.snupkg`), "symbols");
  const configFile = path.join(env.RELEASE_TOOLING_DIR, "dry-run.config.mjs");
  // Keep the real analyzer and exec plugin; omit only the GitHub HTTP publisher for this local remote.
  await writeFile(configFile, `import config from ${JSON.stringify(pathToFileURL(path.join(env.RELEASE_TOOLING_DIR, releaseConfig)).href)};
    export default { ...config, plugins: config.plugins.slice(0, 2) };\n`);
  const cli = stepCommand("Version and publish")
    .replace('"https://github.com/$REPOSITORY.git"', '"$TEST_REPOSITORY_URL"') + " --dry-run --no-ci";
  const cliEnv = {
    RELEASE_CONFIG: "dry-run.config.mjs", TEST_REPOSITORY_URL: repositoryUrl,
    GITHUB_TOKEN: "fake-github-token", NUGET_API_KEY: "fake-nuget-key",
    DEBUG: "semantic-release:git",
  };
  const { stdout, stderr } = await publishRun(cli, cliEnv);
  assert.match(stdout, /Allowed to push to the Git repository/u);
  assert.match(stdout, /next release version is 1\.2\.1/u);
  assert.match(stdout, /Completed step "verifyRelease"/u);
  assert.match(stdout, /Completed step "generateNotes"/u);
  assert.match(`${stdout}\n${stderr}`, /Skip resource-groups-v1\.2\.1 tag creation/u);
  assert.equal(await git("-C", remote, "tag", "--list", "resource-groups-v1.2.1"), "");
  assert.equal(await git("-C", env.RELEASE_PUBLISH_DIR, "rev-parse", "HEAD"), env.HEAD_SHA);
  assert.equal(await git("-C", env.RELEASE_PUBLISH_DIR, "rev-parse", "--is-shallow-repository"), "false");
  assert.match(await git("-C", env.RELEASE_PUBLISH_DIR, "status", "--porcelain"), /D.*untrusted-file/u);

  const gitHelpers = await import(pathToFileURL(path.join(toolingDirectory, "node_modules/semantic-release/lib/git.js")));
  const options = { cwd: env.RELEASE_PUBLISH_DIR, env: { ...env, ...cliEnv } };
  await gitHelpers.verifyAuth(repositoryUrl, "main", options);
  assert.equal(await gitHelpers.isBranchUpToDate(repositoryUrl, "main", options), true);
  await gitHelpers.tag("fixture-only-v1.2.1", env.HEAD_SHA, options);
  await gitHelpers.addNote({ channels: [null] }, "fixture-only-v1.2.1", options);
  assert.deepEqual((await gitHelpers.getTagsNotes(options)).get("fixture-only-v1.2.1"), { channels: [null] });
  await gitHelpers.push(repositoryUrl, options);
  await gitHelpers.pushNotes(repositoryUrl, "fixture-only-v1.2.1", options);
  assert.equal(await git("-C", remote, "rev-parse", "fixture-only-v1.2.1"), env.HEAD_SHA);

  const bin = path.join(runnerTemp, "bin");
  const argsFile = path.join(runnerTemp, "dotnet-args");
  await mkdir(bin);
  await writeFile(path.join(bin, "dotnet"), '#!/bin/sh\npwd > "$DOTNET_ARGS_FILE"\nprintf "%s\\n" "$@" >> "$DOTNET_ARGS_FILE"\n', { mode: 0o755 });
  process.env.RELEASE_ARTIFACTS_DIR = env.RELEASE_ARTIFACTS_DIR;
  t.after(() => { delete process.env.RELEASE_ARTIFACTS_DIR; });
  const { default: config } = await import(pathToFileURL(configFile));
  const execPlugin = await import(pathToFileURL(config.plugins[1][0]));
  await execPlugin.publish(config.plugins[1][1], {
    cwd: env.RELEASE_PUBLISH_DIR, env: {
      ...env, NUGET_API_KEY: "fake-nuget-key", GITHUB_TOKEN: "fake-github-token",
      DOTNET_ARGS_FILE: argsFile, PATH: `${bin}${path.delimiter}${env.PATH}`,
    },
    nextRelease: { version: "1.2.1" }, logger: { log() {} }, stdout: process.stdout, stderr: process.stderr,
  });
  const args = (await readFile(argsFile, "utf8")).trim().split("\n");
  assert.equal(args[0], env.RELEASE_PUBLISH_DIR);
  assert.equal(args[3], path.join(env.RELEASE_ARTIFACTS_DIR, `${packageId}.1.2.1.nupkg`));
  assert.equal(args[args.indexOf("--configfile") + 1], path.join(env.RELEASE_TOOLING_DIR, ".github/release/NuGet.Config"));
  assert.ok(config.plugins.length === 2);
  assert.deepEqual(await readdir(env.RELEASE_PUBLISH_DIR), [".git"]);
  // Advancing the isolated fixture remote must refuse an outdated release, preserving head_sha.
  await git("-C", remote, "update-ref", "refs/heads/main", env.WORKFLOW_SHA);
  assert.equal(await gitHelpers.isBranchUpToDate(repositoryUrl, "main", options), false);
  const stale = await publishRun(cli, { ...cliEnv, GITHUB_TOKEN: undefined });
  assert.match(stale.stdout, /local branch main is behind the remote/u);
  assert.equal(await git("-C", env.RELEASE_PUBLISH_DIR, "rev-parse", "HEAD"), env.HEAD_SHA);
  t.diagnostic("Real CLI: rev-parse, log, tag discovery, notes fetch, verifyAuth push --dry-run, version 1.2.1 and notes generation passed with only .git.");
  t.diagnostic("Real Git helpers: local fixture tag/notes and pushes passed; stale main refused. Real exec publisher ran mocked dotnet in the empty workspace.");
});

test("CI smoke check uses production's workspace parent chain and fails closed on ancestor config", async (t) => {
  const { cwd, env, run } = await fixture(t);
  const ci = await readFile(new URL("../workflows/_extension-ci.yml", import.meta.url), "utf8");
  assert.match(ci, /Smoke test the publish workspace on this runner\n\s+run: bash \.github\/release\/smoke-test-publish-workspace\.sh/u);
  assert.match(ci.split("  release-version:")[0], /fetch-depth: 0/u);
  await symlink(path.join(toolingDirectory, "node_modules"), path.join(cwd, ".github/release/node_modules"), "junction");
  const smoke = "bash .github/release/smoke-test-publish-workspace.sh";
  const options = { GITHUB_REPOSITORY: env.REPOSITORY };
  const publishDirectory = path.join(path.dirname(cwd), "release-publish");
  const { stdout } = await run(smoke, options);
  assert.ok(stdout.includes(`Publish workspace guard passed in ${publishDirectory}.`));
  await assert.rejects(readdir(publishDirectory), { code: "ENOENT" });
  const ancestorConfig = path.join(path.dirname(cwd), "package.json");
  await writeFile(ancestorConfig, "{}");
  await assert.rejects(run(smoke, options), /Untrusted ancestor configuration/u);
  await assert.rejects(readdir(publishDirectory), { code: "ENOENT" });
});
