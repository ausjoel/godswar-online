// gwpublish builds and signs downloads/manifest.json from files in a folder.
// The private key stays on the publisher's Mac and is never uploaded.
package main

import (
	"crypto/ed25519"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"time"
)

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

func entry(base, path, ver string) File {
	f, err := os.Open(path)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	defer f.Close()
	h := sha256.New()
	n, _ := io.Copy(h, f)
	return File{Version: ver, URL: base + filepath.Base(path), Size: n, SHA256: hex.EncodeToString(h.Sum(nil))}
}

func main() {
	keyPath := flag.String("key", "", "Ed25519 private key (PEM, PKCS8)")
	dir := flag.String("dir", ".", "folder holding the files and receiving manifest.json")
	base := flag.String("base-url", os.Getenv("GW_DOWNLOADS_BASE_URL"), "public folder URL (or set GW_DOWNLOADS_BASE_URL)")
	macClient := flag.String("mac-client", "", "mac client zip (file name inside -dir)")
	winClient := flag.String("windows-client", "", "windows client zip")
	clientVer := flag.String("client-version", "", "client version for both")
	macLauncher := flag.String("mac-launcher", "", "mac launcher binary")
	winLauncher := flag.String("windows-launcher", "", "windows launcher exe")
	launcherVer := flag.String("launcher-version", "", "launcher version")
	flag.Parse()
	raw, err := os.ReadFile(*keyPath)
	if err != nil {
		panic(err)
	}
	blk, _ := pem.Decode(raw)
	k, err := x509.ParsePKCS8PrivateKey(blk.Bytes)
	if err != nil {
		panic(err)
	}
	priv := k.(ed25519.PrivateKey)
	m := Manifest{Serial: time.Now().Unix(), Launcher: map[string]File{}, Client: map[string]File{}}
	add := func(mm map[string]File, plat, name, ver string) {
		if name != "" {
			mm[plat] = entry(*base, filepath.Join(*dir, name), ver)
		}
	}
	add(m.Client, "mac", *macClient, *clientVer)
	add(m.Client, "windows", *winClient, *clientVer)
	add(m.Launcher, "mac", *macLauncher, *launcherVer)
	add(m.Launcher, "windows", *winLauncher, *launcherVer)
	b, _ := json.Marshal(m)
	sig := ed25519.Sign(priv, b)
	os.WriteFile(filepath.Join(*dir, "manifest.json"), b, 0o644)
	os.WriteFile(filepath.Join(*dir, "manifest.json.sig"), []byte(base64.StdEncoding.EncodeToString(sig)+"\n"), 0o644)
	fmt.Printf("manifest serial %d written\n", m.Serial)
}
