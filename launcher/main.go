package main

import (
	"crypto/ed25519"
	"encoding/base64"
	"flag"
	"fmt"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"syscall"
	"time"
)

func dataDir() string {
	if runtime.GOOS == "windows" {
		return filepath.Join(os.Getenv("LOCALAPPDATA"), "GodsWar")
	}
	h, _ := os.UserHomeDir()
	return filepath.Join(h, "Library", "Application Support", "GodsWar Launcher")
}

func main() {
	noLaunch := flag.Bool("no-launch", false, "download and install only")
	manifest := flag.String("manifest-url", DefaultManifestURL, "manifest address (testing)")
	flag.Parse()
	key, _ := base64.StdEncoding.DecodeString(PublicKey)
	if len(key) != ed25519.PublicKeySize || DefaultManifestURL == "" {
		fmt.Println("This launcher was built without its update key or manifest address; rebuild with launcher/build.sh (see launcher/README.md).")
		os.Exit(1)
	}
	cfg := &Config{
		ManifestURL: *manifest, PubKey: ed25519.PublicKey(key), DataDir: dataDir(),
		Platform: PlatformName(), Version: Version, NoLaunch: *noLaunch,
		HTTP: &http.Client{Timeout: 0}, Logf: func(f string, a ...any) { fmt.Printf(f+"\n", a...) },
	}
	os.MkdirAll(cfg.DataDir, 0o755)
	if p, ok := DelegateTarget(cfg); ok {
		os.Setenv("GW_LAUNCHER_DELEGATED", "1")
		if runtime.GOOS == "windows" {
			cmd := exec.Command(p, os.Args[1:]...)
			cmd.Stdout, cmd.Stderr, cmd.Stdin = os.Stdout, os.Stderr, os.Stdin
			if err := cmd.Run(); err == nil {
				return
			}
		} else if err := syscall.Exec(p, append([]string{p}, os.Args[1:]...), os.Environ()); err == nil {
			return
		}
		// fall through: run this copy
	}
	if !acquireLock() {
		notify(cfg, "GodsWar is already running or downloading. Please wait for it to finish.")
		return
	}
	if !cfg.NoLaunch {
		if msg := rosettaProblem(); msg != "" {
			fail(cfg, msg)
		}
	}
	cfg.OnFirstInstall = func(mb int64) {
		info(cfg, fmt.Sprintf("GodsWar will now download the game (about %d MB). This can take several minutes. The game opens by itself when it is ready.", mb))
	}
	notify(cfg, "GodsWar is checking for updates...")
	root, err := Update(cfg)
	if err != nil {
		// A client that is already installed can still be played when the VPS is unreachable.
		st := readState(cfg.DataDir)
		if st.ClientRoot == "" {
			fail(cfg, "Could not download GodsWar: "+err.Error())
		}
		cfg.logf("update check failed, starting the installed client: %v", err)
		root = st.ClientRoot
	}
	if cfg.NoLaunch {
		fmt.Println("installed at", root)
		return
	}
	if err := Launch(root, cfg.Platform); err != nil {
		fail(cfg, "Could not start GodsWar: "+err.Error())
	}
	time.Sleep(500 * time.Millisecond)
}

var lockListener net.Listener

// acquireLock allows one launcher at a time; the port is freed by the OS if the process dies.
func acquireLock() bool {
	l, err := net.Listen("tcp", "127.0.0.1:47321")
	if err != nil {
		return false
	}
	lockListener = l
	return true
}
