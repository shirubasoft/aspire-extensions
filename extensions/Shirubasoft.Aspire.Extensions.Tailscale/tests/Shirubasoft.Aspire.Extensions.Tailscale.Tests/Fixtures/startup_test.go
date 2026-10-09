package main

import (
	"bytes"
	"encoding/json"
	"maps"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"tailscale.com/ipn"
	"tailscale.com/ipn/ipnlocal"
	"tailscale.com/ipn/store"
)

// Observe the real startup path, including its second lookup through embedded ID.
// ReadStartupPrefsForTest asserts testing mode and starts no daemon or connection.
type observedStore struct {
	ipn.StateStore
	profileReads []ipn.StateKey
}

func (s *observedStore) ReadState(key ipn.StateKey) ([]byte, error) {
	if strings.HasPrefix(string(key), "profile-") {
		s.profileReads = append(s.profileReads, key)
	}
	return s.StateStore.ReadState(key)
}

type startupCase struct {
	name string
	data []byte
}

func TestAcceptedStartupShapes(t *testing.T) {
	fixtureDir := os.Getenv("TAILSCALE_FIXTURE_DIR")
	if fixtureDir == "" {
		t.Fatal("TAILSCALE_FIXTURE_DIR must point to the checked-in fixtures")
	}
	captured, err := os.ReadFile(filepath.Join(fixtureDir, "tailscaled.state.v1.102.5.json"))
	if err != nil {
		t.Fatal(err)
	}
	var capturedState map[ipn.StateKey][]byte
	if err := json.Unmarshal(captured, &capturedState); err != nil {
		t.Fatal(err)
	}
	machineOnly, err := json.MarshalIndent(map[ipn.StateKey][]byte{
		ipn.MachineKeyStateKey: capturedState[ipn.MachineKeyStateKey],
	}, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	cases := []startupCase{
		{"missing-file", nil},
		{"empty-store", []byte("{}")},
		{"machine-key-before-registration", machineOnly},
		{"captured-registration", captured},
	}
	extended, err := os.ReadFile(filepath.Join(fixtureDir, "tailscaled.single-profile.state.v1.102.5.json"))
	if err != nil {
		t.Fatal(err)
	}
	cases = append(cases, startupCase{"single-profile", extended})
	crlfState := maps.Clone(capturedState)
	crlfState["profile-c298"] = bytes.ReplaceAll(crlfState["profile-c298"], []byte("\n"), []byte("\r\n"))
	crlf, err := json.MarshalIndent(crlfState, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	cases = append(cases, startupCase{"crlf-prefs", crlf})
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			path := filepath.Join(t.TempDir(), "tailscaled.state")
			if tc.data != nil {
				if err := os.WriteFile(path, tc.data, 0600); err != nil {
					t.Fatal(err)
				}
			}
			for restart := 0; restart < 2; restart++ {
				fileStore, err := store.NewFileStore(t.Logf, path)
				if err != nil {
					t.Fatal(err)
				}
				before, err := os.ReadFile(path)
				if err != nil {
					t.Fatal(err)
				}
				observed := &observedStore{StateStore: fileStore}
				prefs, err := ipnlocal.ReadStartupPrefsForTest(t.Logf, observed)
				if err != nil {
					t.Fatal(err)
				}
				var state map[ipn.StateKey][]byte
				if err := json.Unmarshal(before, &state); err != nil {
					t.Fatal(err)
				}
				current := ipn.StateKey(state[ipn.CurrentProfileStateKey])
				if current == "" {
					if len(observed.profileReads) != 0 || prefs.AdvertiseTags().Len() != 0 || prefs.WantRunning() || !prefs.LoggedOut() {
						t.Fatalf("fresh state loaded persisted prefs: %s reads=%v", prefs.Pretty(), observed.profileReads)
					}
				} else {
					if current != "profile-c298" || !slices.Equal(observed.profileReads, []ipn.StateKey{current}) {
						t.Fatalf("startup loaded %v, guard checked %q", observed.profileReads, current)
					}
					if !slices.Equal(prefs.AdvertiseTags().AsSlice(), []string{"tag:apps"}) || !prefs.WantRunning() || prefs.LoggedOut() {
						t.Fatalf("unexpected effective startup prefs: %s", prefs.Pretty())
					}
					if !bytes.Equal(prefs.AsStruct().ToBytes(), bytes.ReplaceAll(state[current], []byte("\r\n"), []byte("\n"))) {
						t.Fatal("loaded prefs differ from the complete prefs in the guard's selected key")
					}
				}
				after, err := os.ReadFile(path)
				if err != nil || !bytes.Equal(before, after) {
					t.Fatalf("startup changed accepted state: %v", err)
				}
				t.Logf("restart=%d profile=%q tags=%v reads=%v state unchanged", restart, current, prefs.AdvertiseTags().AsSlice(), observed.profileReads)
			}
		})
	}
}

// Keep the two review reproducers tied to the actual upstream loader.
func TestUpstreamLoadsLegacyAndMetadataRedirect(t *testing.T) {
	fixtureDir := os.Getenv("TAILSCALE_FIXTURE_DIR")
	for _, name := range []string{"legacy-daemon", "metadata-id-redirect"} {
		t.Run(name, func(t *testing.T) {
			fixture := "tailscaled.state.v1.102.5.json"
			if name == "metadata-id-redirect" {
				fixture = "tailscaled.multi-profile.state.v1.102.5.json"
			}
			data, err := os.ReadFile(filepath.Join(fixtureDir, fixture))
			if err != nil {
				t.Fatal(err)
			}
			var state map[ipn.StateKey][]byte
			if err := json.Unmarshal(data, &state); err != nil {
				t.Fatal(err)
			}
			if name == "legacy-daemon" {
				prefs := ipn.NewPrefs()
				if err := ipn.PrefsFromBytes(state["profile-c298"], prefs); err != nil {
					t.Fatal(err)
				}
				prefs.AdvertiseTags = []string{"tag:web"}
				state = map[ipn.StateKey][]byte{
					ipn.MachineKeyStateKey:         state[ipn.MachineKeyStateKey],
					ipn.LegacyGlobalDaemonStateKey: prefs.ToBytes(),
				}
			} else {
				var profiles map[ipn.ProfileID]ipn.LoginProfile
				if err := json.Unmarshal(state[ipn.KnownProfilesStateKey], &profiles); err != nil {
					t.Fatal(err)
				}
				profile := profiles["c298"]
				profile.ID = "d4e5"
				profiles["c298"] = profile
				state[ipn.KnownProfilesStateKey], err = json.Marshal(profiles)
				if err != nil {
					t.Fatal(err)
				}
			}
			data, err = json.MarshalIndent(state, "", "  ")
			if err != nil {
				t.Fatal(err)
			}
			path := filepath.Join(t.TempDir(), "tailscaled.state")
			if err := os.WriteFile(path, data, 0600); err != nil {
				t.Fatal(err)
			}
			fileStore, err := store.NewFileStore(t.Logf, path)
			if err != nil {
				t.Fatal(err)
			}
			observed := &observedStore{StateStore: fileStore}
			prefs, err := ipnlocal.ReadStartupPrefsForTest(t.Logf, observed)
			if err != nil {
				t.Fatal(err)
			}
			if !slices.Equal(prefs.AdvertiseTags().AsSlice(), []string{"tag:web"}) || !prefs.WantRunning() {
				t.Fatalf("reproducer did not load the unchecked tag set: %s", prefs.Pretty())
			}
			if name == "metadata-id-redirect" && !slices.Equal(observed.profileReads, []ipn.StateKey{"profile-d4e5"}) {
				t.Fatalf("unexpected redirected reads: %v", observed.profileReads)
			}
			t.Logf("unchecked tags=%v profile reads=%v", prefs.AdvertiseTags().AsSlice(), observed.profileReads)
		})
	}
}
