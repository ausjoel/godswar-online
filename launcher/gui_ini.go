package main

import (
	"bytes"
	"os"
	"os/exec"
	"runtime"
	"strings"
	"syscall"
)

// iniValue reads key in [section] of an ini file ("" when missing). Case-insensitive, like the client.
func iniValue(path, section, key string) string {
	b, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	in := false
	for _, l := range strings.Split(strings.ReplaceAll(string(b), "\r\n", "\n"), "\n") {
		t := strings.TrimSpace(l)
		if strings.HasPrefix(t, "[") && strings.HasSuffix(t, "]") {
			in = strings.EqualFold(t[1:len(t)-1], section)
			continue
		}
		if k, v, ok := strings.Cut(t, "="); in && ok && strings.EqualFold(strings.TrimSpace(k), key) {
			return strings.TrimSpace(v)
		}
	}
	return ""
}

// setIniValue sets key in [section], keeping every other line and the file's line endings. A missing key is added
// at the end of the section, a missing section at the end of the file. Nothing is written when the value is unchanged.
func setIniValue(path, section, key, value string) error {
	b, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	nl := "\n"
	if bytes.Contains(b, []byte("\r\n")) {
		nl = "\r\n"
	}
	lines := strings.Split(strings.ReplaceAll(string(b), "\r\n", "\n"), "\n")
	in, found, secEnd := false, false, -1
	for i, l := range lines {
		t := strings.TrimSpace(l)
		if strings.HasPrefix(t, "[") && strings.HasSuffix(t, "]") {
			if in && secEnd < 0 {
				secEnd = i
			}
			in = strings.EqualFold(t[1:len(t)-1], section)
			continue
		}
		if k, v, ok := strings.Cut(t, "="); in && ok && strings.EqualFold(strings.TrimSpace(k), key) {
			if strings.TrimSpace(v) == value {
				return nil
			}
			lines[i] = strings.TrimSpace(k) + "=" + value
			found = true
			break
		}
	}
	if !found {
		if in && secEnd < 0 {
			secEnd = len(lines)
		}
		if secEnd >= 0 {
			// insert after the section's last non-empty line
			at := secEnd
			for at > 0 && strings.TrimSpace(lines[at-1]) == "" {
				at--
			}
			lines = append(lines[:at], append([]string{key + "=" + value}, lines[at:]...)...)
		} else {
			lines = append(lines, "["+section+"]", key+"="+value)
		}
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, []byte(strings.Join(lines, nl)), 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

// restartLauncher starts the newest stored launcher copy (the hand-over that normally happens at start-up) and exits.
func restartLauncher(c *Config) {
	p, ok := DelegateTarget(c)
	if !ok {
		return
	}
	if lockListener != nil {
		lockListener.Close()
	}
	if runtime.GOOS == "darwin" {
		// Same process: the Dock keeps showing the GodsWar app, as at the start-up hand-over.
		syscall.Exec(p, append([]string{p}, os.Args[1:]...), os.Environ())
	}
	cmd := exec.Command(p, os.Args[1:]...) // keep test flags (a normal start has none)
	if err := cmd.Start(); err != nil {
		c.logf("could not start the new launcher: %v", err)
		return
	}
	os.Exit(0)
}
