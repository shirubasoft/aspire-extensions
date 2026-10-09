// Run in an isolated Go module requiring tailscale.com v1.102.5. See README.md.
package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"net/netip"
	"os"
	"reflect"
	"strings"

	"tailscale.com/drive"
	"tailscale.com/ipn"
	"tailscale.com/ipn/store"
	"tailscale.com/tailcfg"
	"tailscale.com/types/appctype"
	"tailscale.com/types/persist"
)

func must(err error) {
	if err != nil {
		panic(err)
	}
}

func jsonFields(value any) string {
	typ := reflect.TypeOf(value)
	var names []string
	for i := 0; i < typ.NumField(); i++ {
		field := typ.Field(i)
		if !field.IsExported() {
			continue
		}
		name := strings.Split(field.Tag.Get("json"), ",")[0]
		if name == "-" {
			continue
		}
		if name == "" {
			name = field.Name
		}
		names = append(names, name)
	}
	return strings.Join(names, " ")
}

func main() {
	if len(os.Args) != 3 {
		panic("usage: generate.go <sanitized-state-fixture> <new-output-path>")
	}
	for _, value := range []any{ipn.Prefs{}, ipn.AutoUpdatePrefs{}, ipn.AppConnectorPrefs{}, persist.Persist{}, tailcfg.UserProfile{}, drive.Share{}, ipn.LoginProfile{}, ipn.NetworkProfile{}} {
		fmt.Printf("%T: %s\n", value, jsonFields(value))
	}
	input, err := os.ReadFile(os.Args[1])
	must(err)
	var state map[ipn.StateKey][]byte
	must(json.Unmarshal(input, &state))
	current := ipn.StateKey(state[ipn.CurrentProfileStateKey])
	var prefs ipn.Prefs
	must(ipn.PrefsFromBytes(state[current], &prefs))
	if !bytes.Equal(state[current], prefs.ToBytes()) {
		panic("prefs do not round-trip exactly through upstream ToBytes")
	}
	var profiles map[ipn.ProfileID]ipn.LoginProfile
	must(json.Unmarshal(state[ipn.KnownProfilesStateKey], &profiles))
	metadata, err := json.Marshal(profiles)
	must(err)
	if !bytes.Equal(metadata, state[ipn.KnownProfilesStateKey]) {
		panic("metadata does not round-trip exactly through upstream LoginProfile")
	}
	fmt.Println("sanitized fixture round-trips exactly through upstream prefs and LoginProfile APIs")

	// Exercise optional JSON fields and every object type that is portable without hardware.
	prefs.AutoExitNode = ipn.AnyExitNode
	prefs.ForceDaemon = true
	prefs.Egg = true
	prefs.NoStatefulFiltering = "true"
	prefs.OperatorUser = "fixture-user"
	prefs.ProfileName = "fixture-profile"
	prefs.RelayServerPort = new(uint16(12345))
	prefs.RelayServerStaticEndpoints = []netip.AddrPort{netip.MustParseAddrPort("192.0.2.1:12345")}
	prefs.DriveShares = []*drive.Share{{Name: "fixture-share", Path: "/fixture", As: "fixture-user", BookmarkData: []byte("fixture")}}
	prefs.Persist.DisallowedTKAStateIDs = []string{"fixture-state"}
	prefs.Persist.UserProfile.ProfilePicURL = "https://example.invalid/fixture.png"
	prefs.Persist.UserProfile.Groups = []string{"group:fixture"}
	state[current] = prefs.ToBytes()
	currentID := ipn.ProfileID(strings.TrimPrefix(string(current), "profile-"))
	state[ipn.ServeConfigKey(currentID)], err = json.Marshal(ipn.ServeConfig{})
	must(err)
	state[current+"||_routeInfo"], err = json.Marshal(appctype.RouteInfo{})
	must(err)

	writeStore(strings.Replace(os.Args[2], "multi-profile", "single-profile", 1), state)
	second := prefs
	second.AdvertiseTags = []string{"tag:web"}
	state["profile-d4e5"] = second.ToBytes()
	profiles["d4e5"] = ipn.LoginProfile{ID: "d4e5", Key: "profile-d4e5", NodeID: "nOTHERNODE", ControlURL: prefs.ControlURL}
	state[ipn.KnownProfilesStateKey], err = json.Marshal(profiles)
	must(err)
	writeStore(os.Args[2], state)
}

func writeStore(output string, state map[ipn.StateKey][]byte) {
	if _, err := os.Stat(output); !os.IsNotExist(err) {
		panic("output path must not already exist")
	}
	fileStore, err := store.NewFileStore(func(string, ...any) {}, output)
	must(err)
	for key, value := range state {
		must(fileStore.WriteState(key, value))
	}
	fmt.Printf("fixture written through upstream NewFileStore: %s\n", output)
}
