package main

import (
	"crypto/ed25519"
	"encoding/base64"
	"flag"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
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
	channel := flag.String("channel-url", DefaultChannelURL, "update channel address (testing)")
	allowFile := flag.Bool("allow-file-urls", false, "accept file:// manifest, channel and download addresses (testing)")
	forceRuntime := flag.Bool("force-runtime", false, "Windows: run the Visual C++ 2005 runtime installer even when the runtime is present (testing)")
	noPatch := flag.Bool("no-patch", false, "update the client with the full zip only")
	showNews := flag.Bool("show-news", false, "show the What's new box even with --no-launch (testing)")
	engineFlag := flag.String("engine", "", "wine or native (Mac): choose the engine without the dialog and remember it")
	noGUI := flag.Bool("no-gui", false, "use the 1.0.4 dialogs instead of the launcher window")
	snapshot := flag.String("ui-snapshot", "", "write PNGs of the launcher window's states into this folder and exit (testing)")
	flag.Parse()
	if *snapshot != "" {
		if err := writeSnapshots(*snapshot); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		return
	}
	key, _ := base64.StdEncoding.DecodeString(PublicKey)
	if len(key) != ed25519.PublicKeySize || DefaultManifestURL == "" {
		fmt.Println("This launcher was built without its update key or manifest address; rebuild with launcher/build.sh (see launcher/README.md).")
		os.Exit(1)
	}
	cfg := &Config{
		ManifestURL: *manifest, PubKey: ed25519.PublicKey(key), DataDir: dataDir(),
		Platform: PlatformName(), Version: Version, NoLaunch: *noLaunch,
		ChannelURL: *channel, NoPatch: *noPatch,
		HTTP: httpClient(*allowFile), Logf: func(f string, a ...any) { fmt.Printf(f+"\n", a...) },
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
	// The launcher window (1.0.5+) for a normal start; the dialogs stay for tests, scripts and systems without a window.
	engineAsked := false
	if !*noLaunch && !*showNews && *engineFlag == "" && !*noGUI && os.Getenv("GW_NO_GUI") == "" {
		cfg.Logf = guiLog(cfg.DataDir)
		// Mac: Wine or Native is still asked on the first start and when Option is held (review d11, d146 keep it).
		cfg.Engine = chooseEngine(cfg, readState(cfg.DataDir).Engine, "")
		engineAsked = true
		err := runGUI(cfg)
		if err == nil {
			return
		}
		cfg.Status, cfg.Progress = nil, nil
		cfg.logf("no launcher window (%v); using the dialogs", err)
	}
	if !engineAsked { // the window path already asked (or used the saved choice); never ask twice
		cfg.Engine = chooseEngine(cfg, readState(cfg.DataDir).Engine, *engineFlag)
	}
	if !cfg.NoLaunch && cfg.Engine == EngineWine {
		if msg := rosettaProblem(); msg != "" {
			fail(cfg, msg)
		}
	}
	cfg.OnFirstInstall = func(mb int64) {
		info(cfg, fmt.Sprintf("GodsWar will now download the game (about %d MB). This can take several minutes. The game opens by itself when it is ready.", mb))
	}
	notify(cfg, "GodsWar is checking for updates...")
	hadClient := readState(cfg.DataDir).Root(cfg.Engine) != ""
	root, err := Update(cfg)
	if err != nil {
		// A client that is already installed can still be played when the VPS is unreachable.
		root = readState(cfg.DataDir).Root(cfg.Engine)
		if root == "" {
			fail(cfg, "Could not download GodsWar: "+err.Error())
		}
		cfg.logf("update check failed, starting the installed client: %v", err)
	}
	ensureRuntime(cfg, *forceRuntime)
	if !cfg.NoLaunch || *showNews {
		whatsNew(cfg, !hadClient && !*showNews)
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

// httpClient: https only, plus file:// when testing (a local signed manifest, channel and zips; trust is unchanged,
// everything is still checked against the compiled-in key and the signed hashes).
func httpClient(allowFile bool) *http.Client {
	t := http.DefaultTransport.(*http.Transport).Clone()
	if allowFile {
		t.RegisterProtocol("file", fileTransport{})
	}
	return &http.Client{Timeout: 0, Transport: t}
}

type fileTransport struct{}

func (fileTransport) RoundTrip(r *http.Request) (*http.Response, error) {
	p := r.URL.Path
	if len(p) > 2 && p[0] == '/' && p[2] == ':' { // file:///C:/x on Windows
		p = p[1:]
	}
	resp := &http.Response{Request: r, Header: http.Header{}, Body: http.NoBody, StatusCode: 404}
	f, err := os.Open(filepath.FromSlash(p))
	if err != nil {
		return resp, nil
	}
	st, _ := f.Stat()
	var off int64
	if rg := r.Header.Get("Range"); strings.HasPrefix(rg, "bytes=") {
		off, _ = strconv.ParseInt(strings.TrimSuffix(strings.TrimPrefix(rg, "bytes="), "-"), 10, 64)
		if off >= st.Size() {
			f.Close()
			resp.StatusCode = 416
			return resp, nil
		}
		f.Seek(off, io.SeekStart)
		resp.StatusCode = 206
	} else {
		resp.StatusCode = 200
	}
	resp.Body, resp.ContentLength = f, st.Size()-off
	return resp, nil
}
