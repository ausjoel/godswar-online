package main

// In-place client patching (launcher 1.0.3+, Windows; review item #14 / console d118). Same rules as the Mac
// gwupdate helper (Launcher/gwupdate/main.swift) and Launcher/patcher.py:
//   - the update channel (updates/stable/) holds latest.json (a hint only) and one folder per release N with
//     manifest.json, manifest.sig (raw Ed25519 over manifest.json, same key as the downloads manifest) and payload.zip;
//   - a release applies only when its signed base_version is the installed release (Client/.client-release);
//   - schema 2 lists the exact sha256 (or absence) every touched file must have before the update;
//   - files come out of payload.zip only by names the signed manifest lists, each checked against its sha256,
//     and every file is unpacked and checked before the first file in the client changes.
// Windows rule: the channel is built from the Mac Client tree. The Windows zip never ships UIScale/ (Mac-only
// compact-layout XML) or .client-release (the launcher writes it), so those entries are skipped on Windows.
// When anything does not fit (unknown release, broken chain, changed local file) the caller falls back to the full zip.

import (
	"archive/zip"
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
)

const DefaultChannelURL = "https://YOUR-DOMAIN.example/updates/stable/"

const (
	maxChanManifest = 2_000_000
	maxChanPayload  = 2_000_000_000
	maxChanStep     = 200
)

const releaseMarker = ".client-release"

type chanEntry struct {
	Path   string `json:"path"`
	Size   int64  `json:"size"`
	SHA256 string `json:"sha256"`
}

type chanManifest struct {
	Schema  int                `json:"schema"`
	Version int                `json:"version"`
	Base    int                `json:"base_version"`
	Channel string             `json:"channel"`
	Files   []chanEntry        `json:"files"`
	Delete  []string           `json:"delete"`
	Before  map[string]*string `json:"before"`
}

var reservedName = regexp.MustCompile(`(?i)^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\.|$)`)

// SafeChannelPath is gwupdate's safePath: relative, forward slashes, no drive, no control or <>"|?* characters,
// no empty/./.. parts, no part ending in a space or dot, no reserved Windows device names.
func SafeChannelPath(name string) error {
	bad := name == "" || strings.HasPrefix(name, "/") || strings.ContainsAny(name, "\\:<>\"|?*")
	for _, r := range name {
		if r < 32 {
			bad = true
		}
	}
	if bad {
		return fmt.Errorf("unsafe path %s", name)
	}
	for _, part := range strings.Split(name, "/") {
		if part == "" || part == "." || part == ".." || strings.HasSuffix(part, " ") || strings.HasSuffix(part, ".") || reservedName.MatchString(part) {
			return fmt.Errorf("unsafe path %s", name)
		}
	}
	return nil
}

func parseChanManifest(raw, sig []byte, key ed25519.PublicKey) (*chanManifest, error) {
	if len(raw) > maxChanManifest {
		return nil, errors.New("manifest too large")
	}
	if !ed25519.Verify(key, raw, sig) {
		return nil, errors.New("invalid manifest signature")
	}
	var m chanManifest
	if err := json.Unmarshal(raw, &m); err != nil || (m.Schema != 1 && m.Schema != 2) || m.Version < 1 || m.Files == nil {
		return nil, errors.New("invalid manifest")
	}
	seen := map[string]bool{}
	var total int64
	for _, f := range m.Files {
		if f.Size < 0 || len(f.SHA256) != 64 || strings.Trim(f.SHA256, "0123456789abcdef") != "" {
			return nil, errors.New("invalid file entry")
		}
		if err := SafeChannelPath(f.Path); err != nil {
			return nil, err
		}
		k := strings.ToLower(f.Path)
		if seen[k] {
			return nil, fmt.Errorf("duplicate file %s", f.Path)
		}
		seen[k] = true
		total += f.Size
	}
	for _, d := range m.Delete {
		if err := SafeChannelPath(d); err != nil {
			return nil, err
		}
		k := strings.ToLower(d)
		if seen[k] {
			return nil, fmt.Errorf("conflicting delete %s", d)
		}
		seen[k] = true
	}
	if total > maxChanPayload {
		return nil, errors.New("payload too large")
	}
	if m.Schema == 2 {
		if m.Before == nil || len(m.Before) != len(m.Files)+len(m.Delete) {
			return nil, errors.New("baseline list differs")
		}
		for _, f := range m.Files {
			if _, ok := m.Before[f.Path]; !ok {
				return nil, errors.New("baseline list differs")
			}
		}
		for _, d := range m.Delete {
			if _, ok := m.Before[d]; !ok {
				return nil, errors.New("baseline list differs")
			}
		}
	} else {
		m.Before = nil
	}
	return &m, nil
}

// skipOnPlatform: entries the Windows client never carries (see the header).
func skipOnPlatform(platform, path string) bool {
	if platform != "windows" {
		return false
	}
	p := strings.ToLower(path)
	return p == releaseMarker || strings.HasPrefix(p, "uiscale/")
}

func isRegular(p string) (exists, regular bool) {
	st, err := os.Lstat(p)
	if err != nil {
		return false, false
	}
	return true, st.Mode().IsRegular()
}

// noLinks: a client path must not pass through a symlink (or, on Windows, a junction).
func noLinks(client, path string) error {
	at := client
	for _, part := range strings.Split(path, "/") {
		at = filepath.Join(at, part)
		if st, err := os.Lstat(at); err == nil && st.Mode()&(os.ModeSymlink|os.ModeIrregular) != 0 {
			return fmt.Errorf("link in client: %s", path)
		}
	}
	return nil
}

// checkBaseline: a file already at its target passes (finishes an interrupted run); otherwise the schema-2 baseline must hold.
func (c *Config) checkBaseline(m *chanManifest, client string) error {
	targets := map[string]string{}
	for _, f := range m.Files {
		targets[f.Path] = f.SHA256
	}
	paths := []string{}
	for _, f := range m.Files {
		paths = append(paths, f.Path)
	}
	paths = append(paths, m.Delete...)
	for _, p := range paths {
		if skipOnPlatform(c.Platform, p) {
			continue
		}
		if err := noLinks(client, p); err != nil {
			return err
		}
		full := filepath.Join(client, filepath.FromSlash(p))
		exists, regular := isRegular(full)
		if exists && !regular {
			return fmt.Errorf("not a file: %s", p)
		}
		t, isTarget := targets[p]
		if isTarget && exists {
			if sum, err := fileSHA256(full); err == nil && sum == t {
				continue
			}
		}
		if !isTarget && !exists {
			continue
		}
		if m.Before == nil {
			continue
		}
		want := m.Before[p]
		if want != nil {
			sum, err := fileSHA256(full)
			if !exists || err != nil || sum != *want {
				return fmt.Errorf("local file differs from this update's base: %s", p)
			}
		} else if exists {
			return fmt.Errorf("update expected no file at: %s", p)
		}
	}
	return nil
}

func (c *Config) applyRelease(m *chanManifest, payload, client, work string) error {
	if err := c.checkBaseline(m, client); err != nil {
		return err
	}
	zr, err := zip.OpenReader(payload)
	if err != nil {
		return fmt.Errorf("payload is not a zip: %v", err)
	}
	defer zr.Close()
	byName := map[string]*zip.File{}
	for _, f := range zr.File {
		byName[f.Name] = f
	}
	stage := filepath.Join(work, "stage-"+strconv.Itoa(m.Version))
	os.RemoveAll(stage)
	if err := os.MkdirAll(stage, 0o755); err != nil {
		return err
	}
	defer os.RemoveAll(stage)
	type staged struct {
		e   chanEntry
		tmp string
	}
	var list []staged
	for i, e := range m.Files {
		if skipOnPlatform(c.Platform, e.Path) {
			continue
		}
		zf, ok := byName[e.Path]
		if !ok || int64(zf.UncompressedSize64) != e.Size {
			return fmt.Errorf("zip differs from manifest: %s", e.Path)
		}
		rc, err := zf.Open()
		if err != nil {
			return fmt.Errorf("damaged zip entry %s: %v", e.Path, err)
		}
		tmp := filepath.Join(stage, strconv.Itoa(i))
		out, err := os.Create(tmp)
		if err != nil {
			rc.Close()
			return err
		}
		h := sha256.New()
		n, err := io.Copy(io.MultiWriter(out, h), io.LimitReader(rc, e.Size+1))
		rc.Close()
		out.Close()
		if err != nil {
			return fmt.Errorf("damaged zip entry %s: %v", e.Path, err)
		}
		if n != e.Size || hex.EncodeToString(h.Sum(nil)) != e.SHA256 {
			return fmt.Errorf("checksum mismatch: %s", e.Path)
		}
		list = append(list, staged{e, tmp})
	}
	for _, s := range list {
		target := filepath.Join(client, filepath.FromSlash(s.e.Path))
		if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil {
			return err
		}
		if err := os.Rename(s.tmp, target); err != nil {
			// Different volume: copy beside the target, then rename.
			near := filepath.Join(filepath.Dir(target), ".gwupdate-"+strconv.Itoa(os.Getpid()))
			if err := copyFile(s.tmp, near); err != nil {
				os.Remove(near)
				return fmt.Errorf("could not replace %s: %v", s.e.Path, err)
			}
			if err := os.Rename(near, target); err != nil {
				os.Remove(near)
				return fmt.Errorf("could not replace %s: %v", s.e.Path, err)
			}
		}
	}
	for _, d := range m.Delete {
		if skipOnPlatform(c.Platform, d) {
			continue
		}
		target := filepath.Join(client, filepath.FromSlash(d))
		if _, err := os.Lstat(target); err == nil {
			if err := os.Remove(target); err != nil {
				return err
			}
		}
	}
	return nil
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.Create(dst)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}

// InstalledRelease reads Client/.client-release (0 = unknown).
func InstalledRelease(client string) int {
	b, err := os.ReadFile(filepath.Join(client, releaseMarker))
	if err != nil {
		return 0
	}
	v, err := strconv.Atoi(strings.TrimSpace(string(b)))
	if err != nil || v < 1 {
		return 0
	}
	return v
}

func writeRelease(client string, rel int) error {
	tmp := filepath.Join(client, releaseMarker+".tmp")
	if err := os.WriteFile(tmp, []byte(strconv.Itoa(rel)+"\n"), 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, filepath.Join(client, releaseMarker))
}

// fetchBounded returns nil, nil for a missing file (HTTP 404/403 or no such file).
func (c *Config) fetchBounded(url string, limit int64) ([]byte, error) {
	resp, err := c.get(url, nil)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode == 404 || resp.StatusCode == 403 {
		return nil, nil
	}
	if resp.StatusCode != 200 {
		return nil, fmt.Errorf("%s: HTTP %d", url, resp.StatusCode)
	}
	b, err := io.ReadAll(io.LimitReader(resp.Body, limit+1))
	if err != nil {
		return nil, err
	}
	if int64(len(b)) > limit {
		return nil, fmt.Errorf("%s is too large", url)
	}
	return b, nil
}

// fetchPayload downloads payload.zip with resume and a size cap; the per-file sha256 checks are the integrity check.
func (c *Config) fetchPayload(url, dest string, limit int64) error {
	have := int64(0)
	if st, err := os.Stat(dest); err == nil {
		have = st.Size()
	}
	if have > limit {
		os.Remove(dest)
		have = 0
	}
	hdr := map[string]string{}
	if have > 0 {
		hdr["Range"] = fmt.Sprintf("bytes=%d-", have)
	}
	resp, err := c.get(url, hdr)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	flags := os.O_CREATE | os.O_WRONLY
	switch {
	case resp.StatusCode == 206 && have > 0:
		flags |= os.O_APPEND
	case resp.StatusCode == 416 && have > 0:
		return nil // already complete
	case resp.StatusCode == 200:
		flags |= os.O_TRUNC
		have = 0
	default:
		return fmt.Errorf("%s: HTTP %d", url, resp.StatusCode)
	}
	out, err := os.OpenFile(dest, flags, 0o644)
	if err != nil {
		return err
	}
	n, err := io.Copy(out, io.LimitReader(&idleReader{r: resp.Body, d: 60e9}, limit-have+1))
	out.Close()
	if err != nil {
		return err
	}
	if have+n > limit {
		os.Remove(dest)
		return errors.New("payload larger than its manifest allows")
	}
	return nil
}

// PatchClient brings the client at root from its installed release up to want (the signed downloads manifest's
// release for this platform's client). It returns the release reached; the caller compares it with want.
// An error means the folder still holds a playable client (every file it had, or a valid mix the next run finishes).
func (c *Config) PatchClient(root string, current, want int) (int, error) {
	if current < 1 || want <= current {
		return current, nil
	}
	if want-current > maxChanStep {
		return current, fmt.Errorf("implausible release step %d -> %d", current, want)
	}
	base := c.ChannelURL
	if base == "" {
		base = DefaultChannelURL
	}
	if !strings.HasSuffix(base, "/") {
		base += "/"
	}
	work := filepath.Join(c.DataDir, "updates")
	if err := os.MkdirAll(work, 0o755); err != nil {
		return current, err
	}
	for cand := current + 1; cand <= want; {
		folder := base + strconv.Itoa(cand) + "/"
		raw, err := c.fetchBounded(folder+"manifest.json", maxChanManifest)
		if err != nil {
			return current, err
		}
		if raw == nil {
			cand++
			continue
		}
		sig, err := c.fetchBounded(folder+"manifest.sig", 4096)
		if err != nil || sig == nil {
			return current, fmt.Errorf("release %d has no signature", cand)
		}
		m, err := parseChanManifest(raw, sig, c.PubKey)
		if err != nil {
			return current, fmt.Errorf("release %d: %v", cand, err)
		}
		if m.Channel != "stable" {
			return current, errors.New("wrong update channel")
		}
		if m.Version != cand {
			return current, errors.New("manifest version differs from its folder")
		}
		if m.Base != current {
			cand++
			continue
		}
		if err := c.checkBaseline(m, root); err != nil {
			return current, err
		}
		var total int64
		for _, f := range m.Files {
			total += f.Size
		}
		limit := total + total/8 + 1<<20 + int64(len(m.Files))*1024
		payload := filepath.Join(work, "payload-"+strconv.Itoa(m.Version)+".zip")
		c.logf("downloading update %d (%d files)", m.Version, len(m.Files))
		if err := c.fetchPayload(folder+"payload.zip", payload, limit); err != nil {
			return current, fmt.Errorf("download failed for release %d (it resumes next time): %v", m.Version, err)
		}
		if err := c.applyRelease(m, payload, root, work); err != nil {
			if strings.HasPrefix(err.Error(), "checksum") || strings.Contains(err.Error(), "zip") {
				os.Remove(payload)
			}
			return current, err
		}
		if err := writeRelease(root, m.Version); err != nil {
			return current, err
		}
		os.Remove(payload)
		c.logf("installed update %d", m.Version)
		current = m.Version
		cand = current + 1
	}
	return current, nil
}
