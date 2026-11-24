package main

import (
	"crypto/ed25519"
	"encoding/base64"
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
)

// chanRelease adds release <ver> (base <base>) to the rig's update channel under /updates/<ver>/.
// change: path -> new content; before: path -> old content (absent key = file did not exist); deletes: paths.
func (r *rig) chanRelease(ver, base int, change map[string]string, before map[string]*string, deletes []string, tamper bool) {
	files := []chanEntry{}
	zipFiles := map[string]string{}
	for p, c := range change {
		files = append(files, chanEntry{Path: p, Size: int64(len(c)), SHA256: sum([]byte(c))})
		zipFiles[p] = c
		if tamper {
			zipFiles[p] = c[:len(c)-1] + "!"
		}
	}
	b := map[string]*string{}
	for p := range change {
		if old, ok := before[p]; ok && old != nil {
			s := sum([]byte(*old))
			b[p] = &s
		} else {
			b[p] = nil
		}
	}
	for _, d := range deletes {
		s := sum([]byte(*before[d]))
		b[d] = &s
	}
	if deletes == nil {
		deletes = []string{}
	}
	m := chanManifest{Schema: 2, Version: ver, Base: base, Channel: "stable", Files: files, Delete: deletes, Before: b}
	raw, _ := json.Marshal(m)
	dir := "/updates/" + itoa(ver) + "/"
	r.files[dir+"manifest.json"] = raw
	r.files[dir+"manifest.sig"] = ed25519.Sign(r.priv, raw)
	r.files[dir+"payload.zip"] = makeZip(zipFiles)
	r.cfg.ChannelURL = r.srv.URL + "/updates/"
}

func itoa(i int) string { return strconv.Itoa(i) }

func strp(s string) *string { return &s }

// installed sets up a Windows client folder at version ver / release rel, as a full-zip install would leave it.
func (r *rig) installed(t *testing.T, ver string, rel int, files map[string]string) string {
	root := filepath.Join(r.cfg.DataDir, "client")
	for p, c := range files {
		os.MkdirAll(filepath.Dir(filepath.Join(root, p)), 0o755)
		os.WriteFile(filepath.Join(root, filepath.FromSlash(p)), []byte(c), 0o644)
	}
	if rel > 0 {
		writeRelease(root, rel)
	}
	writeState(r.cfg.DataDir, State{Serial: 1, ClientVersion: ver, ClientRoot: root})
	return root
}

func readF(t *testing.T, root, p string) string {
	b, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(p)))
	if err != nil {
		return "<missing>"
	}
	return string(b)
}

func base1() map[string]string {
	return map[string]string{"GodsWar.exe": "MZ", "Text/a.dat": "old a", "Text/gone.dat": "bye", "config.ini": "[SERVER]"}
}

func TestWindowsPatchInPlace(t *testing.T) {
	r := newRig(t, "windows", "1.0.1", clientFiles("windows"))
	cf := r.manifest.Client["windows"]
	cf.Release, cf.URL = 2, r.srv.URL+"/no-such.zip" // the full zip must not be needed
	r.manifest.Client["windows"] = cf
	root := r.installed(t, "1.0.0", 1, base1())
	r.chanRelease(2, 1, map[string]string{"Text/a.dat": "new a", "Map/new.dat": "fresh", "UIScale/1280x960/x.xml": "mac only", ".client-release": "2\n"},
		map[string]*string{"Text/a.dat": strp("old a"), "Text/gone.dat": strp("bye"), "UIScale/1280x960/x.xml": strp("not on windows")}, []string{"Text/gone.dat"}, false)
	got, err := Update(r.cfg)
	if err != nil {
		t.Fatal(err)
	}
	if got != root || readF(t, root, "Text/a.dat") != "new a" || readF(t, root, "Map/new.dat") != "fresh" || readF(t, root, "Text/gone.dat") != "<missing>" {
		t.Fatalf("patch not applied: root %s a=%q", got, readF(t, root, "Text/a.dat"))
	}
	if readF(t, root, "UIScale/1280x960/x.xml") != "<missing>" || readF(t, root, "config.ini") != "[SERVER]" {
		t.Fatal("UIScale must be skipped on Windows and config.ini left alone")
	}
	if InstalledRelease(root) != 2 || readState(r.cfg.DataDir).ClientVersion != "1.0.1" {
		t.Fatalf("release %d version %s", InstalledRelease(root), readState(r.cfg.DataDir).ClientVersion)
	}
}

func TestWindowsPatchChainOfTwo(t *testing.T) {
	r := newRig(t, "windows", "1.0.2", clientFiles("windows"))
	cf := r.manifest.Client["windows"]
	cf.Release, cf.URL = 3, r.srv.URL+"/no-such.zip"
	r.manifest.Client["windows"] = cf
	root := r.installed(t, "1.0.0", 1, base1())
	r.chanRelease(2, 1, map[string]string{"Text/a.dat": "a2"}, map[string]*string{"Text/a.dat": strp("old a")}, nil, false)
	r.chanRelease(3, 2, map[string]string{"Text/a.dat": "a3"}, map[string]*string{"Text/a.dat": strp("a2")}, nil, false)
	if _, err := Update(r.cfg); err != nil {
		t.Fatal(err)
	}
	if readF(t, root, "Text/a.dat") != "a3" || InstalledRelease(root) != 3 {
		t.Fatal("chain not applied")
	}
}

// A local file that differs from the release's base, a tampered payload and a missing release all fall back to the zip.
func TestWindowsPatchFallsBackToZip(t *testing.T) {
	for _, mode := range []string{"changed-local", "tampered", "gap"} {
		t.Run(mode, func(t *testing.T) {
			r := newRig(t, "windows", "1.0.1", clientFiles("windows"))
			cf := r.manifest.Client["windows"]
			cf.Release = 2
			r.manifest.Client["windows"] = cf
			files := base1()
			if mode == "changed-local" {
				files["Text/a.dat"] = "edited by the player"
			}
			oldRoot := r.installed(t, "1.0.0", 1, files)
			if mode != "gap" {
				r.chanRelease(2, 1, map[string]string{"Text/a.dat": "new a"}, map[string]*string{"Text/a.dat": strp("old a")}, nil, mode == "tampered")
			} else {
				r.cfg.ChannelURL = r.srv.URL + "/updates/"
			}
			root, err := Update(r.cfg)
			if err != nil {
				t.Fatal(err)
			}
			if _, err := os.Stat(filepath.Join(root, "GodsWar.exe")); err != nil {
				t.Fatal("full zip not installed")
			}
			if root == oldRoot {
				t.Fatal("expected the zip's layout (GodsWar (Windows)/)")
			}
			if InstalledRelease(root) != 2 {
				t.Fatalf("full install must record release 2, got %d", InstalledRelease(root))
			}
		})
	}
}

func TestWindowsPatchBadSignatureRefused(t *testing.T) {
	r := newRig(t, "windows", "1.0.1", clientFiles("windows"))
	root := r.installed(t, "1.0.0", 1, base1())
	r.chanRelease(2, 1, map[string]string{"Text/a.dat": "new a"}, map[string]*string{"Text/a.dat": strp("old a")}, nil, false)
	sig := r.files["/updates/2/manifest.sig"]
	sig[0] ^= 1
	if _, err := r.cfg.PatchClient(root, 1, 2); err == nil || !strings.Contains(err.Error(), "signature") {
		t.Fatalf("want signature error, got %v", err)
	}
	if readF(t, root, "Text/a.dat") != "old a" {
		t.Fatal("client changed")
	}
}

func TestWindowsNoReleaseNumberUsesZip(t *testing.T) {
	r := newRig(t, "windows", "1.0.1", clientFiles("windows"))
	cf := r.manifest.Client["windows"]
	cf.Release = 2
	r.manifest.Client["windows"] = cf
	r.installed(t, "1.0.0", 0, base1()) // a 1.0.2-era install of an older version: no marker
	r.chanRelease(2, 1, map[string]string{"Text/a.dat": "new a"}, map[string]*string{"Text/a.dat": strp("old a")}, nil, false)
	root, err := Update(r.cfg)
	if err != nil || !strings.Contains(root, "GodsWar (Windows)") || InstalledRelease(root) != 2 {
		t.Fatalf("want full zip install with release 2: %s %v", root, err)
	}
}

// A 1.0.2-era install of the current version gets its marker, so the next release can patch it.
func TestWindowsMarkerForCurrentInstall(t *testing.T) {
	r := newRig(t, "windows", "1.0.0", clientFiles("windows"))
	cf := r.manifest.Client["windows"]
	cf.Release = 7
	r.manifest.Client["windows"] = cf
	root := r.installed(t, "1.0.0", 0, base1())
	if _, err := Update(r.cfg); err != nil {
		t.Fatal(err)
	}
	if InstalledRelease(root) != 7 {
		t.Fatalf("marker %d", InstalledRelease(root))
	}
}

func TestMacIgnoresChannel(t *testing.T) {
	r := newRig(t, "mac", "1.0.1", clientFiles("mac"))
	cf := r.manifest.Client["mac"]
	cf.Release = 2
	r.manifest.Client["mac"] = cf
	r.chanRelease(2, 1, map[string]string{"a": "b"}, nil, nil, false)
	root, err := Update(r.cfg)
	if err != nil || InstalledRelease(root) != 0 {
		t.Fatalf("mac must not use the channel or write a marker: %v", err)
	}
}

func TestSafeChannelPath(t *testing.T) {
	for _, p := range []string{"", "/abs", "a/../b", "a\\b", "C:x", "a/./b", "con", "Text/NUL.txt", "a /b", "a./b", "x?y", "a//b", "dir/"} {
		if SafeChannelPath(p) == nil {
			t.Errorf("%q accepted", p)
		}
	}
	for _, p := range []string{"Text/a.dat", "Map/Terr1/Water_Style_001.texture", ".client-release", "console.ini"} {
		if err := SafeChannelPath(p); err != nil {
			t.Errorf("%q refused: %v", p, err)
		}
	}
}

// End to end against the real channel (needs network): GW_E2E_OLD = a Windows 1.5.5 client tree (client-1.5.5/build/win),
// GW_E2E_NEW = the Windows 1.5.6 tree. A copy of OLD is patched from release 15 to 16 with the live, really signed
// update and must then equal NEW (UIScale/ and .client-release aside).
func TestE2ELiveChannel(t *testing.T) {
	oldTree, newTree := os.Getenv("GW_E2E_OLD"), os.Getenv("GW_E2E_NEW")
	if oldTree == "" || newTree == "" {
		t.Skip("set GW_E2E_OLD and GW_E2E_NEW")
	}
	key, _ := base64.StdEncoding.DecodeString(PublicKey)
	dir := t.TempDir()
	root := filepath.Join(dir, "client")
	if err := copyTree(oldTree, root); err != nil {
		t.Fatal(err)
	}
	writeRelease(root, 15)
	c := &Config{PubKey: ed25519.PublicKey(key), DataDir: dir, Platform: "windows", HTTP: http.DefaultClient, Logf: t.Logf}
	got, err := c.PatchClient(root, 15, 16)
	if err != nil || got != 16 {
		t.Fatalf("patch: %d %v", got, err)
	}
	diffs := 0
	compare := func(a, b string) {
		filepath.Walk(a, func(p string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() {
				return nil
			}
			rel, _ := filepath.Rel(a, p)
			if rel == releaseMarker || rel == ".DS_Store" || strings.HasSuffix(rel, "/.DS_Store") {
				return nil
			}
			x, _ := fileSHA256(p)
			y, err := fileSHA256(filepath.Join(b, rel))
			if err != nil || x != y {
				diffs++
				t.Errorf("differs: %s", rel)
			}
			return nil
		})
	}
	compare(root, newTree)
	compare(newTree, root)
	if diffs == 0 {
		t.Logf("patched 1.5.5 == 1.5.6 Windows tree")
	}
}

func copyTree(src, dst string) error {
	return filepath.Walk(src, func(p string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		rel, _ := filepath.Rel(src, p)
		t := filepath.Join(dst, rel)
		if info.IsDir() {
			return os.MkdirAll(t, 0o755)
		}
		return copyFile(p, t)
	})
}
