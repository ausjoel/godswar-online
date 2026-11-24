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
const Version = "1.0.5"

// PublicKey is the raw Ed25519 public key (same trust root as gwupdate).
// Both are set at build time: go build -ldflags "-X main.PublicKey=... -X main.DefaultManifestURL=..." (see build.sh).
var PublicKey = ""

var DefaultManifestURL = ""

type File struct {
	Version string `json:"version"`
	URL     string `json:"url"`
	Size    int64  `json:"size"`
	SHA256  string `json:"sha256"`
	// Release (launcher 1.0.3+, clients only): the update-channel release this zip equals (Client/.client-release).
	// 0 = unknown: the launcher then updates that client with the full zip only.
	Release int `json:"release,omitempty"`
}

type Manifest struct {
	Serial   int64           `json:"serial"`
	Launcher map[string]File `json:"launcher"`
	Client   map[string]File `json:"client"`
	// Native (launcher 1.0.2+): the GodsWar Native app (exe-port, no Wine), Mac only. Older launchers ignore it.
	Native map[string]File `json:"native,omitempty"`
	// News (launcher 1.0.4+): the newest player-site posts for the "What's new" box. Older launchers ignore it.
	News []NewsItem `json:"news,omitempty"`
}

type State struct {
	Serial          int64  `json:"serial"`
	ClientVersion   string `json:"client_version"`
	ClientRoot      string `json:"client_root"`
	LauncherVersion string `json:"launcher_version"`
	LauncherSHA256  string `json:"launcher_sha256"`
	LauncherPath    string `json:"launcher_path"`
	// Engine (1.0.2+): "wine" or "native", the player's choice; "" = not asked yet.
	Engine        string `json:"engine,omitempty"`
	NativeVersion string `json:"native_version,omitempty"`
	NativeRoot    string `json:"native_root,omitempty"`
	// NewsHidden (1.0.4+): news ids the player chose "don't show again" for (newest last, capped).
	NewsHidden []string `json:"news_hidden,omitempty"`
	// Models (1.0.5+): the client's config.ini [SYSTEM] Avatar chosen in Settings ("1" original, "2" new; "" = untouched).
	Models string `json:"models,omitempty"`
}

// Engines. Native is the Mac-only exe-port app; Windows always runs GodsWar.exe itself.
const (
	EngineWine   = "wine"
	EngineNative = "native"
)

// Root returns the installed root for an engine ("" when that engine is not installed).
func (s State) Root(engine string) string {
	if engine == EngineNative {
		return s.NativeRoot
	}
	return s.ClientRoot
}

type Config struct {
	ManifestURL string
	PubKey      ed25519.PublicKey
	DataDir     string
	Platform    string // "mac" or "windows"
	Version     string
	NoLaunch    bool
	Engine      string // EngineWine (default) or EngineNative
	ChannelURL  string // signed update channel for in-place patching ("" = DefaultChannelURL)
	NoPatch     bool   // full zip only (testing)
	HTTP        *http.Client
	Logf        func(string, ...any)
	// OnFirstInstall tells the player what is about to happen before a big first download.
	OnFirstInstall func(sizeMB int64)
	// News: the manifest's news items from the last Update (shown by whatsNew before launch).
	News []NewsItem
	// Status and Progress (1.0.5+): the launcher window shows what is happening; nil in headless runs.
	Status   func(string)
	Progress func(what string, done, total int64)
	// progressLabel names the file being downloaded in Progress.
	progressLabel string
}

func (c *Config) logf(f string, a ...any) {
	if c.Logf != nil {
		c.Logf(f, a...)
	}
	if c.Status != nil {
		m := fmt.Sprintf(f, a...)
		if m != "" {
			c.Status(strings.ToUpper(m[:1]) + m[1:])
		}
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
		var src io.Reader = &idleReader{r: resp.Body, d: 60 * time.Second}
		if c.Progress != nil {
			src = &progressReader{r: src, done: have, total: f.Size, label: c.progressLabel, fn: c.Progress}
		}
		_, err = io.Copy(out, src)
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
	return findRoot(stage, want)
}

// NativeApp is the bundle inside the native package zip (GodsWar-Native/exe-port/build-dist.sh).
const NativeApp = "GodsWar Native.app"

func findRoot(stage, want string) (string, error) {
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
	c.News = m.News
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
		c.progressLabel = "Downloading launcher " + lf.Version
		os.Remove(dest)
		if err := c.Download(lf, dest); err != nil {
			c.logf("launcher update failed, continuing with this one: %v", err)
		} else {
			os.Chmod(dest, 0o755)
			st.LauncherVersion, st.LauncherSHA256, st.LauncherPath = lf.Version, strings.ToLower(lf.SHA256), dest
			removeOldLaunchers(filepath.Dir(dest), dest)
		}
	}
	if c.Engine == EngineNative && c.Platform == "mac" {
		nf, ok := m.Native[c.Platform]
		if !ok {
			return "", errors.New("this server does not offer the Native engine yet; hold Option while opening GodsWar and choose Wine")
		}
		root, ver, err := c.install(nf, "native", st.NativeRoot, st.NativeVersion, func(d string) (string, error) { return findRoot(d, NativeApp) })
		if err != nil {
			return "", err
		}
		st.NativeRoot, st.NativeVersion = root, ver
	} else {
		cf, ok := m.Client[c.Platform]
		if !ok {
			return "", fmt.Errorf("no %s client in the manifest", c.Platform)
		}
		c.patchFirst(cf, &st)
		root, ver, err := c.install(cf, "client", st.ClientRoot, st.ClientVersion, func(d string) (string, error) { return findClientRoot(d, c.Platform) })
		if err != nil {
			return "", err
		}
		st.ClientRoot, st.ClientVersion = root, ver
		c.markRelease(cf, st)
	}
	if c.Engine != "" {
		st.Engine = c.Engine
	}
	st.Serial = m.Serial
	if err := writeState(c.DataDir, st); err != nil {
		return "", err
	}
	return st.Root(c.Engine), nil
}

// install brings one package (Wine client or Native app) to version f.Version in DataDir/<dir> and returns its root.
// The old copy is kept until the new one is unpacked and checked, so a failed update leaves a playable game.
func (c *Config) install(f File, dir, haveRoot, haveVer string, find func(string) (string, error)) (string, string, error) {
	installed := haveRoot != "" && haveVer != ""
	if installed {
		if _, err := os.Stat(haveRoot); err != nil {
			installed = false
		}
	}
	if installed && CompareVersions(f.Version, haveVer) <= 0 {
		return haveRoot, haveVer, nil
	}
	if !installed && c.OnFirstInstall != nil {
		c.OnFirstInstall(f.Size >> 20)
	}
	c.logf("downloading %s %s (%d MB)", dir, f.Version, f.Size>>20)
	c.progressLabel = "Downloading game " + f.Version
	if dir == "native" {
		c.progressLabel = "Downloading GodsWar Native " + f.Version
	}
	zipPath := filepath.Join(c.DataDir, "downloads", filepath.Base(f.URL))
	if err := c.Download(f, zipPath); err != nil {
		return "", "", err
	}
	stage := filepath.Join(c.DataDir, dir+"-new")
	os.RemoveAll(stage)
	c.logf("unpacking")
	stop := c.unpackProgress(zipPath, stage)
	err := extract(zipPath, stage)
	stop()
	if err != nil {
		os.RemoveAll(stage)
		return "", "", err
	}
	if _, err := find(stage); err != nil {
		os.RemoveAll(stage)
		return "", "", err
	}
	live := filepath.Join(c.DataDir, dir)
	old := filepath.Join(c.DataDir, dir+"-old")
	os.RemoveAll(old)
	if _, err := os.Stat(live); err == nil {
		if err := os.Rename(live, old); err != nil {
			return "", "", err
		}
	}
	if err := os.Rename(stage, live); err != nil {
		os.Rename(old, live)
		return "", "", err
	}
	root, _ := find(live)
	os.RemoveAll(old)
	os.Remove(zipPath)
	return root, f.Version, nil
}

// patchFirst (Windows): when the installed client is older than the manifest's, try the signed update channel first.
// On success the client counts as cf.Version, so install() has nothing to download; on any problem install()
// downloads the full zip as before. A running game (its files are locked on Windows) skips patching.
func (c *Config) patchFirst(cf File, st *State) {
	if c.Platform != "windows" || c.NoPatch || cf.Release < 1 || st.ClientRoot == "" || st.ClientVersion == "" {
		return
	}
	if CompareVersions(cf.Version, st.ClientVersion) <= 0 {
		return
	}
	if _, err := os.Stat(filepath.Join(st.ClientRoot, "GodsWar.exe")); err != nil {
		return
	}
	cur := InstalledRelease(st.ClientRoot)
	if cur < 1 {
		c.logf("installed client has no release number; downloading the full client")
		return
	}
	if gameRunning(st.ClientRoot) {
		c.logf("GodsWar is running; close it to update")
		return
	}
	got, err := c.PatchClient(st.ClientRoot, cur, cf.Release)
	if err != nil {
		c.logf("in-place update stopped (%v); downloading the full client instead", err)
		return
	}
	if got == cf.Release {
		c.logf("client updated in place to %s (release %d)", cf.Version, got)
		st.ClientVersion = cf.Version
		return
	}
	c.logf("update channel reached release %d, not %d; downloading the full client instead", got, cf.Release)
}

// markRelease records the channel release of a client that equals the manifest's (after a full install, or for a
// 1.0.2-era install of the current version, which has no marker), so the next release can patch it in place.
func (c *Config) markRelease(cf File, st State) {
	if c.Platform != "windows" || cf.Release < 1 || st.ClientRoot == "" || st.ClientVersion != cf.Version {
		return
	}
	if InstalledRelease(st.ClientRoot) == cf.Release {
		return
	}
	if err := writeRelease(st.ClientRoot, cf.Release); err != nil {
		c.logf("could not record the client release: %v", err)
	}
}

// gameRunning: a running GodsWar.exe cannot be opened for writing on Windows.
func gameRunning(root string) bool {
	f, err := os.OpenFile(filepath.Join(root, "GodsWar.exe"), os.O_RDWR, 0)
	if err != nil {
		return !os.IsNotExist(err)
	}
	f.Close()
	return false
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

func Launch(root, platform, engine string) error {
	if platform == "mac" {
		app := "GodsWar.app"
		if engine == EngineNative {
			app = NativeApp
		}
		return exec.Command("/usr/bin/open", filepath.Join(root, app)).Start()
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

// progressReader reports download progress at most every 100 ms.
type progressReader struct {
	r           io.Reader
	done, total int64
	label       string
	fn          func(string, int64, int64)
	last        time.Time
}

func (p *progressReader) Read(b []byte) (int, error) {
	n, err := p.r.Read(b)
	p.done += int64(n)
	if time.Since(p.last) > 100*time.Millisecond || err != nil {
		p.last = time.Now()
		p.fn(p.label, p.done, p.total)
	}
	return n, err
}

// unpackProgress reports how much of the zip has been unpacked (by the size written so far) until stop is called.
func (c *Config) unpackProgress(zipPath, stage string) (stop func()) {
	if c.Progress == nil {
		return func() {}
	}
	var total int64
	if zr, err := zip.OpenReader(zipPath); err == nil {
		for _, f := range zr.File {
			total += int64(f.UncompressedSize64)
		}
		zr.Close()
	}
	done := make(chan struct{})
	finished := make(chan struct{})
	go func() {
		defer close(finished)
		t := time.NewTicker(time.Second)
		defer t.Stop()
		for {
			var n int64
			filepath.WalkDir(stage, func(_ string, d os.DirEntry, err error) error {
				if err == nil && !d.IsDir() {
					if fi, err := d.Info(); err == nil {
						n += fi.Size()
					}
				}
				return nil
			})
			if n > total {
				n = total
			}
			c.Progress("Unpacking the game", n, total)
			select {
			case <-done:
				return
			case <-t.C:
			}
		}
	}()
	return func() { close(done); <-finished }
}
