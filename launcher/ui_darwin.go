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
