import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);
const script = fileURLToPath(new URL("./bootstrap-version-baseline.sh", import.meta.url));
const extensionPath = "extensions/Shirubasoft.Aspire.Extensions.Sample";

async function repository(t) {
  const cwd = await mkdtemp(path.join(os.tmpdir(), "bootstrap-version-baseline-"));
  t.after(() => rm(cwd, { recursive: true, force: true }));
  const env = {
    ...process.env,
    GIT_CONFIG_NOSYSTEM: "1",
    GIT_CONFIG_GLOBAL: os.devNull,
    GIT_AUTHOR_NAME: "Release test",
    GIT_AUTHOR_EMAIL: "release-test@example.test",
    GIT_COMMITTER_NAME: "Release test",
    GIT_COMMITTER_EMAIL: "release-test@example.test",
  };
  const git = async (...args) => (await execFileAsync("git", args, { cwd, env })).stdout.trim();
  const commit = async (file, message) => {
    await mkdir(path.dirname(path.join(cwd, file)), { recursive: true });
    await writeFile(path.join(cwd, file), `${message}\n`);
    await git("add", ".");
    await git("commit", "-m", message);
    return git("rev-parse", "HEAD");
  };
  const bootstrap = async () => (await execFileAsync(
    "bash", [script, "sample", "2.1.0", extensionPath], { cwd, env })).stdout;
  await git("init", "--initial-branch=main", "--object-format=sha1");
  return { bootstrap, commit, cwd, env, git };
}

test("tags the commit before the extension folder first appeared", async (t) => {
  const { bootstrap, commit, git } = await repository(t);
  await commit("Directory.Packages.props", "feat!: rebuild the repository");
  const beforeExtension = await commit("README.md", "docs: describe the repository");
  await commit(`${extensionPath}/source.txt`, "feat(sample): add the extension");
  await commit(`${extensionPath}/source.txt`, "fix(sample): repair the extension");

  assert.match(await bootstrap(), /Created sample-v2\.1\.0/u);
  assert.equal(await git("rev-list", "-n", "1", "sample-v2.1.0"), beforeExtension);
});

test("excludes mainline commits that land before the extension branch merges", async (t) => {
  const { bootstrap, commit, git } = await repository(t);
  await commit("README.md", "docs: describe the repository");
  await git("checkout", "-b", "feature");
  await commit(`${extensionPath}/source.txt`, "feat(sample): add the extension");
  await git("checkout", "main");
  const mainlineBreak = await commit("Directory.Packages.props", "feat!: break every extension");
  await git("merge", "--no-ff", "feature", "-m", "Merge the extension");

  await bootstrap();
  assert.equal(await git("rev-list", "-n", "1", "sample-v2.1.0"), mainlineBreak);
});

test("tags the root commit when the root commit created the extension", async (t) => {
  const { bootstrap, commit, git } = await repository(t);
  const root = await commit(`${extensionPath}/source.txt`, "feat: create the repository");
  await commit(`${extensionPath}/source.txt`, "fix(sample): repair the extension");

  await bootstrap();
  assert.equal(await git("rev-list", "-n", "1", "sample-v2.1.0"), root);
});

test("keeps an existing release tag", async (t) => {
  const { bootstrap, commit, git } = await repository(t);
  await commit(`${extensionPath}/source.txt`, "feat(sample): add the extension");
  const released = await commit(`${extensionPath}/source.txt`, "fix(sample): repair the extension");
  await git("tag", "sample-v2.1.1");
  await commit(`${extensionPath}/source.txt`, "fix(sample): repair it again");

  assert.equal(await bootstrap(), "");
  assert.equal(await git("tag", "--list", "sample-v2.1.0"), "");
  assert.equal(await git("rev-list", "-n", "1", "sample-v2.1.1"), released);
});

test("fails in a shallow clone instead of tagging its boundary", async (t) => {
  const { commit, cwd, env } = await repository(t);
  await commit(`${extensionPath}/source.txt`, "feat(sample): add the extension");
  await commit(`${extensionPath}/source.txt`, "fix(sample): repair the extension");
  const clone = await mkdtemp(path.join(os.tmpdir(), "bootstrap-version-baseline-shallow-"));
  t.after(() => rm(clone, { recursive: true, force: true }));
  await execFileAsync("git", ["clone", "--quiet", "--depth", "1", `file://${cwd}`, clone], { env });

  await assert.rejects(
    execFileAsync("bash", [script, "sample", "2.1.0", extensionPath], { cwd: clone, env }),
    /needs full history/u);
});

test("fails when no commit touches the extension", async (t) => {
  const { bootstrap, commit } = await repository(t);
  await commit("README.md", "docs: describe the repository");

  await assert.rejects(bootstrap(), /No commit reachable from HEAD touches/u);
});
