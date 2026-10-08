# Pinned Tailscale state fixtures

`tailscaled.state.v1.102.5.json` is sanitized captured state. Its private keys are zero values, and its identities and tailnet names are synthetic. `generate.go` verifies that its decoded prefs and profile metadata round-trip byte-for-byte through the pinned `ipn.PrefsFromBytes`, `Prefs.ToBytes`, and `ipn.LoginProfile` APIs.

`tailscaled.multi-profile.state.v1.102.5.json` is generated offline from that fixture. It exercises optional prefs, nested Drive shares and user groups, multiple profiles, Serve state, and the profile's route-info key. The generator uses upstream prefs and metadata serialization, then writes the store through `store.NewFileStore`. The shell scenarios must read the current profile's tags regardless of the other profile's tags.

Generate and compare in an isolated module, from the repository root:

```bash
fixture_dir="$(realpath extensions/Shirubasoft.Aspire.Extensions.Tailscale/tests/Shirubasoft.Aspire.Extensions.Tailscale.Tests/Fixtures)"
generator_dir="$(mktemp -d)"
cd "$generator_dir"
go mod init tailscale-state-fixtures
go get tailscale.com@v1.102.5
go run -mod=mod "$fixture_dir/generate.go" \
  "$fixture_dir/tailscaled.state.v1.102.5.json" \
  "$generator_dir/tailscaled.multi-profile.state.v1.102.5.json"
cmp "$generator_dir/tailscaled.multi-profile.state.v1.102.5.json" \
  "$fixture_dir/tailscaled.multi-profile.state.v1.102.5.json"
```

The script's exact key allowlists come from the pinned JSON field names in [ipn/prefs.go](https://github.com/tailscale/tailscale/blob/v1.102.5/ipn/prefs.go#L59-L349), [types/persist/persist.go](https://github.com/tailscale/tailscale/blob/v1.102.5/types/persist/persist.go#L21-L36), [tailcfg/tailcfg.go](https://github.com/tailscale/tailscale/blob/v1.102.5/tailcfg/tailcfg.go#L289-L302), [drive/remote.go](https://github.com/tailscale/tailscale/blob/v1.102.5/drive/remote.go#L29-L49), and the Linux TPM representation in [feature/tpm/attestation.go](https://github.com/tailscale/tailscale/blob/v1.102.5/feature/tpm/attestation.go#L144-L147). The generator prints the exported types' JSON names for comparison. The TPM object's names are verified against source because generation requires hardware.

Outer keys follow [ipn/store.go](https://github.com/tailscale/tailscale/blob/v1.102.5/ipn/store.go#L24-L86), [ServeConfigKey](https://github.com/tailscale/tailscale/blob/v1.102.5/ipn/serve.go#L28-L30), [profile keys](https://github.com/tailscale/tailscale/blob/v1.102.5/ipn/ipnlocal/profiles.go#L494-L503), [debug keys](https://github.com/tailscale/tailscale/blob/v1.102.5/ipn/ipnlocal/local.go#L839-L884), and [route-info keys](https://github.com/tailscale/tailscale/blob/v1.102.5/ipn/ipnlocal/local.go#L8584-L8588). Unknown keys, spelling variants, and case-insensitive duplicates fail closed. The literal `State schema` version and the fixture version are each checked against the image pin, so a version bump requires refreshing the schema and fixtures.
