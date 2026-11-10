package main

import (
	"archive/zip"
	"bytes"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func makeZip(files map[string]string) []byte {
	var b bytes.Buffer
	zw := zip.NewWriter(&b)
	for n, c := range files {
		w, _ := zw.Create(n)
		w.Write([]byte(c))
	}
	zw.Close()
	return b.Bytes()
}

func sum(b []byte) string { h := sha256.Sum256(b); return hex.EncodeToString(h[:]) }

type rig struct {
	srv      *httptest.Server
	pub      ed25519.PublicKey
	priv     ed25519.PrivateKey
	manifest Manifest
	files    map[string][]byte
	badSig   bool
	ranged   int32
	cfg      *Config
}

func newRig(t *testing.T, platform, clientVer string, zipFiles map[string]string) *rig {
	r := &rig{files: map[string][]byte{}}
	r.pub, r.priv, _ = ed25519.GenerateKey(rand.Reader)
	zb := makeZip(zipFiles)
	r.files["/c.zip"] = zb
	r.files["/gwlauncher"] = []byte("new launcher binary")
	mux := http.NewServeMux()
	mux.HandleFunc("/manifest.json", func(w http.ResponseWriter, q *http.Request) {
		b, _ := json.Marshal(r.manifest)
		w.Write(b)
	})
	mux.HandleFunc("/manifest.json.sig", func(w http.ResponseWriter, q *http.Request) {
		b, _ := json.Marshal(r.manifest)
		s := ed25519.Sign(r.priv, b)
		if r.badSig {
			s[0] ^= 1
		}
		w.Write([]byte(base64.StdEncoding.EncodeToString(s)))
	})
	mux.HandleFunc("/", func(w http.ResponseWriter, q *http.Request) {
		b, ok := r.files[q.URL.Path]
		if !ok {
			http.NotFound(w, q)
			return
		}
		if q.Header.Get("Range") != "" {
			atomic.AddInt32(&r.ranged, 1)
		}
		http.ServeContent(w, q, "f", time.Time{}, bytes.NewReader(b))
	})
	r.srv = httptest.NewServer(mux)
	t.Cleanup(r.srv.Close)
	r.manifest = Manifest{Serial: 1,
		Client:   map[string]File{platform: {Version: clientVer, URL: r.srv.URL + "/c.zip", Size: int64(len(zb)), SHA256: sum(zb)}},
		Launcher: map[string]File{platform: {Version: "1.0.0", URL: r.srv.URL + "/gwlauncher", Size: int64(len(r.files["/gwlauncher"])), SHA256: sum(r.files["/gwlauncher"])}}}
	r.cfg = &Config{ManifestURL: r.srv.URL + "/manifest.json", PubKey: r.pub, DataDir: t.TempDir(), Platform: platform, Version: "1.0.0", HTTP: http.DefaultClient}
	return r
}

func clientFiles(platform string) map[string]string {
	if platform == "mac" {
		return map[string]string{"GodsWar.app/Contents/Info.plist": "x"}
	}
	return map[string]string{"GodsWar (Windows)/GodsWar.exe": "MZ", "GodsWar (Windows)/config.ini": "[SERVER]"}
}

func TestInstallThenOffline(t *testing.T) {
	p := PlatformName()
	r := newRig(t, p, "5", clientFiles(p))
	root, err := Update(r.cfg)
	if err != nil {
		t.Fatal(err)
	}
	want := map[string]string{"mac": "GodsWar.app", "windows": "GodsWar.exe"}[p]
	if _, err := os.Stat(filepath.Join(root, want)); err != nil {
		t.Fatalf("client not installed: %v", err)
	}
	if st := readState(r.cfg.DataDir); st.ClientVersion != "5" {
		t.Fatalf("state %+v", st)
	}
	r.srv.Close()
	if _, err := Update(r.cfg); err == nil {
		t.Fatal("expected a network error")
	}
	if st := readState(r.cfg.DataDir); st.ClientRoot != root {
		t.Fatal("state lost after a failed check")
	}
}

func TestWindowsGoExtractionLayout(t *testing.T) {
	r := newRig(t, "windows", "5", clientFiles("windows"))
	r.cfg.Platform = "windows"
	dest := t.TempDir()
	zp := filepath.Join(dest, "c.zip")
	os.WriteFile(zp, r.files["/c.zip"], 0o644)
	if err := ExtractZip(zp, filepath.Join(dest, "out")); err != nil {
		t.Fatal(err)
	}
	root, err := findClientRoot(filepath.Join(dest, "out"), "windows")
	if err != nil || filepath.Base(root) != "GodsWar (Windows)" {
		t.Fatalf("root %q err %v", root, err)
	}
}

func TestBadSignature(t *testing.T) {
	p := PlatformName()
	r := newRig(t, p, "5", clientFiles(p))
	r.badSig = true
	if _, err := Update(r.cfg); err == nil || !strings.Contains(err.Error(), "signature") {
		t.Fatalf("want signature error, got %v", err)
	}
	if _, err := os.Stat(filepath.Join(r.cfg.DataDir, "client")); err == nil {
		t.Fatal("installed despite bad signature")
	}
}

func TestTamperedZip(t *testing.T) {
	p := PlatformName()
	r := newRig(t, p, "5", clientFiles(p))
	b := append([]byte{}, r.files["/c.zip"]...)
	b[len(b)/2] ^= 0xff
	r.files["/c.zip"] = b
	if _, err := Update(r.cfg); err == nil || !strings.Contains(err.Error(), "sha256") {
		t.Fatalf("want sha256 error, got %v", err)
	}
	if _, err := os.Stat(filepath.Join(r.cfg.DataDir, "client")); err == nil {
		t.Fatal("installed a tampered download")
	}
}

func TestRollbackRefused(t *testing.T) {
	p := PlatformName()
	r := newRig(t, p, "5", clientFiles(p))
	r.manifest.Serial = 7
	if _, err := Update(r.cfg); err != nil {
		t.Fatal(err)
	}
	r.manifest.Serial = 3
	if _, err := Update(r.cfg); err == nil || !strings.Contains(err.Error(), "rollback") {
		t.Fatalf("want rollback refusal, got %v", err)
	}
}

func TestResume(t *testing.T) {
	r := newRig(t, "windows", "5", clientFiles("windows"))
	f := r.manifest.Client["windows"]
	dest := filepath.Join(r.cfg.DataDir, "downloads", "c.zip")
	os.MkdirAll(filepath.Dir(dest), 0o755)
	os.WriteFile(dest+".part", r.files["/c.zip"][:50], 0o644)
	if err := r.cfg.Download(f, dest); err != nil {
		t.Fatal(err)
	}
	if atomic.LoadInt32(&r.ranged) == 0 {
		t.Fatal("did not use a Range request")
	}
	if got, _ := os.ReadFile(dest); !bytes.Equal(got, r.files["/c.zip"]) {
		t.Fatal("resumed file differs")
	}
}

func TestPathTraversal(t *testing.T) {
	z := filepath.Join(t.TempDir(), "evil.zip")
	os.WriteFile(z, makeZip(map[string]string{"../evil.txt": "x"}), 0o644)
	if err := ExtractZip(z, t.TempDir()); err == nil {
		t.Fatal("Go extractor accepted ../")
	}
	if err := zipHasUnsafeEntries(z); err == nil {
		t.Fatal("pre-check accepted ../")
	}
}

func TestLauncherCopyAndDelegation(t *testing.T) {
	p := PlatformName()
	r := newRig(t, p, "5", clientFiles(p))
	r.manifest.Launcher[p] = File{Version: "1.1.0", URL: r.srv.URL + "/gwlauncher", Size: int64(len(r.files["/gwlauncher"])), SHA256: sum(r.files["/gwlauncher"])}
	if _, err := Update(r.cfg); err != nil {
		t.Fatal(err)
	}
	path, ok := DelegateTarget(r.cfg)
	if !ok {
		t.Fatal("no delegation to the newer copy")
	}
	t.Setenv("GW_LAUNCHER_DELEGATED", "1")
	if _, ok := DelegateTarget(r.cfg); ok {
		t.Fatal("delegation loop not prevented")
	}
	t.Setenv("GW_LAUNCHER_DELEGATED", "")
	os.WriteFile(path, []byte("tampered"), 0o755)
	if _, ok := DelegateTarget(r.cfg); ok {
		t.Fatal("delegated to a copy that fails its hash")
	}
}

func TestVersionCompare(t *testing.T) {
	if CompareVersions("1.2.10", "1.2.9") != 1 || CompareVersions("1.0", "1.0.0") != 0 || CompareVersions("0.9", "1.0") != -1 {
		t.Fatal("compare broken")
	}
}

func TestTwoSuccessiveLauncherUpdates(t *testing.T) {
	p := PlatformName()
	r := newRig(t, p, "5", clientFiles(p))
	pub := func(ver, content string) {
		r.files["/gwlauncher"] = []byte(content)
		r.manifest.Launcher[p] = File{Version: ver, URL: r.srv.URL + "/gwlauncher", Size: int64(len(content)), SHA256: sum([]byte(content))}
	}
	pub("1.1.0", "launcher one")
	if _, err := Update(r.cfg); err != nil {
		t.Fatal(err)
	}
	first, ok := DelegateTarget(r.cfg)
	if !ok {
		t.Fatal("no first delegate")
	}
	// Simulate the 1.1.0 copy now being the running process: it updates to 1.2.0.
	r.cfg.Version = "1.1.0"
	pub("1.2.0", "launcher two")
	if _, err := Update(r.cfg); err != nil {
		t.Fatal(err)
	}
	second, ok := DelegateTarget(&Config{DataDir: r.cfg.DataDir, Platform: p, Version: "1.1.0"})
	if !ok || second == first {
		t.Fatalf("second update did not get its own file: %q vs %q", first, second)
	}
	if b, _ := os.ReadFile(second); string(b) != "launcher two" {
		t.Fatal("wrong content in the second copy")
	}
	if _, err := os.Stat(first); err == nil {
		t.Fatal("old copy not removed")
	}
}

func TestSingleInstanceLock(t *testing.T) {
	if !acquireLock() {
		t.Skip("port in use on this machine")
	}
	defer lockListener.Close()
	if acquireLock() {
		t.Fatal("second instance got the lock")
	}
}

func TestIdleReaderStalls(t *testing.T) {
	pr, pw := io.Pipe()
	defer pw.Close()
	ir := &idleReader{r: pr, d: 100 * time.Millisecond}
	start := time.Now()
	_, err := ir.Read(make([]byte, 4))
	if err == nil || time.Since(start) > 2*time.Second {
		t.Fatalf("stalled read did not end: %v", err)
	}
}
