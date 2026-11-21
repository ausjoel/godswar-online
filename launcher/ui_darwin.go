package main

import (
	"fmt"
	"os"
	"os/exec"
	"runtime"
	"strings"
)

func esc(s string) string { return strings.NewReplacer(`\`, `\\`, `"`, `\"`).Replace(s) }

func notify(c *Config, msg string) {
	fmt.Println(msg)
	exec.Command("/usr/bin/osascript", "-e", fmt.Sprintf(`display notification "%s" with title "GodsWar"`, esc(msg))).Run()
}

func fail(c *Config, msg string) {
	fmt.Fprintln(os.Stderr, msg)
	exec.Command("/usr/bin/osascript", "-e", fmt.Sprintf(`display dialog "%s" with title "GodsWar" buttons {"OK"} default button 1 with icon stop`, esc(msg))).Run()
	os.Exit(1)
}

func info(c *Config, msg string) {
	fmt.Println(msg)
	exec.Command("/usr/bin/osascript", "-e", fmt.Sprintf(`display dialog "%s" with title "GodsWar" buttons {"OK"} default button 1 giving up after 20`, esc(msg))).Run()
}

// rosettaProblem: the bundled Wine is x86_64, so an Apple-chip Mac needs Rosetta.
func rosettaProblem() string {
	if runtime.GOARCH != "arm64" {
		return ""
	}
	if exec.Command("/usr/bin/arch", "-x86_64", "/usr/bin/true").Run() == nil {
		return ""
	}
	return "GodsWar needs Rosetta on this Mac. Open Terminal, run:  softwareupdate --install-rosetta --agree-to-license  then open GodsWar again."
}

// optionHeld: the Option key is down while the launcher starts (no permission needed for the modifier state).
func optionHeld() bool {
	out, _ := exec.Command("/usr/bin/osascript", "-l", "JavaScript", "-e", `ObjC.import("AppKit"); ($.NSEvent.modifierFlags & 524288) ? "1" : "0"`).Output()
	return strings.TrimSpace(string(out)) == "1"
}

// askEngine shows the engine choice; a closed or timed-out dialog keeps the current choice (Wine when none).
func askEngine(current string) string {
	def := "Wine (recommended)"
	if current == EngineNative {
		def = "Native (Beta)"
	}
	msg := "How should GodsWar run on this Mac?\n\nWine (recommended): the tested way to play.\nNative (Beta): runs the game without Wine; still being tested.\n\nTo change this later, hold the Option key while opening GodsWar."
	out, err := exec.Command("/usr/bin/osascript", "-e", fmt.Sprintf(`button returned of (display dialog "%s" with title "GodsWar" buttons {"Native (Beta)", "Wine (recommended)"} default button "%s" giving up after 300)`, esc(msg), def)).Output()
	switch strings.TrimSpace(string(out)) {
	case "Native (Beta)":
		return EngineNative
	case "Wine (recommended)":
		return EngineWine
	}
	_ = err
	return current
}
