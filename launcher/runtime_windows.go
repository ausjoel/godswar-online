//go:build windows

package main

// GodsWar.exe's FB.dll and KMMonitor.dll need the Microsoft VC80 CRT (msvcp80/msvcr80 8.0.50727.762 or a newer
// servicing level) as a shared side-by-side assembly. The launcher carries Microsoft's own "Visual C++ 2005 SP1
// Redistributable Package MFC Security Update" (x86) and runs it quietly when no such assembly is installed:
// one Windows (UAC) prompt, once per PC. Console d117 o1; Development/windows-test-2026-11-22/README.md.

import (
	"crypto/sha256"
	_ "embed"
	"encoding/hex"
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"syscall"
	"unsafe"
)

//go:embed vcredist/vcredist_x86.exe
var vcredist []byte

// VCRedistSHA256 is the file Microsoft publishes at download.microsoft.com/download/8/b/4/
// 8b42259f-5d70-43f4-ac2e-4b208fd8d66a/vcredist_x86.EXE (2,710,520 bytes, Authenticode-signed by Microsoft).
const VCRedistSHA256 = "8648c5fc29c44b9112fe52f9a33f80e7fc42d10f3b5b42b2121542a13e44adfd"

const minCRTBuild = 762

const vcredistArgs = `/q:a /c:"msiexec /i vcredist.msi /qn /norestart"`

var crtDir = regexp.MustCompile(`(?i)^x86_microsoft\.vc80\.crt_1fc8b3b9a1e18e3b_8\.0\.50727\.(\d+)_`)

// HasVC80CRT: an x86 VC80 CRT assembly of build 762 or newer with msvcp80.dll in WinSxS.
func HasVC80CRT() bool {
	sxs := filepath.Join(os.Getenv("WINDIR"), "WinSxS")
	ents, err := os.ReadDir(sxs)
	if err != nil {
		return false
	}
	for _, e := range ents {
		m := crtDir.FindStringSubmatch(e.Name())
		if m == nil || !e.IsDir() {
			continue
		}
		if b, _ := strconv.Atoi(m[1]); b < minCRTBuild {
			continue
		}
		if _, err := os.Stat(filepath.Join(sxs, e.Name(), "msvcp80.dll")); err == nil {
			return true
		}
	}
	return false
}

type shellExecuteInfo struct {
	cbSize       uint32
	fMask        uint32
	hwnd         uintptr
	lpVerb       *uint16
	lpFile       *uint16
	lpParameters *uint16
	lpDirectory  *uint16
	nShow        int32
	hInstApp     uintptr
	lpIDList     uintptr
	lpClass      *uint16
	hkeyClass    uintptr
	dwHotKey     uint32
	hIcon        uintptr
	hProcess     syscall.Handle
}

var procShellExecuteExW = syscall.NewLazyDLL("shell32.dll").NewProc("ShellExecuteExW")

var errDeclined = errors.New("the Windows prompt was declined")

// runElevated starts file with args through the UAC "runas" verb and waits; it returns the exit code.
func runElevated(file, args string) (uint32, error) {
	verb, _ := syscall.UTF16PtrFromString("runas")
	f, _ := syscall.UTF16PtrFromString(file)
	a, _ := syscall.UTF16PtrFromString(args)
	sei := shellExecuteInfo{fMask: 0x40 | 0x100, lpVerb: verb, lpFile: f, lpParameters: a, nShow: 0} // NOCLOSEPROCESS | NOASYNC, SW_HIDE
	sei.cbSize = uint32(unsafe.Sizeof(sei))
	r, _, err := procShellExecuteExW.Call(uintptr(unsafe.Pointer(&sei)))
	if r == 0 {
		if errno, ok := err.(syscall.Errno); ok && errno == 1223 { // ERROR_CANCELLED
			return 0, errDeclined
		}
		return 0, err
	}
	if sei.hProcess == 0 {
		return 0, errors.New("the installer did not start")
	}
	defer syscall.CloseHandle(sei.hProcess)
	syscall.WaitForSingleObject(sei.hProcess, syscall.INFINITE)
	var code uint32
	syscall.GetExitCodeProcess(sei.hProcess, &code)
	return code, nil
}

// ensureRuntime installs the VC80 CRT when it is missing (or always, with force). It never stops the game from
// starting: on a problem it tells the player what to install.
func ensureRuntime(c *Config, force bool) {
	if !force && HasVC80CRT() {
		return
	}
	sum := sha256.Sum256(vcredist)
	if hex.EncodeToString(sum[:]) != VCRedistSHA256 {
		c.logf("the bundled Visual C++ runtime installer is damaged; not running it")
		return
	}
	info(c, "GodsWar needs the Microsoft Visual C++ 2005 runtime. Windows will ask for permission once to install it.")
	dir := filepath.Join(c.DataDir, "runtime")
	os.MkdirAll(dir, 0o755)
	exe := filepath.Join(dir, "vcredist_x86.exe")
	if err := os.WriteFile(exe, vcredist, 0o755); err != nil {
		c.logf("could not prepare the Visual C++ runtime installer: %v", err)
		return
	}
	defer os.Remove(exe)
	// IExpress package: /q:a quiet, /c: replaces its command with a silent per-machine MSI install.
	code, err := runElevated(exe, vcredistArgs)
	switch {
	case err == errDeclined:
		info(c, runtimeHelp)
	case err != nil:
		c.logf("the Visual C++ runtime installer did not run: %v", err)
		info(c, runtimeHelp)
	case code != 0 && code != 3010:
		c.logf("the Visual C++ runtime installer ended with code %d", code)
		info(c, runtimeHelp)
	case !HasVC80CRT():
		c.logf("the Visual C++ runtime installer finished (code %d) but the runtime is still missing", code)
		info(c, runtimeHelp)
	default:
		c.logf("Visual C++ 2005 runtime installed (code %d)", code)
	}
}

const runtimeHelp = "If GodsWar does not start, install \"Microsoft Visual C++ 2005 Service Pack 1 Redistributable Package MFC Security Update\" (x86) from microsoft.com, then open GodsWar again."
