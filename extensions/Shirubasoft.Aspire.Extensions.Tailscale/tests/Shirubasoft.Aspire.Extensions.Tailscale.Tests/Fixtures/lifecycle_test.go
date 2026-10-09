// Copy into the pinned upstream ipn/ipnlocal package to exercise its private manager.
package ipnlocal

import (
	"encoding/json"
	"path/filepath"
	"testing"

	"tailscale.com/health"
	"tailscale.com/ipn"
	"tailscale.com/ipn/store"
	"tailscale.com/tailcfg"
	"tailscale.com/types/persist"
	"tailscale.com/util/eventbus/eventbustest"
)

func TestSidecarSingleProfileLifecycle(t *testing.T) {
	state, err := store.NewFileStore(t.Logf, filepath.Join(t.TempDir(), "tailscaled.state"))
	if err != nil {
		t.Fatal(err)
	}
	newManager := func() *profileManager {
		pm, err := newProfileManagerWithGOOS(state, t.Logf, health.NewTracker(eventbustest.NewBus(t)), "linux")
		if err != nil {
			t.Fatal(err)
		}
		return pm
	}
	pm := newManager()
	prefs := ipn.NewPrefs()
	prefs.WantRunning = true
	prefs.AdvertiseTags = []string{"tag:apps"}
	prefs.Persist = &persist.Persist{
		NodeID: "nFIRST",
		UserProfile: tailcfg.UserProfile{
			ID:        1000,
			LoginName: "tagged-devices",
		},
	}
	if err := pm.SetPrefs(prefs.View(), ipn.NetworkProfile{}); err != nil {
		t.Fatal(err)
	}
	originalID := pm.CurrentProfile().ID()
	if originalID == "" {
		t.Fatal("registration did not persist a profile")
	}
	for _, stage := range []string{"restart", "reauth-same-node", "reauth-new-node", "restart-after-reauth"} {
		pm = newManager()
		if stage == "reauth-new-node" {
			prefs.Persist.NodeID = "nSECOND"
			prefs.Persist.UserProfile.ID = 2000
		}
		if err := pm.SetPrefs(prefs.View(), ipn.NetworkProfile{}); err != nil {
			t.Fatal(err)
		}
		metadata, err := state.ReadState(ipn.KnownProfilesStateKey)
		if err != nil {
			t.Fatal(err)
		}
		var profiles map[ipn.ProfileID]ipn.LoginProfile
		if err := json.Unmarshal(metadata, &profiles); err != nil {
			t.Fatal(err)
		}
		current, err := state.ReadState(ipn.CurrentProfileStateKey)
		if err != nil {
			t.Fatal(err)
		}
		if len(profiles) != 1 || pm.CurrentProfile().ID() != originalID || string(current) != string(profiles[originalID].Key) {
			t.Fatalf("%s changed the single-profile shape: current=%q profiles=%v", stage, current, profiles)
		}
		t.Logf("%s: one profile, ID=%q Key=%q node=%q", stage, originalID, current, prefs.Persist.NodeID)
	}
}
