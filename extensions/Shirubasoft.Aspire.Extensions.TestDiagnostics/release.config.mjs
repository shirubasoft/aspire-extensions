import { createExtensionReleaseConfig } from "../../.github/release/create-extension-release-config.mjs";

export default createExtensionReleaseConfig({
  extensionPath: "extensions/Shirubasoft.Aspire.Extensions.TestDiagnostics",
  packageId: "Shirubasoft.Aspire.Extensions.TestDiagnostics",
  tagPrefix: "test-diagnostics",
});
