import { createExtensionReleaseConfig } from "../../eng/release/create-extension-release-config.mjs";

export default createExtensionReleaseConfig({
  extensionPath: "extensions/Shirubasoft.Aspire.Extensions.Tailscale",
  packageId: "Shirubasoft.Aspire.Tailscale",
  tagPrefix: "tailscale",
});
