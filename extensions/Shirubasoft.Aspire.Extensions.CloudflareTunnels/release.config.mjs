import { createExtensionReleaseConfig } from "../../eng/release/create-extension-release-config.mjs";

export default createExtensionReleaseConfig({
  extensionPath: "extensions/Shirubasoft.Aspire.Extensions.CloudflareTunnels",
  packageId: "Shirubasoft.Aspire.CloudflareTunnels",
  tagPrefix: "cloudflare-tunnels",
});
