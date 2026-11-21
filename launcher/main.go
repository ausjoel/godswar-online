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
	engineFlag := flag.String("engine", "", "wine or native (Mac): choose the engine without the dialog and remember it")
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
	cfg.Engine = chooseEngine(cfg, readState(cfg.DataDir).Engine, *engineFlag)
	if !cfg.NoLaunch && cfg.Engine == EngineWine {
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
		root = readState(cfg.DataDir).Root(cfg.Engine)
		if root == "" {
			fail(cfg, "Could not download GodsWar: "+err.Error())
		}
		cfg.logf("update check failed, starting the installed client: %v", err)
	}
	if cfg.NoLaunch {
		fmt.Println("installed at", root)
		return
	}
	if err := Launch(root, cfg.Platform, cfg.Engine); err != nil {
		fail(cfg, "Could not start GodsWar: "+err.Error())
	}
	time.Sleep(500 * time.Millisecond)
}

// chooseEngine: Windows always runs GodsWar.exe (wine). On a Mac the -engine flag wins; otherwise the saved choice,
// asked on the first start and again whenever Option is held while GodsWar opens (review console d11 = ask).
func chooseEngine(cfg *Config, saved, flagValue string) string {
	if cfg.Platform != "mac" {
		return EngineWine
	}
	e := saved
	switch {
	case flagValue == EngineWine || flagValue == EngineNative:
		e = flagValue
	case saved == "" || optionHeld():
		e = askEngine(saved)
	}
	if e != EngineNative {
		e = EngineWine
	}
	if e != saved { // remember it now, so a failed first download does not ask again
		st := readState(cfg.DataDir)
		st.Engine = e
		writeState(cfg.DataDir, st)
	}
	return e
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
