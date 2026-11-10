// Package main is the GodsWar launcher: it downloads the client from the VPS,
// keeps itself current, and starts the game. Trust comes only from the Ed25519
// key compiled in below; the server holds files, never trust.
package main

import (
	"archive/zip"
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"time"
)

// Version is this binary's own version. Bump it for every published launcher.
const Version = "1.0.1"

// PublicKey is the raw Ed25519 public key (same trust root as gwupdate).
// Both are set at build time: go build -ldflags "-X main.PublicKey=... -X main.DefaultManifestURL=..." (see build.sh).
var PublicKey = ""

var DefaultManifestURL = ""

type File struct {
	Version string `json:"version"`
	URL     string `json:"url"`
	Size    int64  `json:"size"`
	SHA256  string `json:"sha256"`
}

type Manifest struct {
	Serial   int64           `json:"serial"`
	Launcher map[string]File `json:"launcher"`
	Client   map[string]File `json:"client"`
}

type State struct {
	Serial          int64  `json:"serial"`
	ClientVersion   string `json:"client_version"`
	ClientRoot      string `json:"client_root"`
	LauncherVersion string `json:"launcher_version"`
	LauncherSHA256  string `json:"launcher_sha256"`
	LauncherPath    string `json:"launcher_path"`
}

type Config struct {
	ManifestURL string
	PubKey      ed25519.PublicKey
	DataDir     string
	Platform    string // "mac" or "windows"
	Version     string
	NoLaunch    bool
	HTTP        *http.Client
	Logf        func(string, ...any)
	// OnFirstInstall tells the player what is about to happen before a big first download.
	OnFirstInstall func(sizeMB int64)
}

func (c *Config) logf(f string, a ...any) {
	if c.Logf != nil {
		c.Logf(f, a...)
	}
}

func PlatformName() string {
	if runtime.GOOS == "windows" {
		return "windows"
	}
	return "mac"
}

// CompareVersions compares dotted integers: 1.2.10 > 1.2.9.
func CompareVersions(a, b string) int {
	pa, pb := strings.Split(a, "."), strings.Split(b, ".")
	for i := 0; i < len(pa) || i < len(pb); i++ {
		var x, y int
		if i < len(pa) {
			x, _ = strconv.Atoi(pa[i])
		}
		if i < len(pb) {
			y, _ = strconv.Atoi(pb[i])
		}
		if x != y {
			if x < y {
				return -1
			}
			return 1
		}
	}
	return 0
}

func (c *Config) get(url string, hdr map[string]string) (*http.Response, error) {
	req, err := http.NewRequest("GET", url, nil)
	if err != nil {
		return nil, err
	}
	for k, v := range hdr {
		req.Header.Set(k, v)
	}
	return c.HTTP.Do(req)
}

func (c *Config) readAll(url string, limit int64) ([]byte, error) {
	resp, err := c.get(url, nil)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return nil, fmt.Errorf("%s: HTTP %d", url, resp.StatusCode)
	}
	return io.ReadAll(io.LimitReader(resp.Body, limit))
}

// FetchManifest downloads and verifies manifest.json and manifest.json.sig.
func (c *Config) FetchManifest() (*Manifest, error) {
	raw, err := c.readAll(c.ManifestURL, 1<<20)
	if err != nil {
		return nil, err
	}
	sigText, err := c.readAll(c.ManifestURL+".sig", 4096)
	if err != nil {
		return nil, err
	}
	sig, err := base64.StdEncoding.DecodeString(strings.TrimSpace(string(sigText)))
	if err != nil || !ed25519.Verify(c.PubKey, raw, sig) {
		return nil, errors.New("manifest signature is not valid; refusing to continue")
	}
	var m Manifest
	if err := json.Unmarshal(raw, &m); err != nil {
		return nil, err
	}
	return &m, nil
}

func fileSHA256(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

// Download fetches f.URL into dest, resuming a .part file, and checks size and sha256.
// A bad file is deleted and fetched once more.
func (c *Config) Download(f File, dest string) error {
	var last error
	for attempt := 0; attempt < 2; attempt++ {
		if last = c.downloadOnce(f, dest); last == nil {
			return nil
		}
		c.logf("download problem: %v", last)
		os.Remove(dest + ".part")
	}
	return last
}

func (c *Config) downloadOnce(f File, dest string) error {
	part := dest + ".part"
	if err := os.MkdirAll(filepath.Dir(dest), 0o755); err != nil {
		return err
	}
	have := int64(0)
	if st, err := os.Stat(part); err == nil {
		have = st.Size()
	}
	if have > f.Size {
		os.Remove(part)
		have = 0
	}
	if have < f.Size {
		hdr := map[string]string{}
		if have > 0 {
			hdr["Range"] = fmt.Sprintf("bytes=%d-", have)
		}
		resp, err := c.get(f.URL, hdr)
		if err != nil {
			return err
		}
		defer resp.Body.Close()
		flags := os.O_CREATE | os.O_WRONLY
		switch {
		case resp.StatusCode == 206 && have > 0:
			flags |= os.O_APPEND
		case resp.StatusCode == 200:
			flags |= os.O_TRUNC // server ignored Range: start over
		default:
			return fmt.Errorf("%s: HTTP %d", f.URL, resp.StatusCode)
		}
		out, err := os.OpenFile(part, flags, 0o644)
		if err != nil {
			return err
		}
		_, err = io.Copy(out, &idleReader{r: resp.Body, d: 60 * time.Second})
		out.Close()
		if err != nil {
			return err // keep the .part so the next run resumes
		}
	}
	st, err := os.Stat(part)
	if err != nil || st.Size() != f.Size {
		os.Remove(part)
		return errors.New("downloaded size does not match the manifest")
	}
	sum, err := fileSHA256(part)
	if err != nil || !strings.EqualFold(sum, f.SHA256) {
		os.Remove(part)
		return errors.New("downloaded file failed its sha256 check")
	}
	return os.Rename(part, dest)
}

// ExtractZip unpacks with the Go reader; it refuses paths that leave dest.
func ExtractZip(zipPath, dest string) error {
	zr, err := zip.OpenReader(zipPath)
	if err != nil {
		return err
	}
	defer zr.Close()
	base, _ := filepath.Abs(dest)
	for _, f := range zr.File {
		target := filepath.Join(base, filepath.FromSlash(f.Name))
		if target != base && !strings.HasPrefix(target, base+string(os.PathSeparator)) {
			return fmt.Errorf("zip entry %q leaves the install folder", f.Name)
		}
		if f.FileInfo().IsDir() {
			if err := os.MkdirAll(target, 0o755); err != nil {
				return err
			}
			continue
		}
		if f.Mode()&os.ModeSymlink != 0 {
			return fmt.Errorf("zip entry %q is a symlink; use ditto", f.Name)
		}
		if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil {
			return err
		}
		rc, err := f.Open()
		if err != nil {
			return err
		}
		out, err := os.OpenFile(target, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, f.Mode()|0o644)
		if err != nil {
			rc.Close()
			return err
		}
		_, err = io.Copy(out, rc)
		out.Close()
		rc.Close()
		if err != nil {
			return err
		}
	}
	return nil
}

// zipHasUnsafeEntries lets the Mac path check names before handing the zip to ditto.
func zipHasUnsafeEntries(zipPath string) error {
	zr, err := zip.OpenReader(zipPath)
	if err != nil {
		return err
	}
	defer zr.Close()
	for _, f := range zr.File {
		n := filepath.ToSlash(filepath.Clean(f.Name))
		if strings.HasPrefix(n, "/") || n == ".." || strings.HasPrefix(n, "../") || strings.Contains(n, "/../") {
			return fmt.Errorf("zip entry %q leaves the install folder", f.Name)
		}
	}
	return nil
}

func extract(zipPath, dest string) error {
	if runtime.GOOS == "darwin" {
		if err := zipHasUnsafeEntries(zipPath); err != nil {
			return err
		}
		if err := os.MkdirAll(dest, 0o755); err != nil {
			return err
		}
		if out, err := exec.Command("/usr/bin/ditto", "-x", "-k", zipPath, dest).CombinedOutput(); err != nil {
			return fmt.Errorf("ditto: %v: %s", err, out)
		}
		return nil
	}
	return ExtractZip(zipPath, dest)
}

func readState(dir string) State {
	var s State
	if b, err := os.ReadFile(filepath.Join(dir, "state.json")); err == nil {
		json.Unmarshal(b, &s)
	}
	return s
}

func writeState(dir string, s State) error {
	b, _ := json.MarshalIndent(s, "", "  ")
	tmp := filepath.Join(dir, "state.json.tmp")
	if err := os.WriteFile(tmp, b, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, filepath.Join(dir, "state.json"))
}

// launcherPath names one copy per version: a running Windows exe cannot be replaced or deleted,
// so each update gets a new file name.
func launcherPath(dir, platform, version string) string {
	name := "gwlauncher-" + version
	if platform == "windows" {
		name += ".exe"
	}
	return filepath.Join(dir, "launcher", name)
}

// DelegateTarget returns a newer, verified launcher copy in the data folder, if any.
func DelegateTarget(c *Config) (string, bool) {
	if os.Getenv("GW_LAUNCHER_DELEGATED") == "1" {
		return "", false
	}
	st := readState(c.DataDir)
	p := st.LauncherPath
	if p == "" || st.LauncherVersion == "" || CompareVersions(st.LauncherVersion, c.Version) <= 0 {
		return "", false
	}
	if sum, err := fileSHA256(p); err != nil || !strings.EqualFold(sum, st.LauncherSHA256) {
		return "", false
	}
	return p, true
}

func findClientRoot(stage, platform string) (string, error) {
	want := "GodsWar.exe"
	if platform == "mac" {
		want = "GodsWar.app"
	}
	if _, err := os.Stat(filepath.Join(stage, want)); err == nil {
		return stage, nil
	}
	ents, _ := os.ReadDir(stage)
	for _, e := range ents {
		if e.IsDir() {
			if _, err := os.Stat(filepath.Join(stage, e.Name(), want)); err == nil {
				return filepath.Join(stage, e.Name()), nil
			}
		}
	}
	return "", fmt.Errorf("the download does not contain %s", want)
}

// Update checks the manifest, updates the launcher copy and the client, and returns the client root.
func Update(c *Config) (string, error) {
	m, err := c.FetchManifest()
	if err != nil {
		return "", err
	}
	st := readState(c.DataDir)
	if m.Serial < st.Serial {
		return "", fmt.Errorf("manifest serial %d is older than the last seen %d; refusing a rollback", m.Serial, st.Serial)
	}
	if err := os.MkdirAll(c.DataDir, 0o755); err != nil {
		return "", err
	}
	// Launcher copy: only written to the data folder, never over the running file.
	if lf, ok := m.Launcher[c.Platform]; ok && CompareVersions(lf.Version, firstNonEmpty(st.LauncherVersion, c.Version)) > 0 {
		c.logf("updating the launcher to %s", lf.Version)
		dest := launcherPath(c.DataDir, c.Platform, lf.Version)
		os.Remove(dest)
		if err := c.Download(lf, dest); err != nil {
			c.logf("launcher update failed, continuing with this one: %v", err)
		} else {
			os.Chmod(dest, 0o755)
			st.LauncherVersion, st.LauncherSHA256, st.LauncherPath = lf.Version, strings.ToLower(lf.SHA256), dest
			removeOldLaunchers(filepath.Dir(dest), dest)
		}
	}
	cf, ok := m.Client[c.Platform]
	if !ok {
		return "", fmt.Errorf("no %s client in the manifest", c.Platform)
	}
	installed := st.ClientRoot != "" && st.ClientVersion != ""
	if installed {
		if _, err := os.Stat(st.ClientRoot); err != nil {
			installed = false
		}
	}
	if !installed || CompareVersions(cf.Version, st.ClientVersion) > 0 {
		if !installed && c.OnFirstInstall != nil {
			c.OnFirstInstall(cf.Size >> 20)
		}
		c.logf("downloading the client %s (%d MB)", cf.Version, cf.Size>>20)
		zipPath := filepath.Join(c.DataDir, "downloads", filepath.Base(cf.URL))
		if err := c.Download(cf, zipPath); err != nil {
			return "", err
		}
		stage := filepath.Join(c.DataDir, "client-new")
		os.RemoveAll(stage)
		c.logf("unpacking")
		if err := extract(zipPath, stage); err != nil {
			os.RemoveAll(stage)
			return "", err
		}
		if _, err := findClientRoot(stage, c.Platform); err != nil {
			os.RemoveAll(stage)
			return "", err
		}
		live := filepath.Join(c.DataDir, "client")
		old := filepath.Join(c.DataDir, "client-old")
		os.RemoveAll(old)
		if _, err := os.Stat(live); err == nil {
			if err := os.Rename(live, old); err != nil {
				return "", err
			}
		}
		if err := os.Rename(stage, live); err != nil {
			os.Rename(old, live)
			return "", err
		}
		root, _ := findClientRoot(live, c.Platform)
		os.RemoveAll(old)
		os.Remove(zipPath)
		st.ClientVersion, st.ClientRoot = cf.Version, root
	}
	st.Serial = m.Serial
	if err := writeState(c.DataDir, st); err != nil {
		return "", err
	}
	return st.ClientRoot, nil
}

// removeOldLaunchers deletes older copies; a copy that is still running cannot be deleted on Windows, so errors are ignored.
func removeOldLaunchers(dir, keep string) {
	ents, _ := os.ReadDir(dir)
	for _, e := range ents {
		p := filepath.Join(dir, e.Name())
		if p != keep && strings.HasPrefix(e.Name(), "gwlauncher-") && !strings.HasSuffix(e.Name(), ".part") {
			os.Remove(p)
		}
	}
}

func firstNonEmpty(a, b string) string {
	if a != "" {
		return a
	}
	return b
}

func Launch(root, platform string) error {
	if platform == "mac" {
		return exec.Command("/usr/bin/open", filepath.Join(root, "GodsWar.app")).Start()
	}
	cmd := exec.Command(filepath.Join(root, "GodsWar.exe"))
	cmd.Dir = root
	return cmd.Start()
}

// idleReader closes the body when no bytes arrive for d, so a stalled connection ends and resumes.
type idleReader struct {
	r io.ReadCloser
	d time.Duration
	t *time.Timer
}

func (i *idleReader) Read(p []byte) (int, error) {
	if i.t == nil {
		i.t = time.AfterFunc(i.d, func() { i.r.Close() })
	}
	i.t.Reset(i.d)
	n, err := i.r.Read(p)
	if err != nil {
		i.t.Stop()
	}
	return n, err
}
