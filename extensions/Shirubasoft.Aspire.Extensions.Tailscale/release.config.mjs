import { createExtensionReleaseConfig } from "../../.github/release/create-extension-release-config.mjs";

export default createExtensionReleaseConfig({
  extensionPath: "extensions/Shirubasoft.Aspire.Extensions.Tailscale",
  packageId: "Shirubasoft.Aspire.Tailscale",
  tagPrefix: "tailscale",
});
