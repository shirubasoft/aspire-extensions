import { createExtensionReleaseConfig } from "../../.github/release/create-extension-release-config.mjs";

export default createExtensionReleaseConfig({
  extensionPath: "extensions/Shirubasoft.Aspire.Extensions.Multirepo",
  packageId: "Shirubasoft.Aspire.Extensions.Multirepo",
  packages: [
    { id: "Shirubasoft.Aspire.Extensions.Multirepo", symbols: true },
    { id: "Shirubasoft.Aspire.Extensions.Multirepo.Testing", symbols: true },
    { id: "Shirubasoft.Aspire.Extensions.Multirepo.Tool", symbols: true },
    { id: "Shirubasoft.Aspire.Extensions.Multirepo.Templates", symbols: false },
  ],
  tagPrefix: "multirepo",
});
